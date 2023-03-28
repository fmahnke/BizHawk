#!/bin/sh
set -e
LLVM_TAG=llvmorg-16.0.0
LLVM_DIRS="cmake compiler-rt libunwind libcxx libcxxabi"
LLVM_PATH=../llvm-project
LLVM_GIT_DIR=$(git rev-parse --absolute-git-dir)/modules/waterbox/llvm-project

if [ -d "$LLVM_PATH" ]; then
	if [ ! -e "$LLVM_PATH/.git" ] || ! git -C "$LLVM_PATH" rev-parse $LLVM_TAG > /dev/null 2>&1; then
		rm -rf "$LLVM_PATH"
		git submodule deinit -f "$LLVM_PATH"
		rm -rf "$LLVM_GIT_DIR"
	fi
fi

if [ ! -d "$LLVM_PATH" ]; then
	git submodule init "$LLVM_PATH"
	git clone --separate-git-dir="$LLVM_GIT_DIR" --filter=tree:0 --sparse https://github.com/llvm/llvm-project.git "$LLVM_PATH"
fi

cd "$LLVM_PATH"
if [ `git describe` != $LLVM_TAG ] || ! ls $LLVM_DIRS > /dev/null 2>&1; then
	git checkout $LLVM_TAG && git sparse-checkout set $LLVM_DIRS
fi
