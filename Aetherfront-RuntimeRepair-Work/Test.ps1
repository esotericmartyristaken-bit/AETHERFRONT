param([string]$GameRoot='C:\Users\jkell\Documents\Codex\APFBeta')
$ErrorActionPreference='Stop'
$controller=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$out=Join-Path $controller 'build/runtime-repair-candidate'
$plugins=Join-Path $GameRoot 'GameData/LunaMultiplayer/Plugins'
& dotnet build "$PSScriptRoot/Tests/Tests.csproj" -c Release "-p:Candidate=$out/code" "-p:Plugins=$plugins" -o "$out/tests" -v quiet
if($LASTEXITCODE -ne 0){throw 'Runtime repair tests did not compile'}
& "$out/tests/Tests.exe" $controller $GameRoot | Tee-Object -FilePath "$out/TESTS-CLR.txt"
if($LASTEXITCODE -ne 0){throw 'Runtime repair CLR tests failed'}
$env:APF_RUNTIME_CONTROLLER=$controller
$env:APF_RUNTIME_GAME=$GameRoot
$env:AETHERFRONT_KSP_TEST_ROOT=$GameRoot
& "$controller/build/MonoHost.exe" "$out/tests/Tests.exe" | Tee-Object -FilePath "$out/TESTS-MONO.txt"
if($LASTEXITCODE -ne 0){throw 'Runtime repair KSP Mono tests failed'}
