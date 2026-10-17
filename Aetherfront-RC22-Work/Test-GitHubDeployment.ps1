$ErrorActionPreference='Stop'
. "$PSScriptRoot/GitHub.Publish.ps1"
$root=[IO.Path]::GetFullPath("$PSScriptRoot/../KESAS-VPS-Server")
$native=[IO.File]::ReadAllText("$PSScriptRoot/NativePublisher/Publish-Release.ps1")
foreach($path in @("$PSScriptRoot/NativePublisher/Publish-Release.ps1","$PSScriptRoot/Install-GitHubPublisher.ps1","$PSScriptRoot/GitHub.Publish.ps1")){
    $tokens=$null;$errors=$null;$null=[Management.Automation.Language.Parser]::ParseFile($path,[ref]$tokens,[ref]$errors)
    if($errors.Count){throw "PowerShell syntax error: $path"}
}
$prepareIndex=$native.IndexOf('Prepare-GitHubPublication -Plan')
$stopIndex=$native.IndexOf('Wait-UpdateServerStopped $root $cfg;$stopped=$true')
if($prepareIndex-lt 0 -or $stopIndex-lt 0 -or $prepareIndex-ge $stopIndex){throw 'GitHub assets must prepare before services stop.'}
if($native.LastIndexOf('Invoke-PublisherCompletion $githubPlan')-le $native.IndexOf('Copy-UpdateAtomic "$published/APFClientUpdate.json" $currentFeed')){throw 'GitHub latest must activate after the ready native feed.'}
$livePublisherHash=(Get-FileHash -LiteralPath "$root/Publish-Release.ps1").Hash
if($livePublisherHash-ne (Get-FileHash -LiteralPath "$PSScriptRoot/NativePublisher/Publish-Release.before.ps1").Hash -and $livePublisherHash-ne (Get-FileHash -LiteralPath "$PSScriptRoot/NativePublisher/Publish-Release.ps1").Hash){throw 'Live publisher is neither the reviewed baseline nor the reviewed GitHub publisher.'}
$fixture=Join-Path $PSScriptRoot ('PublisherIntegrationFixture-'+[Guid]::NewGuid().ToString('N'))
$null=[IO.Directory]::CreateDirectory($fixture)
$media=Join-Path $fixture 'Media';$null=[IO.Directory]::CreateDirectory($media)
$catalog=Get-Content -LiteralPath "$PSScriptRoot/Candidate/Client/GameData/LunaMultiplayer/Launcher/install-catalog.json" -Raw|ConvertFrom-Json
# A separate signed tiny catalog would require another artifact. Instead validate
# real bootstrap privacy/inventory directly, then unit-test media planning offline.
Assert-GhpPublicZip "$root/Aetherfront-RC22-TestExport.zip"
Assert-GhpPublicZip "$PSScriptRoot/Candidate/Published/APFClient-kesas-rc22.zip"
$manifest=Get-Content -LiteralPath "$PSScriptRoot/Candidate/Published/APFClientUpdate.json" -Raw|ConvertFrom-Json
$bootstrap=[IO.Compression.ZipFile]::OpenRead("$root/Aetherfront-RC22-TestExport.zip")
try{foreach($file in $manifest.files){$entry=$bootstrap.GetEntry($file.path);if(-not $entry -or $entry.Length-ne $file.sizeBytes){throw 'Real bootstrap inventory mismatch.'};$h=[Security.Cryptography.SHA256]::Create();$s=$entry.Open();try{$sha=[BitConverter]::ToString($h.ComputeHash($s)).Replace('-','').ToLowerInvariant()}finally{$s.Dispose();$h.Dispose()};if($sha-ne $file.sha256){throw 'Real bootstrap hash mismatch.'}}}finally{$bootstrap.Dispose()}
$clientOnly=@($catalog.full.clientOnly).Count+@($catalog.potato.clientOnly).Count
$rejectedOldBootstrap=$false
try{$null=New-GitHubPublicationPlan -Repository 'example/aetherfront-downloads' -PublishedDirectory "$PSScriptRoot/Candidate/Published" -BootstrapZip "$root/KESAS-Client-Bootstrap.zip" -CatalogPath "$PSScriptRoot/Candidate/Client/GameData/LunaMultiplayer/Launcher/install-catalog.json" -MediaDirectory $media}catch{if($_.Exception.Message -like '*Bootstrap does not match*'){$rejectedOldBootstrap=$true}else{throw}}
if(-not $rejectedOldBootstrap){throw 'An old public bootstrap must not pass as the new signed client.'}
Write-Output "PASS staged hook ordering/PowerShell syntax, reviewed live publisher, real public ZIP privacy guards and all $($manifest.files.Count) bootstrap hashes. $clientOnly ClientOnly validation rules are not media packages. No GitHub calls or service changes."
