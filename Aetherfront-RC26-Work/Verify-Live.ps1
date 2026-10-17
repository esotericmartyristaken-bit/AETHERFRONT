#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$BuildResultPath)
. "$PSScriptRoot/VersionPatcher/Review.Common.ps1"
$review = Get-RC26ReviewedRelease $BuildResultPath
$version = 'kesas-rc26'
$feedPath = "$RC26Root/Updates/APFClientUpdate.json"
$archive = "$RC26Root/Updates/$version/APFClient-$version.zip"
$feed = Get-RC26SignedFeed $feedPath $archive $version
if (@($feed.files).Count -ne 464 -or (ConvertTo-RC26InventoryJson $feed.files) -cne (ConvertTo-RC26InventoryJson $review.feed.files)) { throw 'Native signed inventory differs from the reviewed 464-file candidate.' }
$deployment = Get-UpdateDeployment $RC26Root
if ($feed.packageUrl -cne "$($deployment.packOrigin)/updates/$version/APFClient-$version.zip") { throw 'Native package URL changed.' }
$pack = Read-UpdateText "$RC26Root/Server/Config/APFPack.json" | ConvertFrom-Json
$expectedPack = Read-UpdateText "$($review.directory)/Release/APFPack-template.json" | ConvertFrom-Json
if ((ConvertTo-RC26Json $pack) -cne (ConvertTo-RC26Json $expectedPack) -or @($pack.managedPackages).Count -ne 0) { throw 'Active server pack differs from the reviewed version-only template.' }
$prepared = "$RC26Root/.github-prepared/$version"
$preparedRecord = Read-UpdateText "$prepared/prepared.json" | ConvertFrom-Json
if ($preparedRecord.schema -ne 1 -or $preparedRecord.version -cne $version -or $preparedRecord.fingerprint.releaseZipSha256 -cne $review.report.releaseSha256 -or
    $preparedRecord.fingerprint.notes -cne $review.report.releaseNotes) { throw 'Native prepared-release fingerprint differs from the approved release.' }
foreach ($file in $preparedRecord.files) {
    $path = Get-UpdateChildPath $prepared $file.path
    Assert-RC26Hash $path $file.sha256
    if (([IO.FileInfo]$path).Length -ne $file.sizeBytes) { throw 'Native prepared-release size changed.' }
}
foreach ($pair in @(@($feedPath,"$prepared/Published/APFClientUpdate.json"),@($archive,"$prepared/Published/APFClient-$version.zip"),
    @("$RC26Root/KESAS-Client-Bootstrap.zip","$prepared/Aetherfront-Starter-$version.zip"))) {
    Assert-RC26Hash $pair[0] (Get-RC26Hash $pair[1])
}
Assert-RC26Hash "$prepared/install-catalog.json" $review.report.catalogSha256
foreach ($file in $review.report.protectedFiles) { Assert-RC26Hash (Get-UpdateChildPath $RC26Root $file.path) $file.sha256 }
if ([IO.File]::Exists("$RC26Root/.update-transaction.json")) { throw 'Native deployment journal still exists; completion requires review.' }
$stream = [IO.File]::Open("$RC26Root/Server/APFDiscordBridge/status.json",[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
$reader = [IO.StreamReader]::new($stream)
try { $status = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
$ready = Read-UpdateText "$RC26Root/Server/APFDiscordBridge/ready.json" | ConvertFrom-Json
$age = ([DateTimeOffset]::UtcNow - ([DateTimeOffset]$status.updatedAt).ToUniversalTime()).TotalSeconds
if ($age -lt -2 -or $age -gt 10 -or $ready.pid -ne $status.serverPid) { throw 'Server bridge is not fresh/ready.' }
$game = Get-Process -Id $status.serverPid
if ($game.Path -ine ([IO.Path]::GetFullPath("$RC26Root/Server/Server.exe")) -or
    !@(Get-NetUDPEndpoint -LocalPort $deployment.gamePort | Where-Object OwningProcess -EQ $game.Id).Count) { throw 'Native game listener ownership mismatch.' }
$tcp = @(Get-NetTCPConnection -LocalPort $deployment.packPort -State Listen)
if ($tcp.Count -ne 1) { throw 'HTTPS listener missing/ambiguous.' }
$https = Get-Process -Id $tcp[0].OwningProcess
if ($https.Path -ine ([IO.Path]::GetFullPath("$RC26Root/PackHost/ApfPackHost.exe"))) { throw 'Native HTTPS executable ownership mismatch.' }
& "$RC26Root/CertificateTool/ApfCertificateTool.exe" --probe-server "https://127.0.0.1:$($deployment.packPort)/health" $deployment.certificateSha256
if ($LASTEXITCODE -ne 0) { throw 'Pinned HTTPS health verification failed.' }
$vanilla = Get-Process -Id 2320
if ($vanilla.StartTime.ToUniversalTime().ToString('o') -cne '2026-09-23T00:37:33.1565324Z' -or
    !@(Get-NetUDPEndpoint -LocalPort 2252 | Where-Object OwningProcess -EQ 2320).Count) { throw 'Vanilla service changed.' }
$attempt = New-RC26Attempt 'Live'
Write-RC26Json "$($attempt.pending)/LIVE-RESULT.json" ([ordered]@{ version = $version; verifiedAtUtc = [DateTime]::UtcNow.ToString('o'); signedFiles = 464
    nativePreparedBytesIdentical = $true; catalogSha256 = $review.report.catalogSha256; fullPackages = 211; potatoPackages = 177
    serverManagedPackagesEmpty = $true; protectedHashesUnchanged = $true; gamePid = $game.Id; httpsPid = $https.Id; vanillaPid = $vanilla.Id
    vanillaUnchanged = $true; liveWrites = $false })
Complete-RC26Attempt $attempt
Write-Output "LIVE-RESULT: $($attempt.complete)/LIVE-RESULT.json"
