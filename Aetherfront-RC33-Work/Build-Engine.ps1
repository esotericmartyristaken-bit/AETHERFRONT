#requires -Version 7.0
param([Parameter(Mandatory)][string]$Baseline,[Parameter(Mandatory)][string]$KspManaged,[Parameter(Mandatory)][string]$Compiler,[Parameter(Mandatory)][string]$Output,[ValidatePattern('^kesas-rc[1-9][0-9]*$')][string]$Version='kesas-rc33')
$ErrorActionPreference='Stop'
$Baseline=(Resolve-Path -LiteralPath $Baseline).Path
$KspManaged=(Resolve-Path -LiteralPath $KspManaged).Path
$Compiler=(Resolve-Path -LiteralPath $Compiler).Path
$Output=[IO.Path]::GetFullPath($Output)
$prior=Join-Path $PSScriptRoot '../Aetherfront-RC31-Work'
foreach($path in @($Baseline,$KspManaged,$Compiler,$Output)){
 for($cursor=$path;$cursor;$cursor=[IO.Path]::GetDirectoryName($cursor)){
  if((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Reparse build path refused'}
 }
}
$plugins=Join-Path $Baseline 'GameData/LunaMultiplayer/Plugins'
if((Get-FileHash -LiteralPath (Join-Path $plugins 'ApfCore.dll')).Hash -ine '0A4B6BD7C25EE4E18ABA88EC8D41CB66B8C0034DA3BA02D2D090DDB798BDC6C0'){throw 'Unreviewed core build reference'}
New-Item -ItemType Directory -Path $Output -Force | Out-Null
$generated=Join-Path $Output 'sources'
New-Item -ItemType Directory -Path $generated -Force | Out-Null
$legacy=Get-Content -Raw -LiteralPath (Join-Path $prior 'Engine/LegacySettingsRecovery.cs')
if(([regex]::Matches($legacy,'kesas-rc31')).Count -ne 1 -or ([regex]::Matches($legacy,'LegacySettingsRecovery-rc31')).Count -ne 1){throw 'Unexpected prior recovery source'}
$legacy=$legacy.Replace('kesas-rc31',$Version).Replace('LegacySettingsRecovery-rc31',('LegacySettingsRecovery-'+$Version.Substring(6))).Replace('completed RC31 release',('completed '+$Version.Substring(6).ToUpperInvariant()+' release'))
$replacement=Join-Path $generated 'LegacySettingsRecovery.cs'
[IO.File]::WriteAllText($replacement,$legacy)
$references=@(foreach($name in @('mscorlib','System','System.Core','System.Xml','System.Security')){Join-Path $KspManaged "$name.dll"})
$references+=@(foreach($name in @('System.IO.Compression','System.IO.Compression.FileSystem')){Join-Path $Baseline "KSP_x64_Data/Managed/$name.dll"})
$references+=Join-Path $plugins 'ApfCore.dll'
$sources=@(Get-ChildItem -LiteralPath (Join-Path $prior 'Engine') -Filter '*.cs' -File | Sort-Object Name | ForEach-Object {if($_.Name -ceq 'LegacySettingsRecovery.cs'){$replacement}else{$_.FullName}})
$sources+=@(foreach($name in @('Transfer.cs','DownloadFiles.cs','GitHub.cs')){Join-Path $prior "../Aetherfront-RC22-Work/Engine/$name"})
$before=@($references+$sources | ForEach-Object {@{path=[IO.Path]::GetFullPath($_);sha256=(Get-FileHash -LiteralPath $_).Hash}})
$arguments=@('/nostdlib+','/target:library','/langversion:10','/optimize+','/debug-','/deterministic+',('/out:"'+(Join-Path $Output 'KesasInstall.dll')+'"'))
$arguments+=@($references | ForEach-Object {'/reference:"'+$_+'"'})
$arguments+=@($sources | ForEach-Object {'"'+$_+'"'})
$response=Join-Path $Output 'build.rsp'
[IO.File]::WriteAllLines($response,$arguments)
& dotnet $Compiler /noconfig "@$response"
if($LASTEXITCODE -ne 0){throw 'RC33 KSP engine compilation failed'}
foreach($file in $before){if((Get-FileHash -LiteralPath $file.path).Hash -cne $file.sha256){throw 'Build inputs changed'}}
@{version=$Version;engineSha256=(Get-FileHash -LiteralPath (Join-Path $Output 'KesasInstall.dll')).Hash;preparedProtocol='prepared28';completionProtocol='prepared30';preservedPreferences=16;inputs=$before} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Output 'ENGINE-RESULT.json')
