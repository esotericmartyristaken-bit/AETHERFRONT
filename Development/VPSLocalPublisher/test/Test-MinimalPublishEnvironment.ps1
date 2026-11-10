#requires -Version 7.5
$ErrorActionPreference='Stop'
$source=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$oldPath=$env:PATH;$oldExtensions=$env:PATHEXT
try {
    $env:PATH='';$env:PATHEXT=$null
    . (Join-Path $source 'Aetherfront-RC44-Work/Deployment/Initialize-LocalPublishEnvironment.ps1')
    $git=Initialize-LocalPublishEnvironment
    $version=(& $git --version) -join '';if($LASTEXITCODE -ne 0 -or $version -notmatch '^git version '){throw 'Absolute Git failed under minimal task environment'}
    $env:PATH=[IO.Path]::GetDirectoryName($git)
    $discovered=(& git --version) -join '';if($LASTEXITCODE -ne 0 -or $discovered -cne $version){throw 'Child executable discovery failed'}
    [pscustomobject]@{status='passed';checks=2;git=$git;version=$version;extensions=$env:PATHEXT;networkAccessed=$false;productionModified=$false}
} finally {$env:PATH=$oldPath;$env:PATHEXT=$oldExtensions}
