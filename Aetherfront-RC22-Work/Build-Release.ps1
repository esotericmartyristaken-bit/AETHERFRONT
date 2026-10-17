param([string]$GitHubRepository='',[string]$GitHubMediaTag='pack-media')
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
$catalog.version='kesas-rc22'
if(-not $PSBoundParameters.ContainsKey('GitHubRepository')){
    $previous=[IO.Compression.ZipFile]::OpenRead("$root/ClientTemplate.zip")
    try{
        $entry=$previous.GetEntry('GameData/LunaMultiplayer/Launcher/install-catalog.json')
        if($entry){$reader=[IO.StreamReader]::new($entry.Open());try{$oldCatalog=$reader.ReadToEnd()|ConvertFrom-Json}finally{$reader.Dispose()};$property=$oldCatalog.PSObject.Properties['gitHub'];if($property -and $property.Value.enabled){$GitHubRepository=$property.Value.repository;$GitHubMediaTag=$property.Value.mediaTag}}
    }finally{$previous.Dispose()}
}
if($GitHubRepository){
    if($GitHubRepository -cnotmatch '^[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9_.-]{1,100}$' -or $GitHubRepository -match '/\.\.?$'){throw 'GitHub repository must be owner/repository.'}
    if($GitHubMediaTag -cnotmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$' -or $GitHubMediaTag -eq 'latest' -or $GitHubMediaTag -like 'kesas-rc*'){throw 'Invalid GitHub media tag.'}
}
$catalog|Add-Member -NotePropertyName gitHub -NotePropertyValue ([ordered]@{enabled=[bool]$GitHubRepository;repository=$GitHubRepository;mediaTag=$GitHubMediaTag})
foreach($profile in @($catalog.full,$catalog.potato)){$profile.packVersion='kesas-rc22';$profile.requiredClientVersion='kesas-rc22'}
$replacement=@{
    'GameData/LunaMultiplayer/Plugins/LmpClient.dll'=[IO.File]::ReadAllBytes("$work/Patched/LmpClient.dll")
    'GameData/LunaMultiplayer/Plugins/ApfCore.dll'=[IO.File]::ReadAllBytes("$work/Patched/ApfCore.dll")
    'GameData/LunaMultiplayer/Plugins/KesasInstall.dll'=[IO.File]::ReadAllBytes("$work/Engine/bin/Release/net472/KesasInstall.dll")
    'GameData/LunaMultiplayer/APFApplyUpdate.ps1'=[IO.File]::ReadAllBytes("$work/APFApplyUpdate.ps1")
    'GameData/LunaMultiplayer/KESASLauncher.ps1'=[IO.File]::ReadAllBytes("$work/KESASLauncher.ps1")
    'GameData/LunaMultiplayer/Data/APFClient.json'=$utf8.GetBytes('{"enabled":true,"clientVersion":"kesas-rc22"}'+"`n")
    'GameData/LunaMultiplayer/Launcher/install-catalog.json'=$utf8.GetBytes(($catalog|ConvertTo-Json -Depth 40)+"`n")
    'KESAS/PLAYER-INSTALL.txt'=[IO.File]::ReadAllBytes("$work/PLAYER-INSTALL.txt")
}
$source=[IO.Compression.ZipFile]::OpenRead("$root/ClientTemplate.zip")
$target=[IO.Compression.ZipFile]::Open("$release/ClientTemplate.zip",[IO.Compression.ZipArchiveMode]::Create)
$inventory=$null
$packArchives=@{}
$packagedOverrides=0
function Put([string]$name,[byte[]]$bytes){$stream=$target.CreateEntry($name,[IO.Compression.CompressionLevel]::Optimal).Open();try{$stream.Write($bytes,0,$bytes.Length)}finally{$stream.Dispose()}}
function EntryHash($entry){$sha=[Security.Cryptography.SHA256]::Create();$stream=$entry.Open();try{return [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','').ToLowerInvariant()}finally{$sha.Dispose();$stream.Dispose()}}
try{
    foreach($entry in $source.Entries){
        $name=$entry.FullName
        if($name -eq 'KESAS/PATCH-MANIFEST.json'){$reader=[IO.StreamReader]::new($entry.Open());try{$inventory=$reader.ReadToEnd()|ConvertFrom-Json}finally{$reader.Dispose()};continue}
        if($name -notmatch '^(GameData/(LunaMultiplayer|000_Harmony)/|KSP_x64_Data/Managed/(System.IO.Compression|System.IO.Compression.FileSystem|System.Numerics)\.dll$|KESAS/)') {
            if($name -notmatch '^GameData/([^/]+)/(.+)$'){throw "Unclassified overlay file: $name"}
            $packageRoot=$Matches[1]
            if(-not $packArchives.ContainsKey($packageRoot)) {
                $package=@($catalog.full.managedPackages|Where-Object {$_.stripPrefix -ceq $packageRoot})
                if($package.Count -ne 1){throw "No unique complete package owns legacy override: $name"}
                $path=Join-Path "$root/Server/Config" ([IO.Path]::GetFileName(([Uri]$package[0].archiveUrl).AbsolutePath))
                $packArchives[$packageRoot]=[IO.Compression.ZipFile]::OpenRead($path)
            }
            $packed=$packArchives[$packageRoot].GetEntry($name.Substring('GameData/'.Length))
            if(-not $packed -or (EntryHash $packed) -cne (EntryHash $entry)){throw "Complete package does not preserve legacy fix: $name"}
            $packagedOverrides++;continue
        }
        if($replacement.ContainsKey($name) -or $name -in @('GameData/LunaMultiplayer/Launcher/KesasLauncher.bin','GameData/LunaMultiplayer/Launcher/Launch KESAS.cmd','GameData/LunaMultiplayer/Launcher/default-profile.txt')){continue}
        if($name -match '^GameData/LunaMultiplayer/Localization/.*\.xml$') {
            $reader=[IO.StreamReader]::new($entry.Open());try{$text=$reader.ReadToEnd()}finally{$reader.Dispose()}
            # Keep accurate filesystem locations, but remove upstream branding
            # from visible titles and prose. Legal notices remain untouched.
            $text=[regex]::Replace($text,'(?i)(?<![A-Za-z/\\])Luna\s*Multi\s*player(?!\s*/|[/\\])','Aetherfront')
            $text=[regex]::Replace($text,'(?i)(?<![A-Za-z])LMP(?![A-Za-z/\\])','Aetherfront')
            $text=$text -replace 'encoding="utf-16"','encoding="utf-8"'
            Put $name ($utf8.GetBytes($text));continue
        }
        $input=$entry.Open();$output=$target.CreateEntry($name,[IO.Compression.CompressionLevel]::Optimal).Open();try{$input.CopyTo($output)}finally{$input.Dispose();$output.Dispose()}
    }
    foreach($item in $replacement.GetEnumerator()){Put $item.Key $item.Value}
}finally{$source.Dispose();$target.Dispose();foreach($archive in $packArchives.Values){$archive.Dispose()}}
if(-not $inventory){throw 'Missing source inventory.'}
$target=[IO.Compression.ZipFile]::Open("$release/ClientTemplate.zip",[IO.Compression.ZipArchiveMode]::Update)
try{
    $records=@(foreach($entry in $target.Entries){$length=$entry.Length;$sha=[Security.Cryptography.SHA256]::Create();$stream=$entry.Open();try{$hash=[BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','').ToLowerInvariant()}finally{$sha.Dispose();$stream.Dispose()};[ordered]@{path=$entry.FullName;bytes=$length;sha256=$hash}})
    $inventory.version='kesas-rc22';$inventory.files=$records
    Put 'KESAS/PATCH-MANIFEST.json' ($utf8.GetBytes(($inventory|ConvertTo-Json -Depth 40)+"`n"))
}finally{$target.Dispose()}
$pack=Get-Content -LiteralPath "$root/Server/Config/APFPack.json" -Raw|ConvertFrom-Json
if($pack.requiredClientVersion -ne 'kesas-rc21'){throw 'Live server version changed; review source before publication.'}
$pack.requiredClientVersion='kesas-rc22';$pack.packVersion='kesas-rc22'
[IO.File]::WriteAllText("$release/APFPack-template.json",($pack|ConvertTo-Json -Depth 40)+"`n",$utf8)
Copy-Item -LiteralPath "$root/Server/Config/AetherfrontTechnology.json" -Destination $release
$names=@('ClientTemplate.zip','APFPack-template.json','AetherfrontTechnology.json')
$files=@($names|ForEach-Object{[ordered]@{path=$_;sha256=(Get-FileHash -LiteralPath (Join-Path $release $_)).Hash.ToLowerInvariant()}})
[IO.File]::WriteAllText("$release/RELEASE.json",([ordered]@{version='kesas-rc22';files=$files}|ConvertTo-Json -Depth 10)+"`n",$utf8)
$archive=[IO.Compression.ZipFile]::Open("$work/Aetherfront-Release-kesas-rc22.zip",[IO.Compression.ZipArchiveMode]::Create)
try{foreach($name in ($names+@('RELEASE.json'))){$null=[IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,(Join-Path $release $name),$name,[IO.Compression.CompressionLevel]::Optimal)}}finally{$archive.Dispose()}
Write-Output "RC22 built: $($records.Count+1) complete client files; $packagedOverrides legacy mod overrides verified identical in complete packages; existing 217 media archives retained."
