#requires -Version 7.0
[CmdletBinding()]param()
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$work=$PSScriptRoot
$root=[IO.Path]::GetFullPath((Join-Path $work '../KESAS-VPS-Server'))
. "$root/Update.Common.ps1"
$utf8=[Text.UTF8Encoding]::new($false)
$release="$work/Release";$candidate="$work/Candidate"
if([IO.Directory]::Exists($release)-or[IO.Directory]::Exists($candidate)){throw 'Immutable RC24 build output exists; preserve previous attempts.'}
$feed=Read-UpdateText "$root/Updates/APFClientUpdate.json"|ConvertFrom-Json
if($feed.version -cne 'kesas-rc23'){throw 'Live release changed; review the RC24 base.'}
Invoke-UpdateTool $root @('--verify','--manifest',"$root/Updates/APFClientUpdate.json",'--archive',"$root/Updates/kesas-rc23/APFClient-kesas-rc23.zip",'--public-key-xml',"$root/secrets/update-signing-public.xml")
New-UpdateDirectory $release;New-UpdateDirectory $candidate
Expand-UpdateArchive "$root/ClientTemplate.zip" "$candidate/Client"
$client="$candidate/Client"
$plugins="$client/GameData/LunaMultiplayer/Plugins"
foreach($name in @('LmpClient.dll','ApfCore.dll')){[IO.File]::Copy("$work/Patched/$name","$plugins/$name",$true)}
[IO.File]::Copy("$work/Engine/bin/Release/net472/KesasInstall.dll","$plugins/KesasInstall.dll",$true)
[IO.File]::Copy("$work/NativeUi/bin/Release/net472/AetherfrontInstallUI.dll","$client/GameData/LunaMultiplayer/Launcher/AetherfrontInstallUI.bin",$false)
[IO.File]::Copy("$work/APFApplyUpdate.ps1","$client/GameData/LunaMultiplayer/APFApplyUpdate.ps1",$true)
[IO.File]::Copy("$work/PLAYER-INSTALL.txt","$client/KESAS/PLAYER-INSTALL.txt",$true)
[IO.File]::WriteAllText("$client/GameData/LunaMultiplayer/Data/APFClient.json",'{"enabled":true,"clientVersion":"kesas-rc24"}'+"`n",$utf8)
$catalogPath="$client/GameData/LunaMultiplayer/Launcher/install-catalog.json"
$catalog=Get-Content -LiteralPath $catalogPath -Raw|ConvertFrom-Json
if($catalog.version -cne 'kesas-rc23' -or !$catalog.gitHub.enabled){throw 'Unexpected install catalog baseline.'}
$declarations=@($catalog.full.managedPackages,$catalog.potato.managedPackages)|ConvertTo-Json -Depth 30 -Compress
$catalog.version='kesas-rc24'
foreach($profile in @($catalog.full,$catalog.potato)){$profile.packVersion='kesas-rc24';$profile.requiredClientVersion='kesas-rc24'}
if((@($catalog.full.managedPackages,$catalog.potato.managedPackages)|ConvertTo-Json -Depth 30 -Compress)-cne$declarations){throw 'Mod package declarations changed.'}
[IO.File]::WriteAllText($catalogPath,($catalog|ConvertTo-Json -Depth 40)+"`n",$utf8)
$inventoryPath="$client/KESAS/PATCH-MANIFEST.json"
$inventory=Get-Content -LiteralPath $inventoryPath -Raw|ConvertFrom-Json
$inventory.version='kesas-rc24'
$client=[IO.Path]::GetFullPath($client)
$inventory.files=@(foreach($file in Get-ChildItem -LiteralPath $client -File -Recurse|Sort-Object FullName){$relative=$file.FullName.Substring($client.Length+1).Replace('\','/');if($relative-ceq'KESAS/PATCH-MANIFEST.json'){continue};[ordered]@{path=$relative;bytes=$file.Length;sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}})
[IO.File]::WriteAllText($inventoryPath,($inventory|ConvertTo-Json -Depth 40)+"`n",$utf8)
[IO.Compression.ZipFile]::CreateFromDirectory($client,"$release/ClientTemplate.zip",[IO.Compression.CompressionLevel]::Optimal,$false)
$pack=Get-Content -LiteralPath "$root/Server/Config/APFPack.json" -Raw|ConvertFrom-Json
if($pack.requiredClientVersion-cne'kesas-rc23'-or$pack.packVersion-cne'kesas-rc23'){throw 'Server pack baseline changed.'}
$pack.requiredClientVersion='kesas-rc24';$pack.packVersion='kesas-rc24'
[IO.File]::WriteAllText("$release/APFPack-template.json",($pack|ConvertTo-Json -Depth 40)+"`n",$utf8)
[IO.File]::Copy("$root/Server/Config/AetherfrontTechnology.json","$release/AetherfrontTechnology.json",$false)
$names=@('ClientTemplate.zip','APFPack-template.json','AetherfrontTechnology.json')
$files=@(foreach($name in $names){[ordered]@{path=$name;sha256=(Get-FileHash -LiteralPath "$release/$name" -Algorithm SHA256).Hash.ToLowerInvariant()}})
[IO.File]::WriteAllText("$release/RELEASE.json",([ordered]@{version='kesas-rc24';files=$files}|ConvertTo-Json -Depth 10)+"`n",$utf8)
$zip=[IO.Compression.ZipFile]::Open("$work/Aetherfront-Release-kesas-rc24.zip",[IO.Compression.ZipArchiveMode]::Create)
try{foreach($name in $names+@('RELEASE.json')){[void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,"$release/$name",$name,[IO.Compression.CompressionLevel]::Optimal)}}finally{$zip.Dispose()}
New-UpdateDirectory "$candidate/Published"
Invoke-UpdateTool $root @('--root-overlay','--source-root',$client,'--output',"$candidate/Published",'--version','kesas-rc24','--package-base-url','https://217.216.49.250:18866/updates/kesas-rc24/','--private-key-xml',"$root/secrets/update-signing-private.xml",'--ksp-version','1.12.5','--release-notes','Visible installation progress, explicit restart, and approved permanent mod removal.')
Invoke-UpdateTool $root @('--verify','--manifest',"$candidate/Published/APFClientUpdate.json",'--archive',"$candidate/Published/APFClient-kesas-rc24.zip",'--public-key-xml',"$root/secrets/update-signing-public.xml")
& "$root/Export-Client.ps1" -Template "$release/ClientTemplate.zip" -Output "$root/Aetherfront-RC24-TestExport.zip"
if(!$?){throw 'Candidate starter export failed.'}
[IO.File]::Copy("$root/Aetherfront-RC24-TestExport.zip","$candidate/Aetherfront-Starter-kesas-rc24.zip",$false)
$next=Get-Content -LiteralPath "$candidate/Published/APFClientUpdate.json" -Raw|ConvertFrom-Json
$report=[ordered]@{version='kesas-rc24';baseVersion='kesas-rc23';createdAtUtc=[DateTime]::UtcNow.ToString('o');signedFiles=@($next.files).Count;packageDeclarationsUnchanged=$true;releaseSha256=(Get-FileHash -LiteralPath "$work/Aetherfront-Release-kesas-rc24.zip" -Algorithm SHA256).Hash.ToLowerInvariant();notActivated=$true}
[IO.File]::WriteAllText("$work/BUILD-RESULT.json",($report|ConvertTo-Json -Depth 10)+"`n",$utf8)
$report|ConvertTo-Json -Depth 10
