#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CodeResultPath,
    [Parameter(Mandatory)][ValidateSet('ReuseRC25','ReviewedAssets')][string]$MediaMode,
    [Parameter(Mandatory)][string]$CompatibilityEvidencePath,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{64}$')][string]$CompatibilityEvidenceSha256,
    [Parameter(Mandatory)][string]$Notes,
    [Parameter(Mandatory)][string]$PotatoPolicyPlanPath,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{64}$')][string]$PotatoPolicyPlanSha256,
    [string]$CatalogPath,
    [string]$AssetStage,
    [string]$AssetPlanName = 'ASSET-PLAN.json',
    [string]$AssetPlanSha256,
    [string]$AssetTestResultPath,
    [string]$AssetTestResultSha256,
    [ValidateSet('ProfileTests','FinalRelease')][string]$Purpose = 'FinalRelease',
    [switch]$ReleaseApproved
)
# Invoke only after main approves the final media/MM/native-test decision. This
# creates a LOCAL candidate, never native staging, activation, or publication.
. "$PSScriptRoot/VersionPatcher/Review.Common.ps1"
if (!$ReleaseApproved) { throw 'Release preparation requires main approval; use -ReleaseApproved only after the final media/baseline decision.' }
if ($Purpose -ceq 'FinalRelease' -and !$AssetStage) { throw 'Final RC26 release requires the reviewed five-archive asset stage. No-assets candidates are test-only.' }
if (($AssetStage -and $MediaMode -cne 'ReviewedAssets') -or (!$AssetStage -and $MediaMode -cne 'ReuseRC25')) { throw 'MediaMode must explicitly match the reviewed asset stage choice.' }
$baseline = Get-RC26Baseline
$review = Get-RC26ReviewedCode $CodeResultPath
Assert-RC26Hash $CompatibilityEvidencePath $CompatibilityEvidenceSha256
if ([string]::IsNullOrWhiteSpace($Notes)) { throw 'Use explicit release notes; the native publisher must receive these same notes.' }
$assetReview = $null; $assetDeclarations = @(); $newMedia = @()
if ($AssetStage) {
    $assetReview = Get-RC26AssetStage $AssetStage $AssetPlanName $AssetPlanSha256 $AssetTestResultPath $AssetTestResultSha256 $baseline.catalog
    $assetDeclarations = $assetReview.declarations; $newMedia = $assetReview.files
}
$policyReview = Get-RC26PotatoPolicyPlan $PotatoPolicyPlanPath $PotatoPolicyPlanSha256 $baseline.catalog
$newMedia += $policyReview.file
$catalog = Get-RC26VersionCatalog $baseline.catalog $assetDeclarations $policyReview.declaration
if ($CatalogPath) {
    $supplied = Read-UpdateText $CatalogPath | ConvertFrom-Json
    if ((ConvertTo-RC26Json $supplied) -cne (ConvertTo-RC26Json $catalog)) { throw 'Catalog may change only revision routing, four repair package versions, five reviewed asset archives, and the independently reviewed Potato-only policy archive.' }
}
$pack = ConvertTo-RC26Json $baseline.pack | ConvertFrom-Json
$pack.packVersion = 'kesas-rc26'; $pack.requiredClientVersion = 'kesas-rc26'
# Every existing clientOnly allowance and DLL pin is retained; managedPackages
# remains empty. Full/Potato manifests belong only in the signed client catalog.
$deployment = Get-UpdateDeployment $RC26Root
$inputPaths = @($baseline.feedPath,$baseline.archive,"$RC26Root/ClientTemplate.zip","$RC26Root/Server/Config/APFPack.json",
    "$RC26Root/Server/Config/AetherfrontTechnology.json","$RC26Root/deployment.json","$RC26Root/Publish-Release.ps1",
    "$RC26Root/GitHub.Publish.ps1","$RC26Root/Update.Common.ps1","$RC26Root/UpdateTool/ApfUpdateTool.exe",
    "$RC26Root/secrets/update-signing-public.xml",$review.resultPath,$CompatibilityEvidencePath)
if ($CatalogPath) { $inputPaths += $CatalogPath }
if ($assetReview) {
    $inputPaths += @($assetReview.planPath,$assetReview.testResultPath)
    $inputPaths += @($assetReview.files.source)
}
$inputPaths += @(foreach ($file in $policyReview.snapshotFiles) { Get-UpdateChildPath $policyReview.directory $file.path })
$inputs = @(foreach ($path in $inputPaths) { [ordered]@{ path = Assert-UpdatePath $path; sha256 = Get-RC26Hash $path } })
$protected = @(foreach ($relative in @('Server/Config/GeneralSettings.xml','Server/Server.exe','PackHost/ApfPackHost.dll','deployment.json','Server/Config/AetherfrontTechnology.json')) {
    [ordered]@{ path = $relative; sha256 = Get-RC26Hash "$RC26Root/$relative" }
})
$media = @{}
foreach ($asset in Get-RC26Media $baseline.catalog) {
    if ($media.ContainsKey($asset.name) -and (ConvertTo-RC26Json $media[$asset.name]) -cne (ConvertTo-RC26Json $asset)) { throw 'Conflicting baseline media declaration.' }
    $media[$asset.name] = $asset
}
# Validate the existing media without copying, rewriting, or duplicating cached
# content. The four repair version bumps keep URL/hash/size exactly unchanged.
foreach ($asset in $media.Values) {
    $path = Get-UpdateChildPath $RC26Root "Server/Config/$($asset.name)"
    Assert-RC26Hash $path $asset.sha256
    if (([IO.FileInfo]$path).Length -ne $asset.sizeBytes) { throw 'Retained media size mismatch.' }
}
$attempt = New-RC26Attempt 'Release'
$stage = $attempt.pending
$release = Get-UpdateChildPath $stage 'Release'
$candidate = Get-UpdateChildPath $stage 'Candidate'
New-UpdateDirectory $release; New-UpdateDirectory $candidate
if ($assetReview) {
    # This is a LOCAL immutable candidate snapshot, not native release staging.
    New-UpdateDirectory "$candidate/Assets"
    foreach ($file in $assetReview.inventory) {
        $destination = Get-UpdateChildPath "$candidate/Assets" $file.path
        New-UpdateDirectory ([IO.Path]::GetDirectoryName($destination))
        [IO.File]::Copy((Get-UpdateChildPath $assetReview.directory $file.path),$destination,$false)
        Assert-RC26Hash $destination $file.sha256
    }
    New-UpdateDirectory "$candidate/Media"
    foreach ($file in $assetReview.files) {
        $destination = Get-UpdateChildPath $candidate $file.path
        [IO.File]::Copy($file.source,$destination,$false)
        Assert-RC26Hash $destination $file.sha256
    }
}
New-UpdateDirectory "$candidate/Policy"
foreach ($file in $policyReview.snapshotFiles) {
    $destination = Get-UpdateChildPath "$candidate/Policy" $file.path
    New-UpdateDirectory ([IO.Path]::GetDirectoryName($destination))
    [IO.File]::Copy((Get-UpdateChildPath $policyReview.directory $file.path),$destination,$false)
    Assert-RC26Hash $destination $file.sha256
}
New-UpdateDirectory "$candidate/Media"
[IO.File]::Copy($policyReview.file.source,(Get-UpdateChildPath $candidate $policyReview.file.path),$false)
Assert-RC26Hash (Get-UpdateChildPath $candidate $policyReview.file.path) $policyReview.file.sha256
$client = Get-UpdateChildPath $candidate 'Client'
Expand-UpdateArchive "$RC26Root/ClientTemplate.zip" $client
Assert-RC26TreeInventory $client $baseline.feed.files
$changedPaths = @('GameData/LunaMultiplayer/Plugins/ApfCore.dll','GameData/LunaMultiplayer/Plugins/LmpClient.dll',
    'GameData/LunaMultiplayer/Plugins/KesasInstall.dll',$RC26CatalogRelative,'GameData/LunaMultiplayer/Data/APFClient.json','KESAS/PATCH-MANIFEST.json')
foreach ($pair in @(@('Patched/ApfCore.dll',$changedPaths[0]),@('Patched/LmpClient.dll',$changedPaths[1]),@('Engine/KesasInstall.dll',$changedPaths[2]))) {
    $source = Get-UpdateChildPath $review.directory $pair[0]
    [IO.File]::Copy($source,(Get-UpdateChildPath $client $pair[1]),$true)
    Assert-RC26Hash (Get-UpdateChildPath $client $pair[1]) (Get-RC26Hash $source)
}
# Only these already-existing fresh-candidate files are replaced. No removal,
# release-root media, ServerPayload, trust-key replacement, or UI graft occurs.
$catalogFile = Get-UpdateChildPath $client $RC26CatalogRelative
[IO.File]::WriteAllText($catalogFile,(ConvertTo-Json -InputObject $catalog -Depth 80) + "`n",$RC26Utf8)
$clientConfig = Get-UpdateChildPath $client 'GameData/LunaMultiplayer/Data/APFClient.json'
$config = Read-UpdateText $clientConfig | ConvertFrom-Json
if ($config.clientVersion -cne 'kesas-rc25' -or !$config.enabled) { throw 'Unexpected baseline client configuration.' }
$config.clientVersion = 'kesas-rc26'
[IO.File]::WriteAllText($clientConfig,(ConvertTo-Json -InputObject $config -Depth 8) + "`n",$RC26Utf8)
$inventoryPath = Get-UpdateChildPath $client 'KESAS/PATCH-MANIFEST.json'
$inventory = Read-UpdateText $inventoryPath | ConvertFrom-Json
$inventory.version = 'kesas-rc26'
$inventory.files = @(foreach ($file in Get-ChildItem -LiteralPath $client -Recurse -Force -File | Sort-Object FullName) {
    $relative = $file.FullName.Substring($client.Length + 1).Replace('\','/')
    if ($relative -ceq 'KESAS/PATCH-MANIFEST.json') { continue }
    [ordered]@{ path = $relative; bytes = $file.Length; sha256 = Get-RC26Hash $file.FullName }
})
[IO.File]::WriteAllText($inventoryPath,(ConvertTo-Json -InputObject $inventory -Depth 80) + "`n",$RC26Utf8)
$nextFiles = @(foreach ($record in $baseline.feed.files) {
    $path = Get-UpdateChildPath $client $record.path
    if ($record.path -cnotin $changedPaths) { Assert-RC26Hash $path $record.sha256 }
    [ordered]@{ path = $record.path; sha256 = Get-RC26Hash $path; sizeBytes = ([IO.FileInfo]$path).Length }
})
Assert-RC26TreeInventory $client $nextFiles
if (@($nextFiles).Count -ne 464) { throw 'RC26 must retain the actual 464-file signed inventory.' }
[IO.Compression.ZipFile]::CreateFromDirectory($client,"$release/ClientTemplate.zip",[IO.Compression.CompressionLevel]::Optimal,$false)
Write-RC26Json "$release/APFPack-template.json" $pack
[IO.File]::Copy("$RC26Root/Server/Config/AetherfrontTechnology.json","$release/AetherfrontTechnology.json",$false)
$names = @('ClientTemplate.zip','APFPack-template.json','AetherfrontTechnology.json')
Write-RC26Json "$release/RELEASE.json" ([ordered]@{ version = 'kesas-rc26'; files = @(foreach ($name in $names) { [ordered]@{ path = $name; sha256 = Get-RC26Hash "$release/$name" } }) })
$releaseZip = "$stage/Aetherfront-Release-kesas-rc26.zip"
$zip = [IO.Compression.ZipFile]::Open($releaseZip,[IO.Compression.ZipArchiveMode]::Create)
try { foreach ($name in $names + @('RELEASE.json')) { $null = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,"$release/$name",$name,[IO.Compression.CompressionLevel]::Optimal) } }
finally { $zip.Dispose() }
New-UpdateDirectory "$candidate/Published"
Invoke-UpdateTool $RC26Root @('--root-overlay','--source-root',$client,'--output',"$candidate/Published",'--version','kesas-rc26',
    '--package-base-url',"$($deployment.packOrigin)/updates/kesas-rc26/",'--private-key-xml',"$RC26Root/secrets/update-signing-private.xml",'--ksp-version','1.12.5','--release-notes',$Notes)
$signed = Get-RC26SignedFeed "$candidate/Published/APFClientUpdate.json" "$candidate/Published/APFClient-kesas-rc26.zip" 'kesas-rc26'
# Normalize dictionary/PSCustomObject representation before comparing. Raw JSON
# property ordering is not an inventory difference; paths, hashes and sizes are.
$canonicalSigned = @($signed.files | ForEach-Object { [pscustomobject][ordered]@{ path=[string]$_.path; sha256=[string]$_.sha256; sizeBytes=[long]$_.sizeBytes } } | Sort-Object path)
$canonicalExpected = @($nextFiles | ForEach-Object { [pscustomobject][ordered]@{ path=[string]$_.path; sha256=[string]$_.sha256; sizeBytes=[long]$_.sizeBytes } } | Sort-Object path)
if ((ConvertTo-RC26Json $canonicalSigned) -cne (ConvertTo-RC26Json $canonicalExpected)) { throw 'Native tool signed an unexpected candidate inventory.' }
Assert-RC26TreeInventory $client $signed.files
$starter = "$candidate/Aetherfront-Starter-kesas-rc26.zip"
[IO.File]::Copy("$release/ClientTemplate.zip",$starter,$false)
$public = Read-UpdateText "$RC26Root/secrets/update-signing-public.xml" 32768
$settings = [Xml.XmlReaderSettings]::new(); $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit; $settings.XmlResolver = $null
$reader = [Xml.XmlReader]::Create([IO.StringReader]::new($public),$settings)
$key = [Xml.XmlDocument]::new()
try { $key.Load($reader) } finally { $reader.Dispose() }
$elements = @($key.DocumentElement.ChildNodes | Where-Object NodeType -EQ ([Xml.XmlNodeType]::Element))
if ($key.DocumentElement.Name -cne 'RSAKeyValue' -or $key.DocumentElement.Attributes.Count -ne 0 -or $elements.Count -ne 2 -or
    @($elements | Where-Object { $_.Name -cnotin @('Modulus','Exponent') -or $_.Attributes.Count -ne 0 }).Count -or
    @($elements | Where-Object Name -CEQ 'Modulus').Count -ne 1 -or @($elements | Where-Object Name -CEQ 'Exponent').Count -ne 1) { throw 'Public-only starter trust key required; private key export refused.' }
$rsa = [Security.Cryptography.RSACryptoServiceProvider]::new()
try { $rsa.PersistKeyInCsp = $false; $rsa.FromXmlString($public); if ($rsa.KeySize -lt 2048) { throw 'Public signing key too small.' }; $public = $rsa.ToXmlString($false) }
finally { $rsa.Dispose() }
$zip = [IO.Compression.ZipFile]::Open($starter,[IO.Compression.ZipArchiveMode]::Update)
try {
    $trust = @{
        'GameData/LunaMultiplayer/Data/APFServer.txt' = "$($deployment.address):$($deployment.gamePort)`n"
        'GameData/LunaMultiplayer/Data/APFTlsCertificatePins.txt' = "$($deployment.certificateSha256)`n"
        'GameData/LunaMultiplayer/Data/APFClientUpdateManifestUrl.txt' = "$($deployment.packOrigin)/updates/APFClientUpdate.json`n"
        'GameData/LunaMultiplayer/Data/APFClientUpdatePublicKey.xml' = $public
    }
    foreach ($entry in $trust.GetEnumerator()) {
        if ($zip.GetEntry($entry.Key)) { throw 'Client template already contains deployment-specific trust.' }
        $writer = [IO.StreamWriter]::new($zip.CreateEntry($entry.Key).Open(),$RC26Utf8)
        try { $writer.Write($entry.Value) } finally { $writer.Dispose() }
    }
} finally { $zip.Dispose() }
[IO.File]::WriteAllText("$starter.sha256","$(Get-RC26Hash $starter)  $([IO.Path]::GetFileName($starter))`n",$RC26Utf8)
Write-RC26Json "$candidate/MEDIA-PREPARATION.json" ([ordered]@{ version = 'kesas-rc26'; mode = $MediaMode; newMedia = @($newMedia | Select-Object path,destination,sha256,sizeBytes); newMediaCount = $newMedia.Count
    requiredBeforePublication = ($newMedia.Count -gt 0); preparationExecuted = $false; existingMedia = @($media.Values | Sort-Object name); repairPackageIds = @(Get-RC26RepairIds)
    potatoPolicyPlanSha256 = $PotatoPolicyPlanSha256; potatoPolicyPackageId = 'kesas-potato-policy'; potatoPolicyProfile = 'potato'
    instructions = 'Preserve every historical asset. New Candidate/Media ZIPs need a SEPARATE main-approved exact-hash preparation into native Server/Config before publication; this build never does that. The unchanged native publisher imports no release-root media. Never overwrite a different immutable file. The native publisher must publish its own exact signed bytes, not this independently signed local preview.' })
foreach ($inputFile in $inputs) { Assert-RC26Hash $inputFile.path $inputFile.sha256 }
if ($assetReview) {
    Assert-UpdateTree $assetReview.directory
    $inventory = @(foreach ($file in Get-ChildItem -LiteralPath $assetReview.directory -Recurse -Force -File | Sort-Object FullName) {
        [ordered]@{ path = $file.FullName.Substring($assetReview.directory.Length + 1).Replace('\','/'); sha256 = Get-RC26Hash $file.FullName }
    })
    if ((ConvertTo-RC26Json $inventory) -cne (ConvertTo-RC26Json $assetReview.inventory)) { throw 'Asset stage changed during candidate build; preserve the incomplete attempt.' }
}
$null = Get-RC26ReviewedCode $CodeResultPath
Write-RC26Json "$stage/BUILD-RESULT.json" ([ordered]@{ schemaVersion = 1; version = 'kesas-rc26'; baseVersion = 'kesas-rc25'; completed = $true
    signedFiles = 464; fullPackages = 211; potatoPackages = 177; repairPackageIds = @(Get-RC26RepairIds); mediaMode = $MediaMode; newMediaCount = $newMedia.Count; purpose = $Purpose
    codeResultPath = $review.resultPath; codeResultSha256 = Get-RC26Hash $review.resultPath; graftAudit = $review.audit; inputFiles = $inputs; protectedFiles = $protected
    compatibilityEvidenceSha256 = $CompatibilityEvidenceSha256; releaseNotes = $Notes; releaseSha256 = Get-RC26Hash $releaseZip; starterSha256 = Get-RC26Hash $starter
    catalogSha256 = Get-RC26Hash $catalogFile; packTemplateSha256 = Get-RC26Hash "$release/APFPack-template.json"
    clientOnlyAllowancesUnchanged = $true; serverManagedPackagesPreservedEmpty = $true; profilePolicyUnchanged = $true; unrelatedArchiveDeclarationsUnchanged = $true
    reviewedAssetPlanSha256 = $AssetPlanSha256; assetTestResultSha256 = $AssetTestResultSha256; assetMedia = @($assetReview.files | Select-Object path,destination,sha256,sizeBytes)
    reviewedPotatoPolicyPlanSha256 = $PotatoPolicyPlanSha256; potatoPolicyTestEvidenceSha256 = $policyReview.testEvidenceSha256
    potatoPolicyReviewed = $true; potatoPolicyProfile = 'potato'; potatoPolicyDeclaration = $policyReview.declaration
    potatoPolicyMedia = $policyReview.file | Select-Object path,destination,sha256,sizeBytes
    potatoPolicySnapshotFiles = @(foreach ($file in $policyReview.snapshotFiles) { [ordered]@{ path = "Candidate/Policy/$($file.path)"; sha256 = $file.sha256 } })
    exactNativePublisherBytesRequired = $true; nativeStagingExecuted = $false; liveWrites = $false; notActivated = $true })
Complete-RC26Attempt $attempt
Write-Output "BUILD-RESULT: $($attempt.complete)/BUILD-RESULT.json"
