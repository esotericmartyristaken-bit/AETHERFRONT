# Aetherfront

Technical dependencies, provenance and exclusions: [Source provenance](SOURCE-PROVENANCE.md).
This source-only snapshot cannot independently reproduce the shipping client;
historical baseline binaries and separately licensed KSP/Unity dependencies are required.

Aetherfront is a Kerbal Space Program multiplayer fork with an in-game installer
and signed updates. This repository hosts source code and public download
releases. Current release: [kesas-rc27](https://github.com/esotericmartyristaken-bit/AETHERFRONT/releases/tag/kesas-rc27).
Use the starter asset, not the source-code ZIP or `pack-media` release.
Verification evidence and explicit limits are listed in [Testing](TESTING.md).

## Install

1. Start with a clean Windows KSP 1.12.5 installation that you own.
2. Download [Aetherfront-Starter-kesas-rc27.zip](https://github.com/esotericmartyristaken-bit/AETHERFRONT/releases/download/kesas-rc27/Aetherfront-Starter-kesas-rc27.zip).
3. Extract it beside `KSP_x64.exe`.
4. Launch KSP, select Regular or Potato, enter the server address, and connect.
5. Downloads, extraction and staging stay in-game. Review any requested mod
   removals, then click **Restart & apply** when preparation is complete.
6. KSP closes only after that explicit action. A hidden helper applies the
   prepared files and relaunches KSP; there is no desktop installer window.
   Verification can take several minutes. Do not manually launch another copy
   during apply. Connect again if needed.

If RC24 or an older client is already running, its loaded updater still handles
the initial migration. To bypass that legacy installer window, close KSP and
extract this starter beside `KSP_x64.exe`, not inside `Resources`, before
launching. Future updates use the in-game RC25 flow. Keep saves and client
identity files.

Existing pack files are repaired on Connect, not merely by extracting the
starter. Complete that update and restart before evaluating old pack warnings.

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

A completed installation at the same signed revision and selected profile uses
its authenticated receipt instead of scanning every installed file on Connect.
Failed or incomplete updates and profile changes cannot use that shortcut.
Client, pack and approved removals are planned together for one apply/restart.

KSP, stock/DLC game files, saves, player identities, private keys and publishing
credentials are not included. Third-party components retain their own licenses;
hosting permission does not transfer ownership or override those terms.
