param([string]$GameRoot='C:\Users\jkell\Documents\Codex\APFBeta')
$ErrorActionPreference='Stop'
$controller=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$build=Join-Path $controller 'build/rc39-session-policy'
$managed=Join-Path $GameRoot 'KSP_x64_Data/Managed'
$harmony=Join-Path $GameRoot 'GameData/000_Harmony/0Harmony.dll'
& dotnet build "$PSScriptRoot/Client/AetherfrontSessionPolicyClient.csproj" -c Release "-p:KspManaged=$managed" "-p:ClientPlugins=$GameRoot/GameData/LunaMultiplayer/Plugins" "-p:HarmonyPath=$harmony" -o $build -v quiet
if($LASTEXITCODE -ne 0){throw 'Session policy build failed'}
& dotnet build "$PSScriptRoot/Tests/SessionPolicyTests.csproj" -c Release "-p:KspManaged=$managed" "-p:HarmonyPath=$harmony" "-p:Candidate=$build" -o "$build/tests" -v quiet
if($LASTEXITCODE -ne 0){throw 'Session policy tests build failed'}
$env:AETHERFRONT_KSP_TEST_ROOT=$GameRoot
& "$controller/build/MonoHost.exe" "$build/tests/SessionPolicyTests.exe" | Tee-Object -FilePath "$build/TEST-RESULTS.txt"
if($LASTEXITCODE -ne 0){throw 'Session policy tests failed in KSP Mono'}
& dotnet build "$PSScriptRoot/NativePatch/NativePatch.csproj" -c Release "-p:CecilPath=$controller/build/rc30-version-patcher/Mono.Cecil.dll" -o "$build/patcher" -v quiet
if($LASTEXITCODE -ne 0){throw 'Native policy patcher build failed'}
& dotnet "$build/patcher/NativePatch.dll" "$controller/build/rc38-release-client/LmpClient.dll" "$build/AetherfrontSessionPolicyClient.dll" "$build/patched/LmpClient.dll" $GameRoot | Tee-Object -FilePath "$build/NATIVE-TEST-RESULTS.txt"
if($LASTEXITCODE -ne 0){throw 'Native policy gate audit failed'}
Write-Output 'RC39 local inputs only. No game install, server, GitHub branch, release, channel or version receipt changed.'
