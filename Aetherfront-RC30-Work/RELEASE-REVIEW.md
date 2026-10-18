# RC30 release review

RC29 failed to preserve generated preferences for Astrogator, Editor Extensions
Redux, Hangar, TweakScale, DeepFreeze, xScience and PlanetShine when their mod
packages were replaced. RC30 extends the exact mutable-path policy from eight
to sixteen audited files. DLLs, models, other configuration, and unrelated
PluginData remain immutable and continue to receive integrity verification.

The fifteen affected package roots receive version-only settings30 repairs.
Potato includes fourteen roots and omits PlanetShine. Archive bytes, hashes,
URLs and cache identities do not change. No complete mod archive is embedded
in the native bundle.

Already-loaded RC26 through RC29 helpers cannot adopt new code during apply.
RC30 retains the prepared28 transaction envelope, accepts authenticated legacy
completion receipts, and restores only exact preferences from the matching
newest authenticated replacement journal at early startup. A separate
LegacySettingsRecovery-rc30.json ledger preserves older recovery evidence.
New helpers record prepared30 preservation and bypass legacy restoration.
Interrupted recovery resumes without another game launch; completed recovery
never overwrites later user edits. No older journal fallback is permitted.

Prepared directory renames receive bounded retries for Windows denial/share
errors 5, 32 and 33, with path/reparse validation before every attempt. There
are at most six waits totaling 525 ms, no copy fallback or destination delete.
Tests cover transient and persistent handles, existing targets and junctions.

The signed RC29 baseline supplies 464 files. The independently reviewed graft
changes one client version getter and one constant field; all other client
methods and fields, the startup hook and ApfCore bytes remain unchanged. The
engine is compiled against the installed KSP framework and signed compression
assemblies. The release builder changes only two DLLs, catalog, client version
configuration and patch inventory. Production signing stays on the host.

Sandbox is an existing server-selected KSP mode. Host configuration maintenance
must preserve Science backups and use a distinct Sandbox client save namespace;
protected server Config/Universe must never be placed in native ServerPayload.
No server runtime changes are bundled.

Validation results and gameplay evidence are recorded separately. Synthetic
CLR/actual KSP Mono probes do not establish a fresh vanilla end-to-end install
or multi-Unity-client flight/physics stability. Inherited mod warnings are
reported separately from new updater failures.
