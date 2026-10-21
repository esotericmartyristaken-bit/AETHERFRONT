# Authenticated additive public media (`media-reference-v1`)

Large public package archives can be uploaded separately from the application
bundle. The application transport remains limited to 512 MiB. Existing embedded
`MediaPayload/kesas-<slug>-<full-sha256>.zip` releases remain supported unchanged.
Neither mechanism permits ordinary packages to install into stock/protected
folders, including `SquadExpansion`. This stages public archives only; it does
not install DLC, deploy an application, or change client installer permissions.

## Controller command

Use the repository's Node/PowerShell 7 environment and existing local `gh`
authentication with push permission on the public repository. Only redistribute
archives that are authorized for public distribution. No credentials belong in
the reference file or ZIPs.

```powershell
node ControlPlane/media-controller.mjs publish --catalog C:/build/install-catalog.json --asset C:/build/kesas-makinghistory-<full-sha256>.zip --asset C:/build/kesas-serenity-<full-sha256>.zip --output C:/build/application/MediaReferences.json
```

Replace `<full-sha256>` with each archive's actual lowercase 64-character SHA256;
the basename must already contain that digest. `--asset` is repeatable. The
catalog must be the exact catalog included in this application's
`ClientTemplate.zip`, at
`GameData/LunaMultiplayer/Launcher/install-catalog.json`. Its `version`, enabled
`gitHub.repository` and `gitHub.mediaTag` must agree with the reference document.
The output directory must already exist.

The command validates every input, catalog binding and complete archive before
its first upload. It uploads without clobbering to only
`esotericmartyristaken-bit/AETHERFRONT`, tag `pack-media`. An absent media release
may be created with `make_latest=false`; an existing release must already be
published and non-prerelease. Existing assets require exact name, size, uploaded
state and GitHub SHA256 digest. Conflicts or missing digest fail closed. Successful
uploads are read back before the reference file is written. Local archive handles
deny writes/renames throughout scan/hash/upload on Windows. No application release,
`deploy` branch, application/control channel, signing key or latest-client pointer
is modified. It never calls the application publisher.

Retry identical inputs after an uncertain result. Existing identical assets and
an identical output file are accepted; changed output intent is rejected. Failed
uploads can leave immutable unreferenced assets. No delete, reset, replacement or
automatic application deployment is attempted. Error output is sanitized; raw
`gh` stderr and signed redirect URLs are not printed.

## Exact reference schema

`MediaReferences.json` is UTF-8 JSON, at most 1 MiB, with exactly these fields:

```json
{
  "schemaVersion": 1,
  "applicationVersion": "kesas-rc34",
  "repository": "esotericmartyristaken-bit/AETHERFRONT",
  "tag": "pack-media",
  "assets": [
    {
      "name": "kesas-makinghistory-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.zip",
      "sizeBytes": 261991749,
      "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
    }
  ]
}
```

The digest above is illustrative, not a release claim. Field names are
case-sensitive. No additional, duplicate, URL, destination, executable, token or
credential fields are accepted. `schemaVersion` and `sizeBytes` must be JSON
integers, not strings, fractions or exponent forms. `applicationVersion` must
match both `RELEASE.json.version` and the bundled catalog's `version`.

Each asset has exactly `name`, `sizeBytes`, `sha256`. Names are unique, at most
200 characters, and match `^kesas-[a-z0-9-]+-([a-f0-9]{64})\.zip$`; the captured
digest equals `sha256`. Existing public-name privacy restrictions still apply.
Sizes are positive and strictly smaller than 2 GiB (2,147,483,648 bytes), matching
the existing GitHub publisher bound. At most 1,000 assets and 8 GiB aggregate
compressed bytes may be referenced in one application. A filename cannot occur
in both `MediaPayload/` and the reference list.

Every reference must match at least one `full.managedPackages` or
`potato.managedPackages` declaration in that application's bundled catalog.
Every declaration referring to that filename must have the same exact filename,
lowercase SHA256 and integer `sizeBytes`. Catalog package URLs may be the existing
HTTPS pack-origin root path `/<name>` or the exact fixed GitHub media URL. Nested,
escaped, case-colliding or conflicting referring names/URLs are rejected. Catalog
URLs identify packages; they are never accepted as download targets.

## Application intent and verification

Include the reference file at the application ZIP root. Add its actual digest to
the existing `RELEASE.json.files` inventory, retaining all required files:

```json
{
  "version": "kesas-rc34",
  "files": [
    {"path": "ClientTemplate.zip", "sha256": "<actual-template-sha256>"},
    {"path": "APFPack-template.json", "sha256": "<actual-pack-template-sha256>"},
    {"path": "AetherfrontTechnology.json", "sha256": "<actual-technology-sha256>"},
    {"path": "MediaReferences.json", "sha256": "<actual-reference-file-sha256>"}
  ]
}
```

Use actual digests, not the placeholders. The normal immutable authenticated
application intent covers the whole application archive's size/SHA256; its file
inventory covers both references and `ClientTemplate.zip`. The publisher signs
the resulting client inventory containing that same catalog. A media hash, public
GitHub asset, reference document or unrelated local catalog alone is **not**
authorization to stage files. Do not invoke the native publisher on untrusted
archives outside its existing protected authenticated-request path.

`Verify-KesasRelease.ps1` independently checks the exact reference schema and
bindings before application queue admission/publication. It spools the verified
nested client template to private bounded disk rather than buffering a large
nested ZIP in RAM. `Publish-Release.ps1` repeats validation against the extracted
application catalog before any media fetch or activation.
Application publication `-DryRun`/`-WhatIf` remains network-free and makes no durable
writes; reference validation can create and remove this private temporary spool.

`Publish-GitHubDeployment.ps1` automatically adds `media-reference-v1` to schema-2
application intent requirements and host compatibility preflight whenever the
archive contains `MediaReferences.json`. Mixed bundles retain `media-payload-v1`.
The fixed privileged verifier and native publisher must both be installed from
the reviewed control/bootstrap transition before capability advertisement. Source
changes alone are not evidence of running host support. Runtime installation and
capability readback remain the parent host-restoration task; this change performs
neither installation nor application publication.

## Host transport, staging and limits

For a new reference, the host constructs only:

```text
https://github.com/esotericmartyristaken-bit/AETHERFRONT/releases/download/pack-media/<name>
```

Anonymous HTTPS uses no cookies/default credentials. Redirects are manual, at most
five, HTTPS port 443 only, with no userinfo or fragment. GitHub redirects must
remain the original exact URL; CDN targets are restricted to
`release-assets.githubusercontent.com`, `objects.githubusercontent.com` and
`github-releases.githubusercontent.com`. Signed CDN queries are allowed only on
those hosts. Download timeout is 30 minutes per asset, including streamed reads.
Both declared HTTP content length (when present) and actual streamed size must
match. Chunked or oversized responses cannot bypass the bound. SHA256 is computed
over the actual downloaded bytes. Buffers are at most 1 MiB.

New bytes land in private, ACL-protected per-application
`.release-staging/<application-guid>/Media/`, not a public incoming URL. Every
referenced archive then passes the complete `Assert-GhpPublicZip` scan, including
all entry contents, secret checks, Windows path checks, link/reparse rejection,
duplicate/case/file-directory collision checks and exact expanded lengths. The
media-specific expanded bound is 8 GiB per archive, with at most 10,000 entries.
The existing 512 MiB default public client/bootstrap ZIP scan is unchanged; no
application, template extraction or stock-folder safety limit is removed.

The entire media batch is validated before the first append. Activation is an
atomic same-volume move per new referenced archive into only
`Server/Config/<name>`, without overwrite. Existing identical size/SHA256 files
are fully scanned and reused without downloading; conflicting files, directories
or reparse paths fail closed. Concurrent append outcomes are accepted only for
identical bytes. The public-serving identity must already have access through
the existing native publisher/System/Administrators identities; staging ACLs are
not loosened.

Media activation is additive, not a multi-file rollback transaction. A later
publication failure may leave already appended valid archives, never replacements
for live files. Existing journals, service/idle guards, signing trust, application
activation and recovery behavior remain in charge. Uncommitted staging is removed
by the publisher's existing private application-staging cleanup.
