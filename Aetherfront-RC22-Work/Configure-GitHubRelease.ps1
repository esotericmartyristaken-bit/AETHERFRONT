param([Parameter(Mandatory=$true)][string]$Repository,[string]$MediaTag='pack-media',[switch]$Apply)
$ErrorActionPreference='Stop'
if($Repository -cnotmatch '^[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9_.-]{1,100}$' -or $Repository -match '/\.\.?$'){throw 'Use owner/repository, not a URL or token.'}
if($MediaTag -cnotmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$' -or $MediaTag-eq 'latest' -or $MediaTag-like 'kesas-rc*'){throw 'Invalid media release tag.'}
$work=[IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')
$server=[IO.Path]::GetFullPath("$work/../KESAS-VPS-Server")
$feed=Get-Content -LiteralPath "$server/Updates/APFClientUpdate.json" -Raw|ConvertFrom-Json
if($feed.version-ne 'kesas-rc21'){throw 'Live version changed; review the release base before rebuilding RC22.'}
if(Test-Path -LiteralPath "$server/Updates/kesas-rc22"){throw 'RC22 already exists on the server; never replace published artifacts.'}
if(-not $Apply){Write-Output "Ready to build an unpublished RC22 for $Repository. No files or services changed. Use -Apply after repository/access setup.";exit 0}
$backup=Join-Path $work ('TestArtifacts/before-github-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N'))
$null=[IO.Directory]::CreateDirectory($backup)
foreach($name in @('Release','Candidate','Aetherfront-Release-kesas-rc22.zip')){
    $path=[IO.Path]::GetFullPath((Join-Path $work $name))
    if(-not $path.StartsWith($work+'\',[StringComparison]::OrdinalIgnoreCase)-or $path-eq $work){throw 'Unsafe unpublished-artifact move.'}
    if(Test-Path -LiteralPath $path){Move-Item -LiteralPath $path -Destination (Join-Path $backup $name)}
}
& "$work/Build-Release.ps1" -GitHubRepository $Repository -GitHubMediaTag $MediaTag
if(-not $?){throw 'Configured release build failed; prior test artifacts remain recoverable.'}
& "$work/Prepare-Candidate.ps1"
if(-not $?){throw 'Configured candidate verification failed.'}
Write-Output "Configured and signed RC22 for $Repository. No upload, restart or activation performed. Reverify this candidate, then use normal staged deployment. Prior test artifacts: $backup"
