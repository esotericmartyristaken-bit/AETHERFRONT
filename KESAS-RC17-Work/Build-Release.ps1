$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression.FileSystem
$work=$PSScriptRoot
$root='.\KESAS-VPS-Server'
$release=Join-Path $work 'Release'
if(Test-Path -LiteralPath $release){throw 'Release exists; preserve immutable build artifacts.'}
$null=New-Item -ItemType Directory -Path $release
$utf8=[Text.UTF8Encoding]::new($false)
$catalog=Get-Content -LiteralPath "$work/../KESAS-RC16-Work/install-catalog.json" -Raw|ConvertFrom-Json
$catalog.version='kesas-rc17'
foreach($profile in @($catalog.full,$catalog.potato)){$profile.packVersion='kesas-rc17';$profile.requiredClientVersion='kesas-rc17'}
$replacement=@{
    'GameData/LunaMultiplayer/Plugins/LmpClient.dll'=[IO.File]::ReadAllBytes("$work/Patched/LmpClient.dll")
    'GameData/LunaMultiplayer/Plugins/ApfCore.dll'=[IO.File]::ReadAllBytes("$work/Patched/ApfCore.dll")
    'GameData/LunaMultiplayer/Plugins/KesasInstall.dll'=[IO.File]::ReadAllBytes("$work/Engine/bin/Release/net472/KesasInstall.dll")
    'GameData/LunaMultiplayer/APFApplyUpdate.ps1'=[IO.File]::ReadAllBytes("$work/APFApplyUpdate.ps1")
    'GameData/LunaMultiplayer/KESASLauncher.ps1'=[IO.File]::ReadAllBytes("$work/KESASLauncher.ps1")
    'GameData/LunaMultiplayer/Data/APFClient.json'=$utf8.GetBytes('{"enabled":true,"clientVersion":"kesas-rc17"}'+"`n")
    'GameData/LunaMultiplayer/Launcher/install-catalog.json'=$utf8.GetBytes(($catalog|ConvertTo-Json -Depth 40)+"`n")
    'KESAS/PLAYER-INSTALL.txt'=[IO.File]::ReadAllBytes("$work/PLAYER-INSTALL.txt")
}
$source=[IO.Compression.ZipFile]::OpenRead("$root/ClientTemplate.zip")
$target=[IO.Compression.ZipFile]::Open("$release/ClientTemplate.zip",[IO.Compression.ZipArchiveMode]::Create)
$inventory=$null
function Put([string]$name,[byte[]]$bytes){$stream=$target.CreateEntry($name,[IO.Compression.CompressionLevel]::Optimal).Open();try{$stream.Write($bytes,0,$bytes.Length)}finally{$stream.Dispose()}}
try{
    foreach($entry in $source.Entries){
        $name=$entry.FullName
        if($name -eq 'KESAS/PATCH-MANIFEST.json'){$reader=[IO.StreamReader]::new($entry.Open());try{$inventory=$reader.ReadToEnd()|ConvertFrom-Json}finally{$reader.Dispose()};continue}
        if($replacement.ContainsKey($name) -or $name -in @('GameData/LunaMultiplayer/Launcher/KesasLauncher.bin','GameData/LunaMultiplayer/Launcher/Launch KESAS.cmd','GameData/LunaMultiplayer/Launcher/default-profile.txt')){continue}
        $input=$entry.Open();$output=$target.CreateEntry($name,[IO.Compression.CompressionLevel]::Optimal).Open();try{$input.CopyTo($output)}finally{$input.Dispose();$output.Dispose()}
    }
    foreach($item in $replacement.GetEnumerator()){Put $item.Key $item.Value}
}finally{$source.Dispose();$target.Dispose()}
if(-not $inventory){throw 'Missing source inventory.'}
$target=[IO.Compression.ZipFile]::Open("$release/ClientTemplate.zip",[IO.Compression.ZipArchiveMode]::Update)
try{
    $records=@(foreach($entry in $target.Entries){$length=$entry.Length;$sha=[Security.Cryptography.SHA256]::Create();$stream=$entry.Open();try{$hash=[BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','').ToLowerInvariant()}finally{$sha.Dispose();$stream.Dispose()};[ordered]@{path=$entry.FullName;bytes=$length;sha256=$hash}})
    $inventory.version='kesas-rc17';$inventory.files=$records
    Put 'KESAS/PATCH-MANIFEST.json' ($utf8.GetBytes(($inventory|ConvertTo-Json -Depth 40)+"`n"))
}finally{$target.Dispose()}
$pack=Get-Content -LiteralPath "$root/Server/Config/APFPack.json" -Raw|ConvertFrom-Json
if($pack.requiredClientVersion -ne 'kesas-rc16'){throw 'Live server version changed; review source before publication.'}
$pack.requiredClientVersion='kesas-rc17';$pack.packVersion='kesas-rc17'
[IO.File]::WriteAllText("$release/APFPack-template.json",($pack|ConvertTo-Json -Depth 40)+"`n",$utf8)
Copy-Item -LiteralPath "$root/Server/Config/AetherfrontTechnology.json" -Destination $release
$names=@('ClientTemplate.zip','APFPack-template.json','AetherfrontTechnology.json')
$files=@($names|ForEach-Object{[ordered]@{path=$_;sha256=(Get-FileHash -LiteralPath (Join-Path $release $_)).Hash.ToLowerInvariant()}})
[IO.File]::WriteAllText("$release/RELEASE.json",([ordered]@{version='kesas-rc17';files=$files}|ConvertTo-Json -Depth 10)+"`n",$utf8)
$archive=[IO.Compression.ZipFile]::Open("$work/KESAS-Release-kesas-rc17.zip",[IO.Compression.ZipArchiveMode]::Create)
try{foreach($name in ($names+@('RELEASE.json'))){$null=[IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,(Join-Path $release $name),$name,[IO.Compression.CompressionLevel]::Optimal)}}finally{$archive.Dispose()}
Write-Output "RC17 built: $($records.Count+1) overlay files; existing 217 media archives retained."
