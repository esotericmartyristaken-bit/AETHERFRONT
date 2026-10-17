# Aetherfront

Aetherfront is a Kerbal Space Program multiplayer fork with an in-game installer
and signed updates. This repository hosts source code and public download
releases. Release publication is being initialized; use only a completed client
release, not the `pack-media` release, as an installation entry point.

## Install

1. Start with a clean Windows KSP 1.12.5 installation that you own.
2. Download the Aetherfront starter ZIP from the latest completed client release.
3. Extract it beside `KSP_x64.exe`.
4. Launch KSP, select Regular or Potato, enter the server address, and connect.
5. The installer verifies and downloads the selected pack, applies it after KSP
   exits, and restarts the game. Connect again if needed.

Server: `217.216.49.250`, game port `18865`. The starter includes the server's
public trust information; players do not need a GitHub account or access token.

Regular includes the graphical pack. Potato removes optional graphical content
while keeping compatible gameplay content. Profile changes are applied through
the same verified installer.

## Updates and hosting

Every connection checks for signed client updates. The selected server's active
signed version remains authoritative. Packages are immutable GitHub release
assets checked against their expected SHA256 and size. The original pinned
server remains available as a transport fallback.

KSP, stock/DLC game files, saves, player identities, private keys and publishing
credentials are not included. Third-party components retain their own licenses;
hosting permission does not transfer ownership or override those terms.
