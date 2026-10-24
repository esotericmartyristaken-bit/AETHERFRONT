# RC37: restore normal RC34-to-current preparation

Connecting an RC34 client to RC36 downloaded the signed client but stopped
preparation before apply. The release builder declared every added mod in both
`managedPackages` and `clientOnly` in its Regular/Potato catalogs. The shipped
validator correctly rejected 10 overlaps in Regular and 7 in Potato.

Catalog profiles now declare these roots only as managed downloads. The server
manifest retains its exceptions because it intentionally has no managed
downloads. `AetherfrontCompatibility`, supplied in the signed client overlay,
remains a client-only exception. No package, hash, URL or profile content changes;
the same ten immutable media archives are reused. Existing failed preparation
can retry normally; no install reset or manual DLL replacement is necessary.

`Development/ReleaseCatalogTests` uses the exact shipped ApfCore and installer
types. It reproduces both RC36 failures, validates both RC37 profiles and the
server manifest, and requires an injected overlap to remain rejected. The
corrected profiles passed on Windows CLR and actual KSP Mono. Finalization now
requires a successful validation receipt matching the exact catalog and server
manifest hashes, and confirms the same catalog is inside the signed template.

The 477-file exact template also passed disposable native signing, verification
and signed-inventory comparison. The native bundle passed Verify-KesasRelease.
Gameplay/server code is unchanged; only the catalog, release version and recovery
markers differ. This focused validation does not assert a completed live Unity
apply/restart/join; that must be recorded separately after publication.
