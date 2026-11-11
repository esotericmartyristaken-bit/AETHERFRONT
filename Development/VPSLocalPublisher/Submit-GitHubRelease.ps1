#requires -Version 7.5
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][string]$Archive,
    [Parameter(Mandatory)][ValidatePattern('^kesas-rc[1-9][0-9]{0,8}$')][string]$Version,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{40}$')][string]$SourceCommit,
    [Parameter(Mandatory)][ValidateSet('verify','publish')][string]$Action
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$repo='esotericmartyristaken-bit/AETHERFRONT'
$releaseId=438071888
$draftTag='apf-operator-inbox'
$gh=(Get-Command gh -CommandType Application | Select-Object -First 1).Source
$node=(Get-Command node -CommandType Application | Select-Object -First 1).Source
$path=(Resolve-Path -LiteralPath $Archive).Path

function Read-Draft {
    $response=& $gh api "repos/$repo/releases/$releaseId"
    if($LASTEXITCODE -ne 0){throw 'GitHub inbox could not be read'}
    $draft=$response | ConvertFrom-Json
    if($draft.id -ne $releaseId -or $draft.tag_name -cne $draftTag -or $draft.draft -ne $true){
        throw 'Unexpected GitHub release inbox'
    }
    return $draft
}

function Send-Asset([string]$Name,[string]$AssetPath,[string]$Type){
    $size=(Get-Item -LiteralPath $AssetPath).Length
    $digest='sha256:'+(Get-FileHash -LiteralPath $AssetPath).Hash.ToLowerInvariant()
    $found=@((Read-Draft).assets | Where-Object name -CEQ $Name)
    if(!$found.Count){
        $url="https://uploads.github.com/repos/$repo/releases/$releaseId/assets?name=$Name"
        $json=& $gh api --method POST -H "Content-Type: $Type" --input $AssetPath $url
        if($LASTEXITCODE -ne 0){throw "GitHub could not upload $Name"}
        $found=@($json | ConvertFrom-Json)
    }
    if($found.Count -ne 1 -or $found[0].name -cne $Name -or $found[0].state -cne 'uploaded' -or
        $found[0].size -ne $size -or $found[0].digest -cne $digest){throw "Unexpected bytes for $Name"}
}

$stream=[IO.FileStream]::new($path,'Open','Read','Read')
try {
    $sha=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant()
    & (Join-Path $PSScriptRoot '../../APFDiscordBot/scripts/Verify-KesasRelease.ps1') -Archive $path -ExpectedVersion $Version | Out-Null
    $null=Read-Draft
    if(!$PSCmdlet.ShouldProcess($draftTag,"Submit $Action for $Version")){return}
    Send-Asset "$sha.zip" $path 'application/zip'
    $identity=& (Join-Path $PSScriptRoot 'Initialize-GitHubReleaseSigner.ps1')
    $signerDir=[IO.Path]::GetDirectoryName($identity.PublicKeyPath)
    $privatePath=Join-Path $signerDir 'controller-key.dpapi'
    $publicHash=& $node -e 'const c=require("node:crypto");const k=c.createPublicKey(require("node:fs").readFileSync(process.argv[1]));process.stdout.write(c.createHash("sha256").update(k.export({type:"spki",format:"der"})).digest("hex"))' $identity.PublicKeyPath
    if($LASTEXITCODE -ne 0 -or $publicHash -cnotmatch '^[a-f0-9]{64}$'){throw 'Release signer identity could not be read'}
    $id=[Guid]::NewGuid().ToString('N')
    $issued=[DateTimeOffset]::UtcNow
    $format="yyyy-MM-dd'T'HH:mm:ss.fff'Z'"
    $culture=[Globalization.CultureInfo]::InvariantCulture
    $expires=$issued.AddMinutes(10).ToString($format,$culture)
    Add-Type -AssemblyName System.Security.Cryptography.ProtectedData
    $entropy=[Text.Encoding]::ASCII.GetBytes('AETHERFRONT GITHUB RELEASE SIGNER v3')
    $secret=[Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($privatePath),$entropy,'CurrentUser')
    try {
        $signature=[Convert]::ToBase64String($secret) | & $node (Join-Path $PSScriptRoot 'Sign-LocalReleaseRequest.mjs') 'vmi3254434' $id $Action $Version $sha $SourceCommit '' ($issued.ToString($format,$culture)) $expires $publicHash
        if($LASTEXITCODE -ne 0){throw 'Release request could not be signed'}
    } finally {[Array]::Clear($secret)}
    $envelope=Join-Path $signerDir ($id+'.json')
    $request=[ordered]@{requestId=$id;action=$Action;version=$Version;archiveSha256=$sha;sourceCommit=$SourceCommit;expiresAtUtc=$expires}
    $requestPath=Join-Path $signerDir "submitted-$id.json"
    [IO.File]::WriteAllText($requestPath,($request|ConvertTo-Json -Compress),[Text.UTF8Encoding]::new($false))
    try {
        [IO.File]::WriteAllText($envelope,$signature,[Text.UTF8Encoding]::new($false))
        try{Send-Asset "v3-request-$id.json" $envelope 'application/json'}
        catch{throw "Submission acknowledgement unavailable for $id. Check its receipt before retrying."}
    } finally {[IO.File]::Delete($envelope)}
    [pscustomobject]$request
} finally {$stream.Dispose()}
