﻿using BizHawk.Common;
using BizHawk.Emulation.Common;
using PeNet;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace BizHawk.Emulation.Cores.Waterbox
{
	/// <summary>
	/// represents one PE file.  used in PeRunner
	/// </summary>
	internal class PeWrapper : IImportResolver, IBinaryStateable, IDisposable
	{
		public Dictionary<int, IntPtr> ExportsByOrdinal { get; } = new Dictionary<int, IntPtr>();
		/// <summary>
		/// ordinal only exports will not show up in this list!
		/// </summary>
		public Dictionary<string, IntPtr> ExportsByName { get; } = new Dictionary<string, IntPtr>();

		public Dictionary<string, Dictionary<string, IntPtr>> ImportsByModule { get; } = new Dictionary<string, Dictionary<string, IntPtr>>();

		public string ModuleName { get; }

		private readonly byte[] _fileData;
		private readonly PeFile _pe;
		private readonly byte[] _fileHash;

		public ulong Size { get; }
		public ulong Start { get; private set; }

		public long LoadOffset { get; private set; }

		public MemoryBlock Memory { get; private set; }

		public IntPtr EntryPoint { get; private set; }

		/// <summary>
		/// for midipix-built PEs, pointer to the construtors to run during init
		/// </summary>
		public IntPtr CtorList { get; private set; }
		/// <summary>
		/// for midipix-build PEs, pointer to the destructors to run during fini
		/// </summary>
		public IntPtr DtorList { get; private set; }

		/*[UnmanagedFunctionPointer(CallingConvention.Winapi)]
		private delegate bool DllEntry(IntPtr instance, int reason, IntPtr reserved);
		[UnmanagedFunctionPointer(CallingConvention.Winapi)]
		private delegate void ExeEntry();*/
		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		private delegate void GlobalCtor();

		/*public bool RunDllEntry()
		{
			var entryThunk = (DllEntry)Marshal.GetDelegateForFunctionPointer(EntryPoint, typeof(DllEntry));
			return entryThunk(Z.US(Start), 1, IntPtr.Zero); // DLL_PROCESS_ATTACH
		}
		public void RunExeEntry()
		{
			var entryThunk = (ExeEntry)Marshal.GetDelegateForFunctionPointer(EntryPoint, typeof(ExeEntry));
			entryThunk();
		}*/
		public unsafe void RunGlobalCtors()
		{
			int did = 0;
			if (CtorList != IntPtr.Zero)
			{
				IntPtr* p = (IntPtr*)CtorList;
				IntPtr f;
				while ((f = *++p) != IntPtr.Zero) // skip 0th dummy pointer
				{
					var ctorThunk = (GlobalCtor)Marshal.GetDelegateForFunctionPointer(f, typeof(GlobalCtor));
					//Console.WriteLine(f);
					//System.Diagnostics.Debugger.Break();
					ctorThunk();
					did++;
				}
			}

			if (did > 0)
			{
				Console.WriteLine($"Did {did} global ctors for {ModuleName}");
			}
			else
			{
				Console.WriteLine($"Warn: no global ctors for {ModuleName}; possibly no C++?");
			}
		}

		public PeWrapper(string moduleName, byte[] fileData, ulong destAddress)
		{
			ModuleName = moduleName;
			_fileData = fileData;
			_pe = new PeFile(fileData);
			Size = _pe.ImageNtHeaders.OptionalHeader.SizeOfImage;

			if (Size < _pe.ImageSectionHeaders.Max(s => (ulong)s.VirtualSize + s.VirtualAddress))
			{
				throw new InvalidOperationException("Image not Big Enough");
			}

			_fileHash = WaterboxUtils.Hash(fileData);
			Mount(destAddress);
		}

		/// <summary>
		/// set memory protections.
		/// </summary>
		private void ProtectMemory()
		{
			Memory.Protect(Memory.Start, Memory.Size, MemoryBlock.Protection.R);

			foreach (var s in _pe.ImageSectionHeaders)
			{
				ulong start = Start + s.VirtualAddress;
				ulong length = s.VirtualSize;

				MemoryBlock.Protection prot;
				var r = (s.Characteristics & (uint)Constants.SectionFlags.IMAGE_SCN_MEM_READ) != 0;
				var w = (s.Characteristics & (uint)Constants.SectionFlags.IMAGE_SCN_MEM_WRITE) != 0;
				var x = (s.Characteristics & (uint)Constants.SectionFlags.IMAGE_SCN_MEM_EXECUTE) != 0;
				if (w && x)
				{
					throw new InvalidOperationException("Write and Execute not allowed");
				}

				prot = x ? MemoryBlock.Protection.RX : w ? MemoryBlock.Protection.RW : MemoryBlock.Protection.R;

				Memory.Protect(start, length, prot);
			}
		}

		/// <summary>
		/// load the PE into memory
		/// </summary>
		/// <param name="org">start address</param>
		private void Mount(ulong org)
		{
			Start = org;
			LoadOffset = (long)Start - (long)_pe.ImageNtHeaders.OptionalHeader.ImageBase;
			Memory = new MemoryBlock(Start, Size);
			Memory.Activate();
			Memory.Protect(Start, Size, MemoryBlock.Protection.RW);

			// copy headers
			Marshal.Copy(_fileData, 0, Z.US(Start), (int)_pe.ImageNtHeaders.OptionalHeader.SizeOfHeaders);

			// copy sections
			foreach (var s in _pe.ImageSectionHeaders)
			{
				ulong start = Start + s.VirtualAddress;
				ulong length = s.VirtualSize;
				ulong datalength = Math.Min(s.VirtualSize, s.SizeOfRawData);

				Marshal.Copy(_fileData, (int)s.PointerToRawData, Z.US(start), (int)datalength);
				WaterboxUtils.ZeroMemory(Z.US(start + datalength), (long)(length - datalength));
			}

			// apply relocations
			var n32 = 0;
			var n64 = 0;
			foreach (var rel in _pe.ImageRelocationDirectory)
			{
				foreach (var to in rel.TypeOffsets)
				{
					ulong address = Start + rel.VirtualAddress + to.Offset;

					switch (to.Type)
					{
						// there are many other types of relocation specified,
						// but the only that are used is 0 (does nothing), 3 (32 bit standard), 10 (64 bit standard)

						case 3: // IMAGE_REL_BASED_HIGHLOW
							{
								byte[] tmp = new byte[4];
								Marshal.Copy(Z.US(address), tmp, 0, 4);
								uint val = BitConverter.ToUInt32(tmp, 0);
								tmp = BitConverter.GetBytes((uint)(val + LoadOffset));
								Marshal.Copy(tmp, 0, Z.US(address), 4);
								n32++;
								break;
							}

						case 10: // IMAGE_REL_BASED_DIR64
							{
								byte[] tmp = new byte[8];
								Marshal.Copy(Z.US(address), tmp, 0, 8);
								long val = BitConverter.ToInt64(tmp, 0);
								tmp = BitConverter.GetBytes(val + LoadOffset);
								Marshal.Copy(tmp, 0, Z.US(address), 8);
								n64++;
								break;
							}
					}
				}
			}
			if (IntPtr.Size == 8 && n32 > 0)
			{
				// check mcmodel, etc
				throw new InvalidOperationException("32 bit relocations found in 64 bit dll!  This will fail.");
			}
			Console.WriteLine($"Processed {n32} 32 bit and {n64} 64 bit relocations");

			ProtectMemory();

			// publish exports
			EntryPoint = Z.US(Start + _pe.ImageNtHeaders.OptionalHeader.AddressOfEntryPoint);
			foreach (var export in _pe.ExportedFunctions)
			{
				if (export.Name != null)
					ExportsByName.Add(export.Name, Z.US(Start + export.Address));
				ExportsByOrdinal.Add(export.Ordinal, Z.US(Start + export.Address));
			}

			// collect information about imports
			// NB: Hints are not the same as Ordinals
			foreach (var import in _pe.ImportedFunctions)
			{
				Dictionary<string, IntPtr> module;
				if (!ImportsByModule.TryGetValue(import.DLL, out module))
				{
					module = new Dictionary<string, IntPtr>();
					ImportsByModule.Add(import.DLL, module);
				}
				module.Add(import.Name, Z.US(Start + import.Thunk));
			}

			var midipix = _pe.ImageSectionHeaders.Where(s => s.Name.SequenceEqual(Encoding.ASCII.GetBytes(".midipix")))
				.SingleOrDefault();
			if (midipix != null)
			{
				var dataOffset = midipix.PointerToRawData;
				CtorList = Z.SS(BitConverter.ToInt64(_fileData, (int)(dataOffset + 0x30)) + LoadOffset);
				DtorList = Z.SS(BitConverter.ToInt64(_fileData, (int)(dataOffset + 0x38)) + LoadOffset);
			}

			Console.WriteLine($"Mounted `{ModuleName}` @{Start:x16}");
			foreach (var s in _pe.ImageSectionHeaders.OrderBy(s => s.VirtualAddress))
			{
				var r = (s.Characteristics & (uint)Constants.SectionFlags.IMAGE_SCN_MEM_READ) != 0;
				var w = (s.Characteristics & (uint)Constants.SectionFlags.IMAGE_SCN_MEM_WRITE) != 0;
				var x = (s.Characteristics & (uint)Constants.SectionFlags.IMAGE_SCN_MEM_EXECUTE) != 0;
				Console.WriteLine("  @{0:x16} {1}{2}{3} `{4}` {5} bytes",
					Start + s.VirtualAddress,
					r ? "R" : " ",
					w ? "W" : " ",
					x ? "X" : " ",
					Encoding.ASCII.GetString(s.Name),
					s.VirtualSize);
			}
		}

		public IntPtr Resolve(string entryPoint)
		{
			IntPtr ret;
			ExportsByName.TryGetValue(entryPoint, out ret);
			return ret;
		}

		public void ConnectImports(string moduleName, IImportResolver module)
		{
			Dictionary<string, IntPtr> imports;
			if (ImportsByModule.TryGetValue(moduleName, out imports))
			{
				foreach (var kvp in imports)
				{
					var valueArray = new IntPtr[] { module.SafeResolve(kvp.Key) };
					Marshal.Copy(valueArray, 0, kvp.Value, 1);
				}
			}
		}

		private bool _disposed = false;

		public void Dispose()
		{
			if (!_disposed)
			{
				Memory.Dispose();
				Memory = null;
				_disposed = true;
			}
		}

		const ulong MAGIC = 0x420cccb1a2e17420;

		public void SaveStateBinary(BinaryWriter bw)
		{
			bw.Write(MAGIC);
			bw.Write(_fileHash);
			bw.Write(Start);

			foreach (var s in _pe.ImageSectionHeaders)
			{
				if ((s.Characteristics & (uint)Constants.SectionFlags.IMAGE_SCN_MEM_WRITE) == 0)
					continue;

				ulong start = Start + s.VirtualAddress;
				ulong length = s.VirtualSize;

				var ms = Memory.GetStream(start, length, false);
				bw.Write(length);
				ms.CopyTo(bw.BaseStream);
			}
		}

		public void LoadStateBinary(BinaryReader br)
		{
			if (br.ReadUInt64() != MAGIC)
				throw new InvalidOperationException("Magic not magic enough!");
			if (!br.ReadBytes(_fileHash.Length).SequenceEqual(_fileHash))
				throw new InvalidOperationException("Elf changed disguise!");
			if (br.ReadUInt64() != Start)
				throw new InvalidOperationException("Trickys elves moved on you!");

			Memory.Protect(Memory.Start, Memory.Size, MemoryBlock.Protection.RW);

			foreach (var s in _pe.ImageSectionHeaders)
			{
				if ((s.Characteristics & (uint)Constants.SectionFlags.IMAGE_SCN_MEM_WRITE) == 0)
					continue;

				ulong start = Start + s.VirtualAddress;
				ulong length = s.VirtualSize;

				if (br.ReadUInt64() != length)
					throw new InvalidOperationException("Unexpected section size for " + s.Name);

				var ms = Memory.GetStream(start, length, true);
				WaterboxUtils.CopySome(br.BaseStream, ms, (long)length);
			}

			ProtectMemory();
		}
	}
}
