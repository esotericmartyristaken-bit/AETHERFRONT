param([string]$GameRoot='C:\Users\jkell\Documents\Codex\APFBeta')
$ErrorActionPreference='Stop'
$controller=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$out=Join-Path $controller 'build/runtime-repair-candidate'
$plugins=Join-Path $GameRoot 'GameData/LunaMultiplayer/Plugins'
$cecil='C:/Users/jkell/.nuget/packages/mono.cecil/0.11.6/lib/netstandard2.0/Mono.Cecil.dll'
$null=[IO.Directory]::CreateDirectory($out)
& dotnet build "$PSScriptRoot/Hook/Hook.csproj" -c Release "-p:Plugins=$plugins" -o "$out/hooks" -v quiet
if($LASTEXITCODE -ne 0){throw 'Runtime repair hook compilation failed'}
& dotnet build "$PSScriptRoot/Patch/Patch.csproj" -c Release "-p:CecilPath=$cecil" -o "$out/patcher" -v quiet
if($LASTEXITCODE -ne 0){throw 'Runtime repair patcher compilation failed'}
& dotnet "$out/patcher/Patch.dll" "$plugins/KesasInstall.dll" "$plugins/AetherfrontContinuityClient.dll" "$out/hooks/RuntimeRepairHooks.dll" "$out/code"
if($LASTEXITCODE -ne 0){throw 'Runtime repair native graft failed'}
& python "$PSScriptRoot/Package.py" $controller $GameRoot
if($LASTEXITCODE -ne 0){throw 'Runtime repair package failed'}
