#requires -Version 7.0
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$work=$PSScriptRoot
$root=[IO.Path]::GetFullPath((Join-Path $work '../KESAS-VPS-Server'))
. "$root/Update.Common.ps1"
$utf8=[Text.UTF8Encoding]::new($false)
$release=Join-Path $work 'Release'
$candidate=Join-Path $work 'Candidate'
if([IO.Directory]::Exists($release) -or [IO.Directory]::Exists($candidate)){throw 'Immutable RC23 build output exists; inspect rather than overwrite.'}
$feed=Read-UpdateText "$root/Updates/APFClientUpdate.json" | ConvertFrom-Json
if($feed.version -cne 'kesas-rc22'){throw 'Live release changed; review RC23 base.'}
Invoke-UpdateTool $root @('--verify','--manifest',"$root/Updates/APFClientUpdate.json",'--archive',"$root/Updates/kesas-rc22/APFClient-kesas-rc22.zip",'--public-key-xml',"$root/secrets/update-signing-public.xml")
$comparison=Get-Content -LiteralPath "$work/Kcalbeloh-Comparison.json" -Raw | ConvertFrom-Json
if($comparison.archive4kSha256 -cne '57beb1c4342582da0ef44a9f838f24103583028f4c5eb2fa6ba33f5ce541c2b1' -or
    @($comparison.gameplayChanges).Count -or @($comparison.missingPaths).Count -or @($comparison.addedPaths).Count -or $comparison.files4k -ne 227){throw '4K comparison gate failed.'}
$newArchive="$work/Kcalbeloh.Textures-4k.zip"
if((Get-FileHash -LiteralPath $newArchive -Algorithm SHA256).Hash -ine $comparison.archive4kSha256 -or ([IO.FileInfo]$newArchive).Length -ne 696453837){throw 'Official archive changed.'}
New-UpdateDirectory $release
New-UpdateDirectory $candidate
Expand-UpdateArchive "$root/ClientTemplate.zip" "$candidate/Client"
$clientPath="$candidate/Client/GameData/LunaMultiplayer/Plugins/LmpClient.dll"
$clientHash=@($feed.files | Where-Object path -CEQ 'GameData/LunaMultiplayer/Plugins/LmpClient.dll')
if($clientHash.Count -ne 1 -or (Get-FileHash -LiteralPath $clientPath -Algorithm SHA256).Hash -ine $clientHash[0].sha256){throw 'Source template client is not the authenticated live client.'}
$dotnet=[IO.Path]::GetFullPath((Join-Path $work '../tools/dotnet-sdk8/dotnet.exe'))
& $dotnet build "$work/Patcher/Patcher.csproj" -c Release --nologo -v quiet
if($LASTEXITCODE){throw 'Version patcher build failed.'}
& $dotnet "$work/Patcher/bin/Release/net8.0/Patcher.dll" $clientPath $clientHash[0].sha256 "$work/Patched/LmpClient.dll"
if($LASTEXITCODE){throw 'Version-only client audit failed.'}
[IO.File]::Copy("$work/Patched/LmpClient.dll",$clientPath,$true)
$catalogPath="$candidate/Client/GameData/LunaMultiplayer/Launcher/install-catalog.json"
$catalog=Get-Content -LiteralPath $catalogPath -Raw | ConvertFrom-Json
if($catalog.version -cne 'kesas-rc22' -or !$catalog.gitHub.enabled -or $catalog.gitHub.repository -cne 'esotericmartyristaken-bit/AETHERFRONT'){throw 'Unexpected published catalog base.'}
$originalCatalog=$catalog | ConvertTo-Json -Depth 40
$oldTexture=@($catalog.potato.managedPackages | Where-Object id -CEQ 'kesas-kcalbelohtextures-44097afb')
if($oldTexture.Count -ne 1 -or $oldTexture[0].sha256 -cne $comparison.archive8kSha256){throw 'Potato source texture package mismatch.'}
$mediaName='kesas-kcalbelohtextures-4k-57beb1c4342582da.zip'
$newTexture=[pscustomobject][ordered]@{
    id='kesas-kcalbelohtextures-4k-v1.1.8';version='sha-57beb1c4342582da'
    archiveUrl="https://217.216.49.250:18866/$mediaName"
    sha256=$comparison.archive4kSha256;sizeBytes=696453837
    destination='GameData/KcalbelohTextures';stripPrefix='GameData/KcalbelohTextures'
}
$catalog.potato.managedPackages=@(foreach($package in $catalog.potato.managedPackages){if($package.id -ceq $oldTexture[0].id){$newTexture}else{$package}})
$catalog.sizes=@($catalog.sizes)+@([pscustomobject]@{id=$newTexture.id;expandedBytes=[long]$comparison.expandedBytes4k})
$catalog.version='kesas-rc23'
foreach($profile in @($catalog.full,$catalog.potato)){$profile.packVersion='kesas-rc23';$profile.requiredClientVersion='kesas-rc23'}
[IO.File]::WriteAllText($catalogPath,($catalog | ConvertTo-Json -Depth 40)+"`n",$utf8)
[IO.File]::WriteAllText("$candidate/Client/GameData/LunaMultiplayer/Data/APFClient.json",'{"enabled":true,"clientVersion":"kesas-rc23"}'+"`n",$utf8)
$lockPath="$candidate/Client/KESAS/CKAN-LOCK.json"
$lock=Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
$regularTexture=@($lock.modules | Where-Object identifier -CEQ 'KcalbelohSystem-Textures-8k')
if($regularTexture.Count -ne 1){throw 'Expected one CKAN texture variant.'}
# Preserve the original Regular CKAN module list. The install catalog is the
# authority for profile-specific packages; record its official texture sources.
$lock | Add-Member -NotePropertyName textureProfiles -NotePropertyValue ([ordered]@{
    full=$regularTexture[0]
    potato=[ordered]@{identifier='KcalbelohSystem-Textures-4k';version='v1.1.8';download=$comparison.official4kSource;download_hash=[ordered]@{sha1='B7F84530414E88E188C8D1EC97C20B4DD754FCF1';sha256=$comparison.archive4kSha256.ToUpperInvariant()};license='CC-BY-NC-ND-4.0'}
})
[IO.File]::WriteAllText($lockPath,($lock | ConvertTo-Json -Depth 40)+"`n",$utf8)
$instructions="`nPotato uses official Kcalbeloh 4K textures; Regular retains 8K. Terrain-height and biome maps are identical.`nInstallation continues after KSP closes. Do not start KSP yourself while files are being installed. Wait for its automatic restart. If installation fails, keep KSP closed and retain APF/APFUpdater.log and all recovery backups.`n"
[IO.File]::AppendAllText("$candidate/Client/KESAS/PLAYER-INSTALL.txt",$instructions,$utf8)
$original=$originalCatalog | ConvertFrom-Json
if(($original.full.managedPackages | ConvertTo-Json -Depth 20 -Compress) -cne ($catalog.full.managedPackages | ConvertTo-Json -Depth 20 -Compress)){throw 'Regular packages changed.'}
$unchangedOld=@($original.potato.managedPackages | Where-Object id -CNE $oldTexture[0].id)
$unchangedNew=@($catalog.potato.managedPackages | Where-Object id -CNE $newTexture.id)
if(($unchangedOld | ConvertTo-Json -Depth 20 -Compress) -cne ($unchangedNew | ConvertTo-Json -Depth 20 -Compress)){throw 'Unrelated Potato packages changed.'}
$inventoryPath="$candidate/Client/KESAS/PATCH-MANIFEST.json"
$inventory=Get-Content -LiteralPath $inventoryPath -Raw | ConvertFrom-Json
$inventory.version='kesas-rc23'
$clientRoot=[IO.Path]::GetFullPath("$candidate/Client")
$inventory.files=@(foreach($file in Get-ChildItem -LiteralPath $clientRoot -File -Recurse | Sort-Object FullName){
    $relative=$file.FullName.Substring($clientRoot.Length+1).Replace('\','/')
    if($relative -ceq 'KESAS/PATCH-MANIFEST.json'){continue}
    [ordered]@{path=$relative;bytes=$file.Length;sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
})
[IO.File]::WriteAllText($inventoryPath,($inventory | ConvertTo-Json -Depth 40)+"`n",$utf8)
[IO.Compression.ZipFile]::CreateFromDirectory($clientRoot,"$release/ClientTemplate.zip",[IO.Compression.CompressionLevel]::Optimal,$false)
$pack=Get-Content -LiteralPath "$root/Server/Config/APFPack.json" -Raw | ConvertFrom-Json
if($pack.requiredClientVersion -cne 'kesas-rc22' -or $pack.packVersion -cne 'kesas-rc22'){throw 'Server template base mismatch.'}
$pack.requiredClientVersion='kesas-rc23';$pack.packVersion='kesas-rc23'
[IO.File]::WriteAllText("$release/APFPack-template.json",($pack | ConvertTo-Json -Depth 40)+"`n",$utf8)
[IO.File]::Copy("$root/Server/Config/AetherfrontTechnology.json","$release/AetherfrontTechnology.json",$false)
$names=@('ClientTemplate.zip','APFPack-template.json','AetherfrontTechnology.json')
$releaseFiles=@(foreach($name in $names){[ordered]@{path=$name;sha256=(Get-FileHash -LiteralPath "$release/$name" -Algorithm SHA256).Hash.ToLowerInvariant()}})
[IO.File]::WriteAllText("$release/RELEASE.json",([ordered]@{version='kesas-rc23';files=$releaseFiles} | ConvertTo-Json -Depth 10)+"`n",$utf8)
$zip=[IO.Compression.ZipFile]::Open("$work/Aetherfront-Release-kesas-rc23.zip",[IO.Compression.ZipArchiveMode]::Create)
try{foreach($name in $names+@('RELEASE.json')){[void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,"$release/$name",$name,[IO.Compression.CompressionLevel]::Optimal)}}finally{$zip.Dispose()}
New-UpdateDirectory "$candidate/Published"
Invoke-UpdateTool $root @('--root-overlay','--source-root',$clientRoot,'--output',"$candidate/Published",'--version','kesas-rc23','--package-base-url','https://217.216.49.250:18866/updates/kesas-rc23/','--private-key-xml',"$root/secrets/update-signing-private.xml",'--ksp-version','1.12.5','--release-notes','Potato installs official Kcalbeloh 4K textures. Regular remains 8K.')
Invoke-UpdateTool $root @('--verify','--manifest',"$candidate/Published/APFClientUpdate.json",'--archive',"$candidate/Published/APFClient-kesas-rc23.zip",'--public-key-xml',"$root/secrets/update-signing-public.xml")
& "$root/Export-Client.ps1" -Template "$release/ClientTemplate.zip" -Output "$root/Aetherfront-RC23-TestExport.zip"
if(!$?){throw 'Candidate starter export failed.'}
[IO.File]::Copy("$root/Aetherfront-RC23-TestExport.zip","$candidate/Aetherfront-Starter-kesas-rc23.zip",$false)
$mediaPath="$root/Server/Config/$mediaName"
if([IO.File]::Exists($mediaPath)){
    if(([IO.FileInfo]$mediaPath).Length -ne 696453837 -or (Get-FileHash -LiteralPath $mediaPath -Algorithm SHA256).Hash -ine $comparison.archive4kSha256){throw 'Immutable media name collision.'}
}else{[IO.File]::Copy($newArchive,$mediaPath,$false)}
$next=Get-Content -LiteralPath "$candidate/Published/APFClientUpdate.json" -Raw | ConvertFrom-Json
$changedFiles=@(foreach($file in $next.files){$previous=@($feed.files | Where-Object path -CEQ $file.path);if($previous.Count -ne 1 -or $previous[0].sha256 -cne $file.sha256){$file.path}})
$report=[ordered]@{
    version='kesas-rc23';baseVersion='kesas-rc22';createdAtUtc=[DateTime]::UtcNow.ToString('o')
    signedFiles=@($next.files).Count;changedClientFiles=$changedFiles
    regularPackagesUnchanged=$true;unrelatedPotatoPackagesUnchanged=$true
    official4kBytes=696453837;official4kSha256=$comparison.archive4kSha256
    mediaName=$mediaName;newPotatoTexturePackage=$newTexture
    releaseSha256=(Get-FileHash -LiteralPath "$work/Aetherfront-Release-kesas-rc23.zip" -Algorithm SHA256).Hash.ToLowerInvariant()
    notActivated=$true
}
[IO.File]::WriteAllText("$work/BUILD-RESULT.json",($report | ConvertTo-Json -Depth 10)+"`n",$utf8)
$report | ConvertTo-Json -Depth 10
