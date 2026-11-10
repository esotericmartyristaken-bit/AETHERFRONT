#requires -Version 7.4
param([Parameter(Mandatory)][string]$Archive,[Parameter(Mandatory)][string]$ExpectedVersion,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{64}$')][string]$ExpectedSha256)
$ErrorActionPreference='Stop'
$file=[IO.FileStream]::new($Archive,'Open','Read','Read')
try {
    if([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($file)).ToLowerInvariant() -cne $ExpectedSha256){throw 'Approved archive differs'}
    & (Join-Path $PSScriptRoot 'Verify-KesasRelease.ps1') -Archive $Archive -ExpectedVersion $ExpectedVersion | Out-Null
    $file.Position=0
    $zip=[IO.Compression.ZipArchive]::new($file,[IO.Compression.ZipArchiveMode]::Read,$true)
    try {if($zip.GetEntry('AccountReset.json')){throw 'Account changes require separate operator authorization'}}finally{$zip.Dispose()}
} finally {$file.Dispose()}
'Verified '+$ExpectedVersion
