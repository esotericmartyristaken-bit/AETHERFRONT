param([Parameter(Mandatory)][string]$Settings)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$configuration=Get-Content -LiteralPath $Settings -Raw|ConvertFrom-Json
$selected=Get-Content -LiteralPath (Join-Path $configuration.root 'native-cache/current-native.json') -Raw|ConvertFrom-Json
$directory=[IO.Path]::GetFullPath($selected.directory)
$cache=[IO.Path]::GetFullPath((Join-Path $configuration.root 'native-cache')).TrimEnd('\')+'\'
$seed=[IO.Path]::GetFullPath((Join-Path $configuration.root 'bootstrap/native'))
$managed=[IO.Path]::GetFullPath((Join-Path $configuration.root 'managed/releases')).TrimEnd('\')+'\'
$isManaged=$directory.StartsWith($managed,[StringComparison]::OrdinalIgnoreCase) -and $directory.Substring($managed.Length).Replace('\','/') -cmatch '^control-[1-9][0-9]{0,8}-[a-f0-9]{64}/KESAS-VPS-Server$'
if($directory -ine $seed -and -not $directory.StartsWith($cache,[StringComparison]::OrdinalIgnoreCase) -and -not $isManaged){throw 'Untrusted server launcher selection'}
$launcher=Join-Path $directory 'Start-Server.ps1'
for($cursor=$launcher;$cursor;$cursor=[IO.Path]::GetDirectoryName($cursor)){
    if([IO.File]::GetAttributes($cursor) -band [IO.FileAttributes]::ReparsePoint){throw 'Unsafe server launcher path'}
}
if($selected.sha256 -cnotmatch '^[a-f0-9]{64}$' -or (Get-FileHash -LiteralPath $launcher).Hash -ine $selected.sha256){throw 'Server launcher integrity mismatch'}
$stamp=Get-Date -Format 'yyyyMMdd-HHmmss-fff'
$permitPath=Join-Path $configuration.root 'native-cache/server-start-permit.json'
$publication=$false
if(Test-Path -LiteralPath $permitPath){
    if([IO.File]::GetAttributes($permitPath) -band [IO.FileAttributes]::ReparsePoint){throw 'Unsafe publication start permit'}
    $permit=Get-Content -LiteralPath $permitPath -Raw|ConvertFrom-Json
    $age=([DateTimeOffset]::UtcNow-([DateTimeOffset]$permit.createdAt).ToUniversalTime()).TotalSeconds
    $journalPath=Join-Path $configuration.nativeRoot '.update-transaction.json'
    $journalHash=if(Test-Path -LiteralPath $journalPath){(Get-FileHash -LiteralPath $journalPath).Hash.ToLowerInvariant()}else{$null}
    if($permit.schemaVersion -ne 1 -or $permit.purpose -cne 'native-publication-start' -or $permit.id -cnotmatch '^[a-f0-9]{32}$' -or $age -lt 0 -or $age -gt 60 -or [IO.Path]::GetFullPath($permit.nativeRoot) -ine [IO.Path]::GetFullPath($configuration.nativeRoot) -or $permit.launcherSha256 -cne $selected.sha256 -or $permit.journalSha256 -cne $journalHash){throw 'Publication start permit is stale or does not match native state'}
    [IO.File]::Move($permitPath,(Join-Path $configuration.root "native-cache/used-server-start-$($permit.id).json"))
    $publication=$true
}
if($publication){& $launcher -DeploymentRoot $configuration.nativeRoot -LauncherLog (Join-Path $configuration.nativeRoot "LauncherLogs/$stamp.log") -PublicationActivation}
else {& $launcher -DeploymentRoot $configuration.nativeRoot -LauncherLog (Join-Path $configuration.nativeRoot "LauncherLogs/$stamp.log")}
