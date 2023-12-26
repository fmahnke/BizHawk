{
  #inputs = {
  #  nixpkgs.url = "github:nixos/nixpkgs/master";
  #};
  #inputs.nixpkgs.url = "nixpkgs/nixos-23.05";
  inputs.nixpkgs.url = "nixpkgs/nixos-unstable";
  #inputs.nixpkgs.url = "https://github.com/NixOS/nixpkgs/archive/23.11.tar.gz";

  outputs = { self, ... }@inputs:
    let
      pkgs = inputs.nixpkgs.legacyPackages.x86_64-linux;
    in
    with pkgs;
    {
      packages.x86_64-linux.default = with pkgs;
        (callPackage ./default.nix {
          dotnet-sdk_5 = (import (fetchTarball "https://github.com/NixOS/nixpkgs/archive/5234f4ce9340fffb705b908fff4896faeddb8a12^.tar.gz") {}).dotnet-sdk_5;
        }).emuhawk;

      hydraJobs.x86_64-linux = {
        inherit (self) packages;
      };

      devShells.x86_64-linux.default = pkgs.mkShell {
        nativeBuildInputs = with pkgs; [
          pkg-config
          dotnet-sdk
          mono
        ];

        buildInputs = with pkgs; [
          openal
          systemd
          SDL2
        ];
        #buildInputs = with python311.pkgs; [ manim ];
      };
    };
}
