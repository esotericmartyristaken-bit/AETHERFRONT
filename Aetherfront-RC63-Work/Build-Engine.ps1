#requires -Version 7.5
param([Parameter(Mandatory)][string]$Output)
$ErrorActionPreference='Stop'
$source=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$baseline='C:\Users\Administrator\Desktop\APF\UpdateBuilds\RC56-Sync-Fixture\ClientUpdate-20261113-ValidatedC\Baseline'
$managed='C:\Users\Administrator\Desktop\APF\Aetherfront-KSP-Smoke\KSP_x64_Data\Managed'
$plugins='C:\Users\Administrator\Desktop\APF\UpdateBuilds\RC56-Sync-Fixture\RC62-Final\Combined'
$sdk='C:\Users\Administrator\Desktop\APF\tools\dotnet-sdk8\dotnet.exe'
$compiler='C:\Users\Administrator\Desktop\APF\tools\dotnet-sdk8\sdk\8.0.424\Roslyn\bincore\csc.dll'
if(Test-Path -LiteralPath $Output){throw 'Use a new exact-reference engine output'}
$null=New-Item -ItemType Directory -Path $Output
$references=@(foreach($name in @('mscorlib','System','System.Core','System.Xml','System.Security')){Join-Path $managed "$name.dll"})
$references+=@(foreach($name in @('System.IO.Compression','System.IO.Compression.FileSystem')){Join-Path $baseline "KSP_x64_Data/Managed/$name.dll"})
$references+=Join-Path $plugins 'ApfCore.dll'
$sources=@(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'Engine') -Filter '*.cs' -File|Select-Object -ExpandProperty FullName)
$sources+=@(Get-ChildItem -LiteralPath (Join-Path $source 'Aetherfront-RC31-Work/Engine') -Filter '*.cs' -File|Where-Object Name -NotIn @('Installer.cs','NativeInstall.cs','PreparedPack.cs','LegacySettingsRecovery.cs','PostExit.cs')|Select-Object -ExpandProperty FullName)
$sources+=@(foreach($relative in @('Aetherfront-RC28-Work/Engine/IncrementalInstall.cs','Aetherfront-RC28-Work/Engine/DeltaClientFiles.cs','Aetherfront-RuntimeRepair-Work/Hook/RuntimeAssemblyRepair.cs','Aetherfront-RC22-Work/Engine/Transfer.cs','Aetherfront-RC22-Work/Engine/DownloadFiles.cs','Aetherfront-RC22-Work/Engine/GitHub.cs')){Join-Path $source $relative})
$inputs=@($sources+$references|ForEach-Object{@{path=$_;sha256=(Get-FileHash -LiteralPath $_).Hash.ToLowerInvariant()}})
$arguments=@('/noconfig','/nostdlib+','/target:library','/langversion:10','/optimize+','/debug-','/deterministic+',('/out:'+(Join-Path $Output 'KesasInstall.dll')))+@($references|ForEach-Object{'/reference:'+$_})+$sources
& $sdk $compiler @arguments
if($LASTEXITCODE){throw 'Exact KSP-reference installer build failed'}
foreach($row in $inputs){if((Get-FileHash -LiteralPath $row.path).Hash.ToLowerInvariant() -cne $row.sha256){throw 'Engine build input changed'}}
$proof=@{atUtc=[DateTime]::UtcNow.ToString('o');engineSha256=(Get-FileHash -LiteralPath (Join-Path $Output 'KesasInstall.dll')).Hash.ToLowerInvariant();inputs=$inputs}
[IO.File]::WriteAllText((Join-Path $Output 'Build.json'),($proof|ConvertTo-Json -Depth 6))
