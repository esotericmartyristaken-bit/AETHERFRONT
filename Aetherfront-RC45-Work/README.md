# RC45: ground clearance, local orbit tracking, and clock persistence

RC45 combines the launch-clearance and local kinematic-orbit repairs with the
RC44 mission-clock repair. RC44 is an immutable release: changed bytes must use
the new `kesas-rc45` identity, not replace an RC44 archive or tag. This document
describes the candidate and its evidence, not a claim of production activation.

## Ground-clearance evidence and scope

The reviewed incident contains failures across multiple craft and launch sites,
not one defective craft. Five failed unpacks each have an unsupported
closest-point warning immediately before a ground correction between 997.561 m
and 997.707 m. A successful launch instead has a 0.878 m correction without that
warning. No persistent water-launch mode was established by that evidence.

Native Unity 2019.4.18f1 fixtures reproduce the underlying clearance defect.
For three translated/sized non-convex mesh fixtures, the unsupported query
returns its probe unchanged. The shipping `Vessel.getLowestPoint` then reports
1000 m instead of the fixture's 1 m, 2 m, or 3 m extent. Replacing only that query
restores those geometry-derived values. This proves the false 1000 m clearance
mechanism feeding the ground-correction calculation; the incident's matching
approximately 997.6 m corrections are not an end-to-end replay of those flights.
The exact offending collider in each historical vessel was not captured.

[GroundClearance.cs](ClientFixes/GroundClearance.cs) replaces exactly one
`Collider.ClosestPoint(Vector3)` call inside `Vessel.getLowestPoint`. Zero or
multiple matches reject installation. Box, sphere, capsule, and convex mesh
queries retain Unity's original closest-point behavior for valid probes.
Non-convex meshes alone use a finite, nonempty current-world bounding-box
support point. The stock method has already rotated the actual vessel and
consumes the result's Z coordinate; using the lower Z bound follows its existing
layer-26/27/30 bounds heuristic. This is conservative clearance, not an exact
nearest point on a concave surface or an arbitrary-pose query replacement.

Missing/inactive unsupported colliders, invalid bounds, non-finite probes, and
unknown collider types reject the query rather than inventing a point. A real
world-origin support point remains valid when the bounds justify it. There is
no global Physics patch, ground-collision bypass, invincibility, launch-site
override, or persistent water-launch flag. The failure finalizer restores the
captured vessel rotation and preserves the exception instead of continuing
unpack after rejection.

The reviewed source is frozen at SHA256
`d34d31ae20049c316c2a759b1a8f38af39fc661ac61d415edc363dcb434f1ebc`.
Exceptional cleanup remains a limit: the finalizer does not clean up the stock
measurement object, and restoration after an earlier third-party prefix throws
before rotation capture has not been validated. No such co-patch was established
as the incident's cause.

## Local kinematic orbit tracking

The shipping LMP tracking path lacks the stock kinematic-rigidbody velocity
sampling guard. Native fixtures inject stale velocity during an unpacked-but-
kinematic transition and reproduce orbital-plane deviations of 94.258 degrees,
95.016 degrees, and 96.163 degrees. Selecting the stock callback preserves the
reference plane in those fixtures. These are controlled reproductions of the
roughly 90-degree failure, not measurements of a natural flight transition.

[LocalOrbitTracking.cs](ClientFixes/LocalOrbitTracking.cs) selects stock tracking
only for a running session's active, non-spectated vessel with a kinematic root
rigidbody, the local player's control lock, and no incoming position updates.
Dynamic, remote, spectated, foreign-lock, and queued-update cases retain the LMP
path. It does not change velocities, position queues, ownership, or universal
time. The helper types are grafted into the existing client assemblies; their
standalone build DLLs are not additional installed plugins.

## Tests and limits

The final native Unity graft run passed 43 cases / 918 checks against the actual
Candidate5 LmpClient and Candidate2 AetherfrontContinuityClient assemblies. It
executes the production GroundClearance.Install, production orbit prefix, and
native guard with actual ownership and queued-update resolution, including the
no-owner case. All 128 policy combinations retain their expected decisions.
Coverage also includes three ground geometries, supported collider behavior,
existing bounds layers, disabled/trigger exclusions, invalid bounds/probes, and
exact-one-call transpiler matching.

The tested LmpClient SHA256 is
`5441fc826e5861dcad99257640805386a8ae774f9f69c18f9c2876ec01a24799`;
the tested AetherfrontContinuityClient SHA256 is
`564cd10e83bc8b3bce298044ef9e54048616b7557651b678c1bd45519e893b58`.

This is native engine testing of the actual final grafts, not a natural-unpack,
full-flight, full-modpack, multiplayer, or end-to-end gameplay replay. Stale
tracking velocity is deliberately injected. Natural transition timing, all craft
shapes, third-party patch interactions, and rejection-path cleanup remain unproven.
Clock corrections near unpack do not by themselves establish their causal role;
RC45 does not change client TimeSync or make unpack a safe hard-time-step barrier.

The server clock helper is carried forward byte-for-byte with SHA256
`ff3a6c99ef8a8a076ca77c0344788f7f8b59110cdb959b81537e2c2bc745dea9`.
Its RC44 evidence remains 32 clock-store cases / 206 assertions and eight
isolated authenticated server cases / 103 assertions; these are inherited
results, not new RC45 flight tests. See [RC44](../Aetherfront-RC44-Work/README.md)
and [TESTING.md](../TESTING.md). The repair bounds eligible atomic-replacement
retries without resetting saved clocks or relaxing corruption/rollback checks.

## Build and local operator workflow

Build only from the pinned immutable RC44 archive. Use licensed local KSP
references and keep them, test captures, build output, credentials, and live
state outside Git. The helper projects target .NET Framework 4.7.2; the Cecil
patcher targets .NET 8. Supply `KspManaged`, `HarmonyPath`, `LmpClientPath`, and
`CecilPath` as applicable. The patcher also requires
`AETHERFRONT_KSP_MANAGED` and `AETHERFRONT_HARMONY_DIRECTORY`.

[Patch/Program.cs](Patch/Program.cs) accepts `ground BASELINE REPAIR OUTPUT` and
`orbit BASELINE REPAIR OUTPUT`. Review the emitted audits: pinned inputs,
preserved assembly identity, only the intended original methods/field changed,
and no leaked standalone helper dependency. [Build-Release.py](Build-Release.py)
accepts `--baseline`, `--ground`, `--client`, and `--output`; use a fresh output
directory. It checks the graft audits, unchanged server/transport payloads,
coordinated profiles, unchanged media archives, and the inherited clock hash.
Do not include private evidence or KSP decompiled source in the release.

The deployment entry point remains in the historically named
[RC44 Deployment directory](../Aetherfront-RC44-Work/Deployment/Deploy-LocalRelease.ps1).
For a separately authorized deployment:

1. Freeze the reviewed source commit, exact archive SHA256, and a new private
   report path. Run `APFDiscordBot/scripts/Verify-KesasRelease.ps1` with
   `-Archive` and `-ExpectedVersion kesas-rc45` before publication. This archive
   check is not a candidate-boot or activation permit.
2. Use the reviewed local `Deploy-LocalRelease.ps1` entry point with `-Archive`,
   `-ArchiveSha256`, `-SourceCommit`, and `-ReportPath`. It requires an already
   authorized elevated operator and disabled Git-triggered host deployment.
   It verifies the local commit and deployment code, freezes its operator
   bundle, and uses the configured local services. Do not use remote-host Git,
   a source push, or the retired Git-triggered publication path to deploy.
3. Before warning players or stopping production, the publisher boots and shuts
   down the exact prepared native candidate on isolated ports. The gate retains
   private evidence, verifies the signed client bytes and pinned loopback HTTPS,
   and checks that production processes and baseline files remain unchanged.
   World snapshots are per-file shared-read copies, not transactional backups.
   A passing candidate boot is not a KSP client join or launch test.
4. With players connected, admission delivers the full 15-minute warning with
   follow-up notices. Fresh authoritative zero player and connection counts
   allow the existing early-empty route. Missing/stale status, changed server
   identity, or failed required announcements block shutdown; there is no
   external active-player override. The candidate receipt and bound inputs are
   rechecked after admission, immediately before the production stop.
5. Accept completion only after matching pack/feed versions, the pinned health
   probe, and the local completion report. An upload, prepared archive, warning,
   or candidate receipt alone does not prove activation or successful gameplay.

These are operator instructions, not authorization to request UAC or deploy.
If elevation is unavailable or declined, stop; do not bypass it. Keep protected
configuration and credentials private. No remote credentials belong in commands,
reports committed to Git, or release notes.

## Failure and recovery

On any failed gate, retain the private report, receipts, prepared bytes, and
journals. Do not force admission, copy DLLs into live directories, edit clocks,
delete journals, or blindly restart/replay a publisher. Candidate failure before
the stop must leave the running production service alone.

Use a separately reviewed preactivation recovery only when its exact protected
plan and unchanged prior-release inventory prove that activation has not begun.
The reviewed recovery refuses an activation journal or published candidate,
checks publisher/process identity and lock ownership, and can request the fixed
server task only under its bounded no-service/no-listener conditions. Do not
assume that a recovery plan is armed merely because a deployment failed. An
ambiguous activation, changed inventory, live owner, or `needs-review` report
requires operator review, not a new recovery plan or a forced retry. A later
deployment still needs candidate preflight and normal player admission.
