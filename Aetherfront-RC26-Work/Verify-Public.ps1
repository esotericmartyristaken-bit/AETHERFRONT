#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$BuildResultPath)
. "$PSScriptRoot/VersionPatcher/Review.Common.ps1"
$review = Get-RC26ReviewedRelease $BuildResultPath
$version = 'kesas-rc26'
$nativeFeed = Get-RC26SignedFeed "$RC26Root/Updates/APFClientUpdate.json" "$RC26Root/Updates/$version/APFClient-$version.zip" $version
if (@($nativeFeed.files).Count -ne 464 -or (ConvertTo-RC26Json @($nativeFeed.files | Sort-Object path)) -cne (ConvertTo-RC26Json @($review.feed.files | Sort-Object path))) { throw 'Native signed candidate inventory mismatch.' }
$catalogRecord = @($nativeFeed.files | Where-Object path -CEQ $RC26CatalogRelative)
if ($catalogRecord.Count -ne 1 -or $catalogRecord[0].sha256 -cne $review.report.catalogSha256) { throw 'Native catalog does not match reviewed signed catalog.' }
$catalog = Get-RC26ZipJson "$RC26Root/Updates/$version/APFClient-$version.zip" $catalogRecord[0]
$repository = $catalog.gitHub.repository
if (!$catalog.gitHub.enabled -or $repository -cne 'esotericmartyristaken-bit/AETHERFRONT' -or $catalog.gitHub.mediaTag -cne 'pack-media') { throw 'Unreviewed GitHub routing.' }
$headers = @{ 'User-Agent' = 'Aetherfront-Public-Verification'; Accept = 'application/vnd.github+json' }
$latest = Invoke-RestMethod "https://api.github.com/repos/$repository/releases/latest" -Headers $headers -TimeoutSec 30
if ($latest.tag_name -cne $version -or $latest.draft -or $latest.prerelease -or @($latest.assets).Count -ne 3) { throw 'RC26 is not the latest complete public release.' }
$mediaRelease = Invoke-RestMethod "https://api.github.com/repos/$repository/releases/tags/pack-media" -Headers $headers -TimeoutSec 30
if ($mediaRelease.draft) { throw 'Historical media release is not public.' }
# Authenticate every locally retained historical feed/catalog, not only RC25.
# No old asset is deleted or overwritten by this verifier.
$history = Get-RC26HistoricalMedia
if ('kesas-rc25' -cnotin $history.versions -or 'kesas-rc26' -cnotin $history.versions) { throw 'Historical release inventory is incomplete.' }
$expected = @{}
foreach ($asset in @($history.media) + @(Get-RC26Media $catalog)) {
    if ($expected.ContainsKey($asset.name) -and (ConvertTo-RC26Json $expected[$asset.name]) -cne (ConvertTo-RC26Json $asset)) { throw 'Conflicting historical media declaration.' }
    $expected[$asset.name] = $asset
}
if (@($mediaRelease.assets).Count -ne $expected.Count) { throw 'Historical/public media inventory mismatch.' }
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($asset in $mediaRelease.assets) {
    if (!$seen.Add($asset.name) -or !$expected.ContainsKey($asset.name)) { throw 'Unexpected/duplicate public media asset.' }
    $package = $expected[$asset.name]
    if ($asset.state -cne 'uploaded' -or $asset.size -ne $package.sizeBytes -or $asset.digest -cne "sha256:$($package.sha256)") { throw 'Historical media digest/size mismatch.' }
}
$native = @{
    'APFClientUpdate.json' = "$RC26Root/Updates/APFClientUpdate.json"
    'APFClient-kesas-rc26.zip' = "$RC26Root/Updates/kesas-rc26/APFClient-kesas-rc26.zip"
    'Aetherfront-Starter-kesas-rc26.zip' = "$RC26Root/KESAS-Client-Bootstrap.zip"
}
$prepared = "$RC26Root/.github-prepared/$version"
foreach ($name in $native.Keys) {
    $preparedPath = if ($name -ceq 'Aetherfront-Starter-kesas-rc26.zip') { "$prepared/$name" } else { "$prepared/Published/$name" }
    Assert-RC26Hash $native[$name] (Get-RC26Hash $preparedPath)
}
$attempt = New-RC26Attempt 'Public'
foreach ($asset in $latest.assets) {
    if (!$native.ContainsKey($asset.name)) { throw 'Unexpected public release asset.' }
    $source = $native[$asset.name]; $hash = Get-RC26Hash $source
    if ($asset.state -cne 'uploaded' -or $asset.size -ne ([IO.FileInfo]$source).Length -or $asset.digest -cne "sha256:$hash") { throw 'Public/native release digest mismatch.' }
    $destination = Get-UpdateChildPath $attempt.pending $asset.name
    Invoke-WebRequest "https://github.com/$repository/releases/download/$version/$($asset.name)" -OutFile $destination -MaximumRedirection 5 -TimeoutSec 120
    Assert-RC26Hash $destination $hash
    if (([IO.FileInfo]$destination).Length -ne $asset.size) { throw 'Anonymous release download size mismatch.' }
}
$null = Get-RC26SignedFeed "$($attempt.pending)/APFClientUpdate.json" "$($attempt.pending)/APFClient-kesas-rc26.zip" $version
Write-RC26Json "$($attempt.pending)/PUBLIC-RESULT.json" ([ordered]@{ version = $version; verifiedAtUtc = [DateTime]::UtcNow.ToString('o'); signedFiles = 464
    anonymousAssets = 3; mediaAssets = $expected.Count; historicalVersions = $history.versions; historicalMediaRetained = $true
    nativePreparedBytesIdentical = $true; nativePublicBytesIdentical = $true; tlsBypass = $false; liveWrites = $false })
Complete-RC26Attempt $attempt
Write-Output "PUBLIC-RESULT: $($attempt.complete)/PUBLIC-RESULT.json"
