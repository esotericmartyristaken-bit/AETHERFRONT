param([string]$GameRoot = 'C:\Users\jkell\Documents\Codex\APFBeta')
$ErrorActionPreference='Stop'
$controller=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$build=Join-Path $controller 'build/rc38-presentation'
& "$PSScriptRoot/Build-Presentation.ps1" -GameRoot $GameRoot
& dotnet build "$PSScriptRoot/Tests/PresentationTests.csproj" -c Release "-p:KspManaged=$GameRoot/KSP_x64_Data/Managed" "-p:HarmonyPath=$GameRoot/GameData/000_Harmony/0Harmony.dll" "-p:Candidate=$build/code" -o "$build/tests" -v quiet
if($LASTEXITCODE -ne 0){throw 'Presentation test build failed'}
$env:AETHERFRONT_KSP_TEST_ROOT=$GameRoot
$env:APF_PRESENTATION_CANDIDATE=Join-Path $build 'code'
$env:APF_PRESENTATION_OVERLAY=Join-Path $build 'overlay'
$result=& "$controller/build/MonoHost.exe" "$build/tests/PresentationTests.exe" 2>&1
$result | Tee-Object -FilePath "$build/TEST-RESULTS.txt"
if($LASTEXITCODE -ne 0){throw 'Actual KSP Mono presentation tests failed'}
