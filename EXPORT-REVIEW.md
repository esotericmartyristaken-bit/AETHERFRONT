# Publication review

Status: reviewed source export for publication. The export itself performs no Git initialization, authentication, push, release publication, game launch or service/process changes. These are separate authorized operator actions.

Before integration:

1. Require a finalized `SOURCE-MANIFEST.json` and a zero-finding `PRIVACY-SCAN.json`; rerun the exporter with `-VerifyOnly`.
2. Review allowlisted source paths, any recorded transformations, exact dependency inventories and excluded binary prerequisites.
3. Confirm that examples/test credentials are non-production placeholders. The historical credential-bearing client handoff is excluded entirely.
4. Keep upstream/file-level notices and the LGPL text. Resolve shipping baseline source correspondence; do not claim a complete shipping rebuild.
5. Review retained deployment/publisher/test scripts without executing them against a running environment.
6. Integrate into a normal checkout only after human review. The staged README may replace the initial repository README. Publication/authentication remain separate work.

## Final refresh workflow

Staging can be prepared before a release finishes. After publisher, recovery and profile-test source edits are ready:

1. Add any new reviewed source file/directory paths to `export-policy.json`. Only named source extensions are copied; generated fixtures remain excluded. Do not add release payloads or live configuration.
2. Run `Aetherfront-RC22-Work/Export-GitHubSource.ps1 -WorkspaceRoot $PWD -Refresh` from the original workspace. This updates exported source copies and regenerates fingerprints, dependencies, the privacy report and source manifest. Original files remain untouched.
3. Run `Aetherfront-GitHub-Source/scripts/Test-SourceStage.ps1 -WorkspaceRoot $PWD -CheckOriginalSources`. Review any drift before integration.
4. If the main repository README becomes an installation guide, keep this staging README as `SOURCE-PROVENANCE.md`. That destination is allowlisted for metadata; other new metadata paths require an explicit `metadataFiles` entry. Run the exporter with `-RefreshMetadata` to rescan/rehash the changed documents without changing copied source.
5. Run the exporter with `-VerifyOnly`. Only then copy reviewed staging into the normal checkout. Any later source/document edits require another appropriate refresh and scan.

Default repeat runs are verification-only. Explicit refresh never publishes, authenticates or invokes retained build/test/deployment scripts. The final reviewer clears `concurrentSourceReviewPending` only after original-source drift, privacy and selected source areas have been checked; new edits require another refresh.

The available full fork's provenance is supported by the old handoff and sibling upstream Git metadata, not by a clean commit for all later modifications. The deployed server assembly is fingerprinted only; equivalence to the staged older source has not been established. Main RC22 work may change concurrently after this snapshot: compare source hashes before final integration. Do not overwrite its release artifacts or scripts.
