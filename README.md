# Aetherfront

Technical dependencies, provenance and exclusions: [Source provenance](SOURCE-PROVENANCE.md).
This source-only snapshot cannot independently reproduce the shipping client;
historical baseline binaries and separately licensed KSP/Unity dependencies are required.

Aetherfront is a Kerbal Space Program multiplayer fork with an in-game installer
and signed updates. This repository hosts source code and public download
releases. Current release: [kesas-rc24](https://github.com/esotericmartyristaken-bit/AETHERFRONT/releases/tag/kesas-rc24).
Use the starter asset, not the source-code ZIP or `pack-media` release.
Verification evidence and explicit limits are listed in [Testing](TESTING.md).

## Install

1. Start with a clean Windows KSP 1.12.5 installation that you own.
2. Download [Aetherfront-Starter-kesas-rc24.zip](https://github.com/esotericmartyristaken-bit/AETHERFRONT/releases/download/kesas-rc24/Aetherfront-Starter-kesas-rc24.zip).
3. Extract it beside `KSP_x64.exe`.
4. Launch KSP, select Regular or Potato, enter the server address, and connect.
5. After downloads finish, KSP closes and a separate installation window shows
   progress. Keep KSP closed while files are being installed and verified.
6. Click **Restart KSP** when installation is complete. The game does not restart
   automatically. Connect again if needed. An older updater can still restart
   automatically once while migrating its client to this version.

Unrelated-mod removal still requires approval. Approved removed mods are
permanently deleted, not backed up. Replaced managed files retain failure
recovery; saves, identity and controls are preserved.

Server: `217.216.49.250`, game port `18865`. The starter includes the server's
public trust information; players do not need a GitHub account or access token.

Regular includes the graphical pack. Potato removes optional graphical content
while keeping compatible gameplay content. Profile changes are applied through
the same verified installer. Potato uses the author's official Kcalbeloh 4K
texture archive; Regular retains 8K. All 42 terrain-height and biome maps are
byte-identical between those variants. No custom texture resizing is distributed.

## Updates and hosting

Every connection checks for signed client updates. The selected server's active
signed version remains authoritative. Packages are immutable GitHub release
assets checked against their expected SHA256 and size. The original pinned
server remains available as a transport fallback.

KSP, stock/DLC game files, saves, player identities, private keys and publishing
credentials are not included. Third-party components retain their own licenses;
hosting permission does not transfer ownership or override those terms.
