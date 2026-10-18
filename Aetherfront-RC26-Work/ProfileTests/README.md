# RC26 dirty existing-profile migration tests

Sources and output belong only to `Aetherfront-RC26-Work/ProfileTests`.
The runner does not build, modify, activate, publish, or sign product code.

`-ResumeFixture <absolute-owned-ProfileTests/Pxxxxxxxx>` is available only with
`-Run`. It requires a completed RC26 Potato receipt, the same signed candidate,
exact asset/policy evidence, installed frozen core/engine hashes, and the prior
successful old-helper proof with ten archives and one explicit restart. Applying
or failed migrations cannot be resumed through this path. The prior proof SHA256
is recorded in the continuation result; the successful upgrade is not repeated.

The reconnect fixture stages the signed feed and server manifest at the real
connection coordinator's `APF/.client-update-staging/connect` paths. Its positive
same-profile receipt and negative opposite-profile control test the receipt path,
not an accidental full-scan fallback caused by missing connection metadata.
NativeRun still verifies the complete selected manifest at every profile commit.

## Inputs and execution gate

Use the reviewed `Code-ce4097af3e0f4a5587c74cc1713fd210/CODE-RESULT.json`.
The executable pins Engine `be8a27a5f9a568fa938d98fa4ba4ad9b5e6f1f5a56772e58510a6773828741b0`
and scanner-grafted core `0a4b6bd7c25ee4e18aba88ec8d41cb66b8c0034da3ba02d2d090ddb798bdc6c0`.
A test run additionally requires the absolute directory of a complete, frozen,
production-signed RC26 candidate containing `Client` and `Published`.
No fixture is created by a compile-only invocation. Do not build or execute until
MAIN approves the source and final candidate. The retired `bfaf` build is explicitly
rejected by both the runner and executable.

The migration uses the final signed candidate directly. Both catalogs must match
the original RC25 manifests with only revision routing, four `-repair26` runtime
repair versions, the five exact reviewed AssetPlan declarations, and the explicitly
reviewed Potato-only policy replacement applied. Full has no policy replacement.
All other package fields remain identical. The initial Potato replacement
set is the exact union of those declarations (ten distinct packages),
not an unrestricted media exception and not a full-pack reinstall.

`-AssetStage` explicitly selects either the stage directory or its AssetPlan JSON.
`-AssetPlanSha256` and `-AssetTestResultSha256` are mandatory reviewed input hashes;
the test result defaults to `ASSET-TEST-RESULT.json` beside the plan, or can be set
with `-AssetTestResult`. The plan/report must cover exactly the five fixed package
IDs, seven byte-identical texture aliases and eight precise Static bindings, with
per-package preservation of all original entries and ZIP metadata. Archive SHA256,
URL, size, old declaration and profile mapping are bound to that exact plan.

Execution also requires explicit `-PotatoPolicyPlanPath` and
`-PotatoPolicyPlanSha256`. Compile-only can omit both and never creates a fixture.
The schema-1 RC26 Potato plan must be sealed in `ModFixes/Staged-RC26*`, bind the
signed RC25 baseline and exactly `kesas-potato-policy`, and preserve Full. Its
source, archive and passing non-synthetic MM evidence are hash-bound. Both actual
ZIPs are inspected: only the existing `ZZZ_KESASPotato/DisableExhaust.cfg` may
change; every other entry's bytes and metadata must remain identical. Installed
Potato bytes are checked against the source; Full must contain no policy root.

```powershell
& ./Aetherfront-RC26-Work/ProfileTests/Test-Profile.ps1 -CodeResult <absolute-CODE-RESULT.json> -AssetStage <absolute-ASSET-PLAN.json> -AssetPlanSha256 <reviewed-plan-hash> -AssetTestResultSha256 <reviewed-TestAssets-result-hash>

# Run only after MAIN confirms the candidate is frozen.
& ./Aetherfront-RC26-Work/ProfileTests/Test-Profile.ps1 -CodeResult <absolute-CODE-RESULT.json> -AssetStage <absolute-ASSET-PLAN.json> -AssetPlanSha256 <reviewed-plan-hash> -AssetTestResultSha256 <reviewed-TestAssets-result-hash> -PotatoPolicyPlanPath <absolute-POLICY-PLAN.json> -PotatoPolicyPlanSha256 <reviewed-policy-plan-hash> -CandidateRoot <absolute-final-Candidate> -Run

# Separate, optional long profile cycle; do not start automatically.
& ./Aetherfront-RC26-Work/ProfileTests/Test-Profile.ps1 -CodeResult <absolute-CODE-RESULT.json> -AssetStage <absolute-ASSET-PLAN.json> -AssetPlanSha256 <reviewed-plan-hash> -AssetTestResultSha256 <reviewed-TestAssets-result-hash> -PotatoPolicyPlanPath <absolute-POLICY-PLAN.json> -PotatoPolicyPlanSha256 <reviewed-policy-plan-hash> -CandidateRoot <absolute-final-Candidate> -Run -FullCycle
```

`-Runtime KSP-Mono` uses the existing exact KSP MonoHost. All runs use the exact
RC25 starter compression and numerics overlay. Offline compilation caches and
results remain under this test directory, not the user's home.

## What the default run proves

1. Clone the completed RC25 Potato177 baseline `P3e9e2e69` into a new owned short
   `ProfileTests/Pxxxxxxxx` fixture. No downloads or fresh 12 GB extraction.
   Immutable package bytes may be hardlinked. Client/bootstrap, state, settings,
   generated runtime paths, and editable configuration files are private copies.
   Every explicit test write unlinks an existing destination first.
2. Add private dirty runtime payloads at the observed ContractConfigurator singular
   `log`, Engineer root log/settings, KSPCF PNG/raw texture-cache, and performance
   cache paths. These payloads are synthetic; the package archive bytes are real.
3. Load the actual signed RC25 Engine/core in a separate private worker. Prepare
   and authenticate a combined real signed RC26 client update plus the exact union
   of four runtime repair archives, five reviewed additive media archives and the
   one explicit Potato-only policy archive: exactly ten initial replacements.
   Installed RC25 data remains unchanged until commit.
4. Commit through that already-loaded old helper. Assert successful target state,
   no hidden rollback, one explicit GameStub restart, and rejection of a second.
   The old helper's normal final scanner still verifies the entire signed profile.
5. Regenerate dirty files after migration. In the RC26 process, directly scan the
   four actual repaired roots with all runtime files exclusively locked. This
   specifically proves that the new scanner call site skips runtime files before
   opening/hashing them. The projected scanner assertion checks its install list,
   not extra roots outside that deliberately bounded manifest. A PluginData DLL
   negative control must still be detected.
6. Verify every installed addition's SHA256/size against the reviewed plan: seven
   texture aliases and the single config containing eight precise bindings. Original
   ZIP-entry byte/metadata preservation is attributed to the independently pinned
   passing TestAssets report; this harness does not claim to rerun that asset suite.
7. Assert the rich completed RC26 reconnect fast path with a private manual-addon
   sentinel, opposite-profile rejection, and preserved identity, saves and controls.
   Recheck original baseline metadata and every source file in all runtime/media
   replacement roots, not only the four runtime roots.
8. Verify the installed Potato policy's exact source bytes and package state;
   verify the policy package/root is absent from Full. MM behavioral correctness
   is attributed to the separately approved hash-bound MM evidence, not Unity.

## Optional long cycle and limitations

`-FullCycle` continues Potato177 to Regular/Full211 to Potato177 using real local
catalog archives, fresh private dirty runtime files before/after each transition,
the new RC26 helper, receipt checks and four-root scanner probes. It is opt-in
because each ordinary native final scanner may hash the complete existing profile.

This is a reused existing-profile, headless filesystem migration test, not a
fresh-stock install, a fresh full-pack extraction, a Unity gameplay test, or proof
that the mods can parse every synthetic runtime payload. No production RPC, live
game/client directory, service, release/version source, or original RC25 fixture
is a write target. Evidence is retained; the runner never kills test processes.
