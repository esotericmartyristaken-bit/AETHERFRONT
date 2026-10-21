#requires -Version 7.0
param([Parameter(Mandatory)][string]$ControlReleaseRoot,[Parameter(Mandatory)][string]$TestRoot,[Parameter(Mandatory)][string]$UpdateToolRoot,[switch]$PrepareOnly)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath($TestRoot)
if(Test-Path -LiteralPath $root){throw 'Fixture root must not exist'}
$native=Join-Path $ControlReleaseRoot 'KESAS-VPS-Server'
. "$native/Update.Common.ps1"
New-UpdateDirectory "$root/Server/Config"
New-UpdateDirectory "$root/Server/Universe"
New-UpdateDirectory "$root/Server/Aetherfront"
New-UpdateDirectory "$root/secrets"
Copy-UpdateTree $UpdateToolRoot "$root/UpdateTool"
$utf8=[Text.UTF8Encoding]::new($false)
function Put([string]$Path,[string]$Text){Write-UpdateAtomic $Path $Text}
function Json($Value){return ($Value|ConvertTo-Json -Depth 30 -Compress)}
function Zip([string]$Path,$Entries){
    $zip=[IO.Compression.ZipFile]::Open($Path,[IO.Compression.ZipArchiveMode]::Create)
    try{foreach($name in $Entries.Keys){$entry=$zip.CreateEntry($name);$stream=$entry.Open();try{$content=if($Entries[$name] -is [byte[]]){$Entries[$name]}else{$utf8.GetBytes([string]$Entries[$name])};$stream.Write($content,0,$content.Length)}finally{$stream.Dispose()}}}finally{$zip.Dispose()}
}
Put "$root/deployment.json" (Json @{address='127.0.0.1';gamePort=48861;packPort=48862;packOrigin='https://127.0.0.1:48862';certificateSha256=('a'*64)})
$rsa=[Security.Cryptography.RSA]::Create(2048)
try{Put "$root/secrets/update-signing-private.xml" $rsa.ToXmlString($true);Put "$root/secrets/update-signing-public.xml" $rsa.ToXmlString($false)}finally{$rsa.Dispose()}
Put "$root/Server/Config/APFPack.json" (Json @{serverId='fixture-server';packId='fixture-pack';packVersion='kesas-rc30';requiredClientVersion='kesas-rc30'})
Put "$root/Server/Config/operator.xml" '<mode>Sandbox</mode>'
Put "$root/Server/Universe/fixture.sfs" 'fixture Universe preserved'
Put "$root/Server/Aetherfront/identity.xml" 'fixture identity preserved'
$preserve=@('deployment.json','secrets/update-signing-private.xml','secrets/update-signing-public.xml','Server/Config/operator.xml','Server/Universe/fixture.sfs','Server/Aetherfront/identity.xml')
$before=@{};foreach($name in $preserve){$before[$name]=(Get-FileHash -LiteralPath "$root/$name").Hash}
New-UpdateDirectory "$root/Build/MediaPayload"
Zip "$root/Build/marker.zip" @{'ZZZ_KESASPotato/Profile.cfg'='KESAS_POTATO_COMPAT { enabled = true }'}
$mediaHash=(Get-FileHash -LiteralPath "$root/Build/marker.zip").Hash.ToLowerInvariant()
$mediaName="kesas-potato-marker-$mediaHash.zip"
[IO.File]::Move("$root/Build/marker.zip","$root/Build/MediaPayload/$mediaName")
$package=@{archiveUrl="https://example.invalid/$mediaName";sha256=$mediaHash;sizeBytes=([IO.FileInfo]"$root/Build/MediaPayload/$mediaName").Length}
$catalog=@{version='kesas-rc31';full=@{managedPackages=@($package)};potato=@{managedPackages=@($package)}}
$client=[ordered]@{
    'GameData/LunaMultiplayer/Launcher/install-catalog.json'=(Json $catalog)
    'GameData/LunaMultiplayer/Data/APFClient.json'='{"clientVersion":"kesas-rc31"}'
}
$template=[IO.Compression.ZipFile]::OpenRead((Join-Path (Split-Path -Parent $UpdateToolRoot) 'ClientTemplate.zip'))
try {
    foreach($entry in $template.Entries | Where-Object FullName -cmatch '^GameData/LunaMultiplayer/Plugins/[^/]+\.dll$') {
        $memory=[IO.MemoryStream]::new();$stream=$entry.Open();try{$stream.CopyTo($memory);$client[$entry.FullName]=$memory.ToArray()}finally{$stream.Dispose();$memory.Dispose()}
    }
} finally {$template.Dispose()}
$inventory=@(foreach($name in $client.Keys){$bytes=if($client[$name] -is [byte[]]){$client[$name]}else{$utf8.GetBytes($client[$name])};@{path=$name;bytes=$bytes.Length;sha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()}})
$client['KESAS/PATCH-MANIFEST.json']=(Json @{version='kesas-rc31';files=$inventory})
Zip "$root/Build/ClientTemplate.zip" $client
Put "$root/Build/APFPack-template.json" (Json @{serverId='replaced-in-fixture';packId='replaced-in-fixture';packVersion='kesas-rc31';requiredClientVersion='kesas-rc31'})
Put "$root/Build/AetherfrontTechnology.json" '{}'
$files=@(foreach($name in @('ClientTemplate.zip','APFPack-template.json','AetherfrontTechnology.json',"MediaPayload/$mediaName")){@{path=$name;sha256=(Get-FileHash -LiteralPath "$root/Build/$name").Hash.ToLowerInvariant()}})
Put "$root/Build/RELEASE.json" (Json @{version='kesas-rc31';files=$files})
$archive="$root/Aetherfront-Release-kesas-rc31.zip"
$zip=[IO.Compression.ZipFile]::Open($archive,[IO.Compression.ZipArchiveMode]::Create)
try{foreach($name in @('RELEASE.json')+@($files|ForEach-Object path)){$null=[IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,"$root/Build/$name",$name)}}finally{$zip.Dispose()}
if($PrepareOnly){
    Write-Output (Json @{root=$root;archive=$archive;version='kesas-rc31';sha256=(Get-FileHash -LiteralPath $archive).Hash.ToLowerInvariant();sizeBytes=([IO.FileInfo]$archive).Length})
    return
}
& "$ControlReleaseRoot/APFDiscordBot/scripts/Verify-KesasRelease.ps1" -Archive $archive -ExpectedVersion 'kesas-rc31'
& "$native/Publish-Release.ps1" -DeploymentRoot $root -ReleaseZip $archive -Notes 'Isolated media fixture'
$feed=Read-UpdateText "$root/Updates/APFClientUpdate.json"|ConvertFrom-Json
$pack=Read-UpdateText "$root/Server/Config/APFPack.json"|ConvertFrom-Json
if($feed.version -cne 'kesas-rc31' -or $pack.packVersion -cne 'kesas-rc31' -or $pack.serverId -cne 'fixture-server' -or $pack.packId -cne 'fixture-pack'){throw 'Fixture activation version or identity mismatch'}
if((Get-FileHash -LiteralPath "$root/Server/Config/$mediaName").Hash -ine $mediaHash){throw 'Public media not deployed'}
foreach($name in $preserve){if((Get-FileHash -LiteralPath "$root/$name").Hash -cne $before[$name]){throw 'Protected fixture changed'}}
if(Test-Path -LiteralPath "$root/.update-transaction.json"){throw 'Fixture completion journal remains pending'}
Write-Output 'PASS real native publisher, disposable signing keys, signed client feed, new media and protected state; no live services changed.'
