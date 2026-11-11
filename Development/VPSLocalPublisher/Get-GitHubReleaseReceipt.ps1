#requires -Version 7.5
[CmdletBinding()]
param([Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{32}$')][string]$RequestId)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$repo='esotericmartyristaken-bit/AETHERFRONT'
$tag='apf-operator-inbox'
$gh=(Get-Command gh -CommandType Application | Select-Object -First 1).Source
$identity=& (Join-Path $PSScriptRoot 'Initialize-GitHubReleaseSigner.ps1')
$root=[IO.Path]::GetDirectoryName($identity.PublicKeyPath)
$expected=Get-Content -LiteralPath (Join-Path $root "submitted-$RequestId.json") -Raw | ConvertFrom-Json -DateKind String
$json=& $gh api "repos/$repo/releases/438071888"
if($LASTEXITCODE){throw 'GitHub inbox read failed'}
$release=$json|ConvertFrom-Json
if($release.id -ne 438071888 -or $release.tag_name -cne $tag -or !$release.draft){throw 'Unexpected GitHub inbox'}
$name="v3-receipt-$RequestId.json"
$assets=@($release.assets | Where-Object name -CEQ $name)
if(!$assets.Count){
    $status=if([DateTimeOffset]::Parse($expected.expiresAtUtc) -lt [DateTimeOffset]::UtcNow){'receipt-missing-after-request-expiry'}else{'waiting-for-receipt'}
    return [pscustomobject]@{requestId=$RequestId;status=$status;expiresAtUtc=$expected.expiresAtUtc}
}
if($assets.Count -ne 1 -or $assets[0].state -cne 'uploaded' -or $assets[0].size -lt 1 -or $assets[0].size -gt 4096 -or
    $assets[0].digest -cnotmatch '^sha256:[a-f0-9]{64}$'){throw 'Invalid GitHub receipt metadata'}
$temporary=Join-Path $root ('readback-'+[Guid]::NewGuid().ToString('N')+'.json')
try {
    & $gh release download $tag --repo $repo --pattern $name --output $temporary
    if($LASTEXITCODE){throw 'GitHub receipt download failed'}
    if((Get-Item -LiteralPath $temporary).Length -ne $assets[0].size -or
        ('sha256:'+(Get-FileHash -LiteralPath $temporary).Hash.ToLowerInvariant()) -cne $assets[0].digest){
        throw 'Downloaded receipt differs from GitHub metadata'
    }
    $receipt=Get-Content -LiteralPath $temporary -Raw|ConvertFrom-Json -DateKind String
    foreach($key in @('requestId','version','action','sourceCommit','archiveSha256')){
        if($receipt.$key -cne $expected.$key){throw 'Receipt identity differs from the submitted job'}
    }
    if($receipt.schemaVersion -ne 1 -or $receipt.host -cne 'vmi3254434' -or
        $receipt.status -cnotin @('verified','healthy','failed','rejected','interrupted')){
        throw 'Invalid release receipt'
    }
    $receipt | Select-Object requestId,action,version,status,stage,code,activeVersion,observedAtUtc
} finally {[IO.File]::Delete($temporary)}
