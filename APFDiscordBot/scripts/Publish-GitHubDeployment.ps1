#requires -Version 7.0
<#
Publish an already built/tested native bundle, never source or a client release.
WhatIf/DryRun perform local validation only: no gh, HTTP, uploads or file writes.
No signing keys are read. Repository write authority authorizes deployment.
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [Parameter(Mandatory)][string]$Archive,
    [Parameter(Mandatory)][string]$Version,
    [AllowEmptyString()][string]$Notes = '',
    [string]$Repository = 'esotericmartyristaken-bit/AETHERFRONT',
    [switch]$SkipChannelUpdate,
    [switch]$DryRun
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$branch = 'deploy'
$channelPath = 'deployment/production.json'
$maximumBytes = 512MB
$utf8 = [Text.UTF8Encoding]::new($false, $true)

function Require([bool]$Condition, [string]$Message) {
    if (!$Condition) { throw [IO.InvalidDataException]::new($Message) }
}
function VersionNumber([string]$Value) {
    [long]$number = 0
    Require ($Value -cmatch '\Akesas-rc[1-9][0-9]{0,14}\z' -and
        [long]::TryParse($Value.Substring(8), [ref]$number)) 'Invalid native release version (positive integer, at most 15 digits).'
    return $number
}
function Read-Channel([string]$Json) {
    Require ($utf8.GetByteCount($Json) -le 16384) 'Deployment manifest exceeds 16 KiB.'
    $document = [Text.Json.JsonDocument]::Parse($Json)
    try {
        $root = $document.RootElement
        Require ($root.ValueKind -eq [Text.Json.JsonValueKind]::Object) 'Deployment manifest must be an object.'
        $fields = @('schemaVersion','version','releaseTag','assetName','sha256','sizeBytes','notes')
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($property in $root.EnumerateObject()) {
            Require ($property.Name -cin $fields -and $seen.Add($property.Name)) 'Unknown or duplicate deployment manifest field.'
            if ($property.Name -cin @('schemaVersion','sizeBytes')) {
                Require ($property.Value.ValueKind -eq [Text.Json.JsonValueKind]::Number -and
                    $property.Value.GetRawText() -cmatch '\A[1-9][0-9]*\z') 'Manifest integers must be positive JSON integers.'
            } else {
                Require ($property.Value.ValueKind -eq [Text.Json.JsonValueKind]::String) 'Manifest text fields must be strings.'
            }
        }
        Require ($seen.Count -eq $fields.Count) 'Missing deployment manifest field.'
        $value = ConvertFrom-Json -InputObject $Json
        Require ($value.schemaVersion -eq 1) 'Unsupported deployment manifest schema.'
        $null = VersionNumber $value.version
        Require ($value.releaseTag -ceq "host-$($value.version)" -and
            $value.assetName -ceq "Aetherfront-Release-$($value.version).zip") 'Deployment release tag/asset mismatch.'
        Require ($value.sha256 -cmatch '\A[a-f0-9]{64}\z') 'Deployment SHA256 must be lowercase hexadecimal.'
        Require ($value.sizeBytes -gt 0 -and $value.sizeBytes -le $maximumBytes) 'Deployment asset must be positive and at most 512 MiB.'
        Require ($value.notes.Length -le 500 -and $value.notes -notmatch '[\p{Cc}\p{Cf}]') 'Notes exceed 500 characters or contain control characters.'
        return [ordered]@{ schemaVersion = 1; version = $value.version; releaseTag = $value.releaseTag
            assetName = $value.assetName; sha256 = $value.sha256; sizeBytes = [long]$value.sizeBytes; notes = $value.notes }
    } finally { $document.Dispose() }
}
function Same-Channel($Left, $Right) {
    return ($null -ne $Left -and
        (ConvertTo-Json -InputObject $Left -Compress) -ceq (ConvertTo-Json -InputObject $Right -Compress))
}
function Invoke-Gh([string[]]$Arguments, [string]$Body = '') {
    # Never print gh stderr, which can contain local paths or signed URLs.
    if ($Body) { $output = @($Body | & gh @Arguments 2>&1) }
    else { $output = @(& gh @Arguments 2>&1) }
    $code = $LASTEXITCODE
    $text = ($output | ForEach-Object { [string]$_ }) -join "`n"
    Require ($text.Length -le 4MB) 'GitHub response exceeded its output limit.'
    return @{ exitCode = $code; text = $text }
}
function Api([string]$Endpoint, [string]$Method = 'GET', $Body = $null, [switch]$AllowMissing) {
    $resource = "repos/$Repository"
    if ($Endpoint) { $resource += "/$Endpoint" }
    $arguments = @('api', $resource, '--hostname', 'github.com', '--method', $Method,
        '--header', 'Accept: application/vnd.github+json', '--header', 'X-GitHub-Api-Version: 2022-11-28', '--include')
    $json = ''
    if ($null -ne $Body) { $arguments += @('--input','-'); $json = ConvertTo-Json -InputObject $Body -Depth 10 -Compress }
    $response = Invoke-Gh $arguments $json
    $http = [regex]::Matches($response.text, '(?m)^HTTP/\S+ ([0-9]{3})[^\r\n]*\r?\n(?:[^\r\n]+\r?\n)*\r?\n')
    Require ($http.Count -eq 1) 'GitHub did not return one unambiguous HTTP response.'
    $status = [int]$http[0].Groups[1].Value
    if ($AllowMissing -and $status -eq 404) { return $null }
    Require ($response.exitCode -eq 0 -and $status -ge 200 -and $status -lt 300) 'GitHub API request failed; preserve assets and inspect access/concurrency before retrying.'
    return (ConvertFrom-Json -InputObject $response.text.Substring($http[0].Index + $http[0].Length) -NoEnumerate)
}
function Snapshot {
    $reference = Api "git/ref/heads/$branch"
    Require ($reference.ref -ceq "refs/heads/$branch" -and $reference.object.type -ceq 'commit' -and
        $reference.object.sha -cmatch '\A[a-f0-9]{40}\z') 'Missing or invalid deploy branch; create it explicitly before publication.'
    $head = $reference.object.sha
    $file = Api "contents/$($channelPath)?ref=$head" -AllowMissing
    if ($null -eq $file) { return @{ head = $head; fileSha = $null; manifest = $null } }
    Require ($file.type -ceq 'file' -and $file.path -ceq $channelPath -and $file.encoding -ceq 'base64' -and
        $file.sha -cmatch '\A[a-f0-9]{40}\z' -and $file.size -gt 0 -and $file.size -le 16384 -and
        $file.content.Length -le 32768) 'Invalid deployment channel contents response.'
    $bytes = [Convert]::FromBase64String($file.content)
    Require ($bytes.Length -eq $file.size) 'Deployment channel contents length mismatch.'
    return @{ head = $head; fileSha = $file.sha; manifest = Read-Channel ($utf8.GetString($bytes)) }
}
function Check-Order($Current, $Candidate) {
    if ($null -eq $Current) { return }
    $old = VersionNumber $Current.version
    $next = VersionNumber $Candidate.version
    Require ($next -gt $old -or ($next -eq $old -and (Same-Channel $Current $Candidate))) 'Channel downgrade or changed same-version publication refused. Use a new release version.'
}
function Host-Release {
    $release = Api "releases/tags/$($manifest.releaseTag)" -AllowMissing
    if ($null -eq $release) {
        $created = Invoke-Gh @('release','create',$manifest.releaseTag,'--repo',"github.com/$Repository",
            '--prerelease','--latest=false','--title',$manifest.releaseTag,'--notes','Native host deployment artifact; not a client release.')
        # A competing publisher or lost response can create the same tag. Read
        # back before deciding; never delete/recreate or promote an existing tag.
        $release = Api "releases/tags/$($manifest.releaseTag)" -AllowMissing
        Require ($null -ne $release) 'Host prerelease creation was not confirmed. No channel update requested.'
    }
    Require ($release.tag_name -ceq $manifest.releaseTag -and $release.prerelease -is [bool] -and
        $release.prerelease -and $release.draft -is [bool] -and !$release.draft -and
        [string]$release.id -cmatch '\A[1-9][0-9]*\z') 'Existing host tag must be a published prerelease, never a stable/client release.'
    return $release
}
function Remote-Asset($Release) {
    $matches = @()
    for ($page = 1; $page -le 10; $page++) {
        $items = @(Api "releases/$($Release.id)/assets?per_page=100&page=$page")
        foreach ($item in $items) {
            if ($item.name -ieq $manifest.assetName) { $matches += $item }
        }
        if ($items.Count -lt 100) { break }
        Require ($page -lt 10) 'Host prerelease exceeds the bounded 1000-asset inventory.'
    }
    Require ($matches.Count -le 1) 'Duplicate or case-colliding deployment assets refused.'
    if ($matches.Count) { return $matches[0] }
    return $null
}
function Anonymous-Hash([string]$Url, [long]$Size) {
    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $false
    $handler.UseCookies = $false
    $handler.UseDefaultCredentials = $false
    $client = [Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromMinutes(10)
    $deadline = [Threading.CancellationTokenSource]::new([TimeSpan]::FromMinutes(10))
    $current = [Uri]$Url
    try {
        for ($hop = 0; $hop -le 5; $hop++) {
            Require ($current.Scheme -ceq 'https' -and $current.Port -eq 443 -and !$current.UserInfo -and !$current.Fragment -and
                $current.IdnHost -cin @('github.com','release-assets.githubusercontent.com','objects.githubusercontent.com') -and
                ($current.IdnHost -cne 'github.com' -or $current.AbsoluteUri -ceq $Url)) 'Untrusted deployment asset redirect refused.'
            $response = $client.GetAsync($current, [Net.Http.HttpCompletionOption]::ResponseHeadersRead, $deadline.Token).GetAwaiter().GetResult()
            try {
                if ([int]$response.StatusCode -in @(301,302,303,307,308)) {
                    Require ($hop -lt 5 -and $null -ne $response.Headers.Location) 'Deployment asset redirect limit exceeded.'
                    $current = [Uri]::new($current, $response.Headers.Location)
                    continue
                }
                Require ([int]$response.StatusCode -eq 200) 'Anonymous deployment asset download failed.'
                Require ($null -eq $response.Content.Headers.ContentLength -or $response.Content.Headers.ContentLength -eq $Size) 'Remote deployment asset length mismatch.'
                $stream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
                $hash = [Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
                try {
                    $buffer = [byte[]]::new(81920)
                    [long]$total = 0
                    while (($count = $stream.ReadAsync($buffer,0,$buffer.Length,$deadline.Token).GetAwaiter().GetResult()) -gt 0) {
                        $total += $count
                        Require ($total -le $Size) 'Remote deployment asset exceeded its declared length.'
                        $hash.AppendData($buffer,0,$count)
                    }
                    Require ($total -eq $Size) 'Remote deployment asset was truncated.'
                    return [BitConverter]::ToString($hash.GetHashAndReset()).Replace('-','').ToLowerInvariant()
                } finally { $hash.Dispose(); $stream.Dispose() }
            } finally { $response.Dispose() }
        }
    } finally { $deadline.Dispose(); $client.Dispose() }
}
function Verify-Remote($Asset) {
    Require ($Asset.name -ceq $manifest.assetName -and $Asset.state -ceq 'uploaded' -and
        [string]$Asset.size -cmatch '\A[1-9][0-9]*\z' -and $Asset.size -eq $manifest.sizeBytes) 'Existing deployment asset name/state/size mismatch. Never overwrite it.'
    $url = "https://github.com/$Repository/releases/download/$($manifest.releaseTag)/$($manifest.assetName)"
    Require ($Asset.browser_download_url -ceq $url) 'Unexpected deployment asset URL.'
    $digest = $Asset.PSObject.Properties['digest']
    if ($null -ne $digest -and ![string]::IsNullOrEmpty([string]$digest.Value)) {
        Require ([string]$digest.Value -cmatch '\Asha256:[a-fA-F0-9]{64}\z' -and
            ([string]$digest.Value).Substring(7) -ieq $manifest.sha256) 'Existing deployment asset digest mismatch. Never overwrite it.'
    } else {
        Require ((Anonymous-Hash $url $manifest.sizeBytes) -ceq $manifest.sha256) 'Anonymous deployment asset SHA256 mismatch. Never overwrite it.'
    }
}

Require ($Repository -ceq 'esotericmartyristaken-bit/AETHERFRONT') 'Only the fixed AETHERFRONT repository is authorized.'
$null = VersionNumber $Version
Require ($Notes.Length -le 500 -and $Notes -notmatch '[\p{Cc}\p{Cf}]') 'Notes exceed 500 characters or contain control characters.'
$archivePath = [IO.Path]::GetFullPath($Archive)
Require (![IO.Path]::IsPathRooted($Archive) -or !$archivePath.StartsWith('\\')) 'Archive must be a local file, not a network share.'
for ($cursor = $archivePath; $cursor; $cursor = [IO.Path]::GetDirectoryName($cursor)) {
    Require (!([IO.File]::GetAttributes($cursor) -band [IO.FileAttributes]::ReparsePoint)) 'Archive path must not contain reparse points.'
}
Require ([IO.Path]::GetFileName($archivePath) -ceq "Aetherfront-Release-$Version.zip") 'Archive basename must equal the immutable deployment asset name.'
# Hold the input read-only across validation, hashing and upload. No rename,
# replacement or in-place write can race the publisher on Windows.
$archiveHandle = [IO.FileStream]::new($archivePath,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
try {
    Require ($archiveHandle.Length -gt 0 -and $archiveHandle.Length -le $maximumBytes) 'Archive must be positive and at most 512 MiB.'
    $null = & (Join-Path $PSScriptRoot 'Verify-KesasRelease.ps1') -Archive $archivePath -ExpectedVersion $Version
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $digest = [BitConverter]::ToString($sha.ComputeHash($archiveHandle)).Replace('-','').ToLowerInvariant() }
    finally { $sha.Dispose() }
    $manifest = Read-Channel (ConvertTo-Json -InputObject ([ordered]@{ schemaVersion = 1; version = $Version
        releaseTag = "host-$Version"; assetName = "Aetherfront-Release-$Version.zip"; sha256 = $digest
        sizeBytes = $archiveHandle.Length; notes = $Notes }) -Compress)
    if ($DryRun -or $WhatIfPreference) {
        Write-Output 'Local validation passed. Offline plan only; GitHub state/order/authentication NOT checked; build/KSP tests NOT executed.'
        Write-Output (ConvertTo-Json -InputObject $manifest)
        return
    }
    if (!$PSCmdlet.ShouldProcess("github.com/$Repository", "Publish immutable $($manifest.releaseTag) artifact; channel update disabled=$SkipChannelUpdate")) { return }
    $null = Get-Command gh -CommandType Application,Function -ErrorAction Stop
    $repo = Api ''
    Require ($repo.full_name -ceq $Repository -and $repo.private -is [bool] -and !$repo.private -and
        $repo.archived -is [bool] -and !$repo.archived -and $repo.disabled -is [bool] -and !$repo.disabled) 'Deployment repository must be public, enabled and not archived.'
    $before = Snapshot
    Check-Order $before.manifest $manifest
    $release = Host-Release
    $asset = Remote-Asset $release
    if ($null -eq $asset) {
        $uploaded = Invoke-Gh @('release','upload',$manifest.releaseTag,$archivePath,'--repo',"github.com/$Repository")
        # No --clobber. Read back even after failure: another publisher or a
        # lost response may have produced the exact immutable asset.
        $asset = Remote-Asset $release
        Require ($null -ne $asset) 'Upload not confirmed. Host prerelease retained; channel unchanged. Retry the same bytes.'
    }
    Verify-Remote $asset
    if ($SkipChannelUpdate) { Write-Output "Verified host prerelease $($manifest.releaseTag); deployment channel unchanged."; return }
    $after = Snapshot
    Check-Order $after.manifest $manifest
    if (Same-Channel $after.manifest $manifest) { Write-Output "Deployment channel already matches $Version; no content write."; return }
    Require ($after.head -ceq $before.head -and $after.fileSha -ceq $before.fileSha) 'Deploy branch changed during publication. Asset retained; rerun against the new branch head.'
    $body = @{ branch = $branch; message = "Deploy $Version"; content = [Convert]::ToBase64String($utf8.GetBytes((ConvertTo-Json -InputObject $manifest) + "`n")) }
    if ($null -ne $before.fileSha) { $body.sha = $before.fileSha }
    $null = Api "contents/$channelPath" 'PUT' $body
    $confirmed = Snapshot
    Require (Same-Channel $confirmed.manifest $manifest) 'Channel update not confirmed or concurrently superseded. Read back before retrying; never force overwrite.'
    Write-Output "Published $($manifest.releaseTag); $branch/$channelPath now requests $Version. Host activation is asynchronous, not confirmed here."
} finally { $archiveHandle.Dispose() }
