param([string]$GameRoot='C:\Users\jkell\Documents\Codex\APFBeta')
$ErrorActionPreference='Stop'
$controller=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$source=Join-Path $controller 'source'
$build=Join-Path $controller 'build'
& dotnet build "$source/Development/ProgressionPatch/ProgressionPatch.csproj" -c Release "-p:CecilPath=$build/rc30-version-patcher/Mono.Cecil.dll" -o "$build/rc39-release-tools" -v quiet
if($LASTEXITCODE -ne 0){throw 'Release version tool did not compile'}
& dotnet "$build/rc39-release-tools/ProgressionPatch.dll" release-version "$build/rc39-session-policy/patched/LmpClient.dll" "$build/rc39-release-client/LmpClient.dll" "$build/rc39-release-client/version-audit.json" "$GameRoot/KSP_x64_Data/Managed" kesas-rc38 kesas-rc39
if($LASTEXITCODE -ne 0){throw 'RC39 version graft failed'}
& python "$PSScriptRoot/Build-Release.py" $controller $GameRoot
if($LASTEXITCODE -ne 0){throw 'RC39 bundle assembly failed'}
