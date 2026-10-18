# Tests against frozen shipping assemblies

These tests reference the authenticated handoff/client baseline assemblies instead
of silently compiling historical net6.0 server source. Do not copy their outputs
into a KSP or production server directory. Use a verified baseline and separate
build/obj output directories; client and server DLLs must remain separate.

* ShippingCoreTests targets .NET Framework 4.7.2 with TestPlugins pointing to the
  signed Windows client Plugins directory. It adapts only test junction creation
  from the historical core suite, which otherwise requires a .NET 6 API.
* ShippingDomainTests targets .NET 8 with TestRuntime pointing to the actual
  copied server runtime. Its historical 23 scenarios use an explicit end date
  for recurring catch-up objectives, as required by deployed validation. Added
  cases cover overflow, permissions, replay, atomic trade, vessel grants, gifts,
  lease expiry, donations and private faction chat. Legacy domain methods are
  not evidence that removed story commands are available over the server API.
* ShippingIdentityTests targets .NET 8 and the same actual server runtime. It
  tests the actual identity proof/challenge classes, including concurrent replay,
  expiration, wrong keys/account/name and request snapshotting.
* ShippingNetworkTests uses only IPAddress.Loopback UDP28865, disposable signing
  identities and an already running isolated server with empty initial Universe
  and keys. It modifies its disposable factions/state. Never point it at
  production. It tests three authenticated clients, current pack gates,
  factions, membership, roles, funds, alliances, PNG upload, trade, impersonation,
  replay, malformed JSON, phantom-craft denial and removed-story denial.
  --malformed-ingress first sends eight bounded unauthenticated malformed
  transport payloads and then requires the successful authenticated scenario.
  --seed-local-review <private-key-path> <player-name> is an explicit optional
  local UI fixture, using that client's existing identity without exporting it.
  The corresponding real client must be disconnected from the local server.

Example build: dotnet build Development/ShippingDomainTests -c Release
-p:TestRuntime=<verified-runtime> -o <isolated-test-output>. The other runtime
projects accept the same property; ShippingCoreTests uses TestPlugins.

Bot tests run with npm test under APFDiscordBot. On Windows, native-verifier.test.js
runs the release preflight regressions using the absolute built-in Windows
PowerShell path, without requiring pwsh on PATH. Test-KesasReleasePreflight.ps1
can also run separately with -TestRoot <new-fixture-directory>. Fixtures are
never extracted. The strengthened verifier accepts the actual immutable RC29
native bundle and rejects Windows-normalized paths, links, collisions, bad
hashes, undeclared entries and oversized expansion declarations.

The GitHub Windows workflow uses pinned action commits, read-only contents
permission, no persisted Git credentials, no deployment secrets, and only
synthetic bot/preflight tests and a dependency audit. It does not deploy or
claim a real Unity/production integration test passed.

The existing desktop RC28 installation was continued and upgraded to RC29 through
the signed updater. Production Potato and Regular joins, VAB/SPH/research,
unchanged reconnect and single-apply profile restart were observed. Runtime
warnings remain; passing critical startup gates does not mean error-free logs.
Actual two-Unity-client docking/flight stress and a completely fresh vanilla
bootstrap remain distinct checks and must never be inferred from protocol tests.
