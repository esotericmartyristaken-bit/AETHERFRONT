param([string]$GameRoot='C:\Users\jkell\Documents\Codex\APFBeta')
$ErrorActionPreference='Stop'
$controller=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$out=Join-Path $controller 'build/rc38-expansion-bootstrap'
$null=[IO.Directory]::CreateDirectory($out)
$managed=Join-Path $GameRoot 'KSP_x64_Data/Managed'
$refs=@('mscorlib','System','System.Core','UnityEngine','UnityEngine.CoreModule','UnityEngine.IMGUIModule','Assembly-CSharp','System.IO.Compression','System.IO.Compression.FileSystem')|ForEach-Object {Join-Path $managed ($_+'.dll')}
$sources=Get-ChildItem -LiteralPath "$PSScriptRoot/Expansion" -Filter '*.cs' -File | Sort-Object Name | ForEach-Object FullName
$argsList=@('/nostdlib+','/target:library','/langversion:10','/optimize+','/debug-','/deterministic+',('/out:"'+$out+'\AetherfrontExpansionBootstrap.dll"'))
$argsList+=@($refs|ForEach-Object {'/reference:"'+$_+'"'})
$argsList+=@($sources|ForEach-Object {'"'+$_+'"'})
[IO.File]::WriteAllLines("$out/build.rsp",$argsList)
& dotnet 'C:/Program Files/dotnet/sdk/10.0.302/Roslyn/bincore/csc.dll' /noconfig "@$out/build.rsp"
if($LASTEXITCODE -ne 0){throw 'Expansion bootstrap build failed'}
& dotnet build "$PSScriptRoot/ExpansionTests/ExpansionRepairTests.csproj" -c Release -o "$out/tests" -v quiet
if($LASTEXITCODE -ne 0){throw 'Expansion test build failed'}
$env:APF_EXPANSION_TEST_OUTPUT=Join-Path $controller ('tests/rc38-expansion-'+[Guid]::NewGuid().ToString('N').Substring(0,8))
$env:AETHERFRONT_KSP_TEST_ROOT=$GameRoot
& "$controller/build/MonoHost.exe" "$out/tests/ExpansionRepairTests.exe" | Tee-Object -FilePath "$out/TEST-RESULTS.txt"
if($LASTEXITCODE -ne 0){throw 'Expansion recovery tests failed in actual KSP Mono'}
@{version='kesas-rc38';assemblySha256=(Get-FileHash "$out/AetherfrontExpansionBootstrap.dll").Hash.ToLowerInvariant();payloadHashesUnchanged=$true;repairBeforeStockLoader=$true;productionChanged=$false}|ConvertTo-Json|Set-Content "$out/DLC-BUILD.json"
