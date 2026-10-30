param([string]$GameRoot='C:\Users\jkell\Documents\Codex\APFBeta',[switch]$ReproduceBaseline)
$ErrorActionPreference='Stop'
$controller=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$repo=Join-Path $controller 'source'
$build=Join-Path $controller 'build'
$runtime=Join-Path $controller 'staging/PersonalServer'
function Run([string[]]$Arguments) {
 & dotnet @Arguments
 if($LASTEXITCODE -ne 0){throw "Clock build/test failed: $($Arguments[0])"}
}
Run @('build',"$repo/Aetherfront-RC35-Work/Server/AetherfrontContinuityServer.csproj",'-c','Release',"-p:ServerReference=$build/rc35-server-reference/Server.dll","-p:CandidateCore=$build/rc33-server/AetherfrontCore.dll","-p:ServerRuntime=$runtime",'-o',"$build/rc38-clock-server",'-v','quiet')
Run @('build',"$PSScriptRoot/ClockPatch/ClockPatch.csproj",'-c','Release',"-p:CecilPath=$build/rc30-version-patcher/Mono.Cecil.dll",'-o',"$build/rc38-clock-patcher",'-v','quiet')
Run @("$build/rc38-clock-patcher/ClockPatch.dll","$build/rc35-server/Server.dll","$build/rc38-clock-server/AetherfrontContinuityServer.dll","$build/rc38-clock-server/Server.dll")
Run @('build',"$repo/Aetherfront-RC35-Work/Client/AetherfrontContinuityClient.csproj",'-c','Release',"-p:ClientPlugins=$GameRoot/GameData/LunaMultiplayer/Plugins","-p:KspManaged=$GameRoot/KSP_x64_Data/Managed","-p:HarmonyPath=$GameRoot/GameData/000_Harmony/0Harmony.dll","-p:LifeSupportDll=$GameRoot/GameData/UmbraSpaceIndustries/LifeSupport/USILifeSupport.dll",'-o',"$build/rc38-clock-client",'-v','quiet')
Copy-Item -LiteralPath "$build/rc37-release-client/LmpClient.dll" -Destination "$build/rc38-clock-client/LmpClient.dll" -Force
Run @('build',"$PSScriptRoot/ClockBoundaryTests/ClockBoundaryTests.csproj",'-c','Release',"-p:Candidate=$build/rc38-clock-server","-p:Core=$build/rc33-server","-p:Runtime=$runtime",'-o',"$build/rc38-clock-boundary-tests",'-v','quiet')
$evidence=Join-Path $controller ('tests/rc38-clock-fixed-'+[Guid]::NewGuid().ToString('N').Substring(0,8))
& dotnet "$build/rc38-clock-boundary-tests/ClockBoundaryTests.dll" $evidence $runtime | Tee-Object -FilePath "$build/rc38-clock-server/BOUNDARY-TESTS.txt"
if($LASTEXITCODE -ne 0){throw 'Boundary regression failed'}
Run @('build',"$repo/Development/ContinuityServerTests/ContinuityServerTests.csproj",'-c','Release',"-p:Candidate=$build/rc38-clock-server","-p:Core=$build/rc33-server","-p:Runtime=$runtime",'-o',"$build/rc38-continuity-server-tests",'-v','quiet')
$evidence=Join-Path $controller ('tests/rc38-server-regressions-'+[Guid]::NewGuid().ToString('N').Substring(0,8))
& dotnet "$build/rc38-continuity-server-tests/ContinuityServerTests.dll" $evidence $runtime > "$build/rc38-clock-server/SERVER-REGRESSIONS.txt"
if($LASTEXITCODE -ne 0){throw 'Server continuity regressions failed'}
Run @('build',"$PSScriptRoot/ClockClientTests/ClockClientTests.csproj",'-c','Release',"-p:KspManaged=$GameRoot/KSP_x64_Data/Managed","-p:HarmonyPath=$GameRoot/GameData/000_Harmony/0Harmony.dll","-p:Candidate=$build/rc38-clock-client",'-o',"$build/rc38-clock-client-tests",'-v','quiet')
Run @('build',"$repo/Development/ContinuityMonoTests/ContinuityMonoTests.csproj",'-c','Release',"-p:KspManaged=$GameRoot/KSP_x64_Data/Managed","-p:GameRoot=$GameRoot","-p:HarmonyPath=$GameRoot/GameData/000_Harmony/0Harmony.dll","-p:Candidate=$build/rc38-clock-client",'-o',"$build/rc38-continuity-mono-tests",'-v','quiet')
$env:AETHERFRONT_KSP_TEST_ROOT=$GameRoot
$env:APF_CONTINUITY_CANDIDATE=Join-Path $build 'rc38-clock-client'
& "$build/MonoHost.exe" "$build/rc38-clock-client-tests/ClockClientTests.exe" | Tee-Object -FilePath "$build/rc38-clock-client/CLIENT-CLOCK-TESTS.txt"
if($LASTEXITCODE -ne 0){throw 'Client clock Mono regressions failed'}
& "$build/MonoHost.exe" "$build/rc38-continuity-mono-tests/ContinuityMonoTests.exe" | Tee-Object -FilePath "$build/rc38-clock-client/CONTINUITY-MONO-REGRESSIONS.txt"
if($LASTEXITCODE -ne 0){throw 'Client continuity Mono regressions failed'}
if($ReproduceBaseline){
 Run @('build',"$PSScriptRoot/ClockBoundaryTests/ClockBoundaryTests.csproj",'-c','Release',"-p:Candidate=$build/rc35-server","-p:Core=$build/rc33-server","-p:Runtime=$runtime",'-o',"$build/rc38-clock-baseline-test",'-v','quiet')
 $evidence=Join-Path $controller ('tests/rc38-clock-baseline-'+[Guid]::NewGuid().ToString('N').Substring(0,8))
 & dotnet "$build/rc38-clock-baseline-test/ClockBoundaryTests.dll" $evidence $runtime reproduce | Tee-Object -FilePath "$build/rc38-clock-server/BASELINE-REPRODUCTION.txt"
 if($LASTEXITCODE -ne 0){throw 'Baseline reproduction fixture failed'}
}
Write-Output 'Clock candidate only. No production process, state, keys, deployment pointer or installation was changed.'
