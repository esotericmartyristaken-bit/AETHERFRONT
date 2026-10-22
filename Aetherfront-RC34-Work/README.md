# RC34 expansion delivery and editor camera

This release adds Making History, Breaking Ground and WASD Editor Camera
Continued 0.7.3.3 to both Regular and Potato. DLC redistribution was explicitly
authorized by the user. Licensed assets and KSP assemblies are build inputs;
they are not source-repository files. The existing Sandbox mode, server binaries,
state, identities and signing keys are preserved.

Ordinary pack and signed overlay validators continue protecting SquadExpansion.
The signed ExpansionBootstrap addon pins two immutable licensed data archives
inside normal managed payload roots. KSPAddon.Instantly runs before GameDatabase
constructs its UrlDir; the bootstrap installs complete missing DLC directories
atomically at their canonical paths before stock expansion loading. Original
KSP expansion signature verification remains intact. Executable entries,
traversal, links, reparse paths, size violations and different pre-existing DLCs
are refused. Identical manual copies are preserved and adopted through receipts.
Timestamp/size receipts avoid hashing all assets on every startup. These local
receipts are a performance cache, not proof against a malicious modified client.

Build-Expansions.ps1 compiles against explicit actual KSP Mono references and
embeds the SHA256 of each compressed licensed input. Build-Release.py verifies
the signed RC33 baseline, client-version method graft and narrowly retargeted
legacy recovery Engine before assembling RC34. It adds only the new addon,
version/catalog changes and the unchanged-base inventory. Use the existing
Build-Engine.ps1 with -Version kesas-rc34 and ProgressionPatch's release-version /
engine-review commands. Never replace a complete historical server build.

Prepare writes a small Native/ClientTemplate.zip plus three separate media ZIPs.
Publish those through ControlPlane/media-controller.mjs with the exact prepared
install-catalog.json and output Native/MediaReferences.json. Finalize validates
the returned immutable reference bindings and creates the small native release.
Normal Publish-HostRelease.ps1 preflights media-reference-v1 and retains guarded
activation, TLS trust and host-held client signing. Large media never needs a
manual host copy or a bypass of the native application size bound.

Compiler/method/inventory/signature checks were performed. Additional gameplay,
clean-install and automatic DLC bootstrap runs were not performed because the
user explicitly instructed publication without further testing. Manual DLC
installation into the user's existing client is separate evidence from this
new delivery path. No gameplay pass should be inferred from build success.

The server remains Sandbox. Regenerate the science catalog from a DLC-loaded
runtime before a later switch to Science; this release does not claim verified
progression for new DLC experiments.