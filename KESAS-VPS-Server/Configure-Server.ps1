[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Address,
    [ValidateRange(1024,65535)][int]$GamePort=18865,
    [ValidateRange(1024,65535)][int]$PackPort=18866,
    [string]$Name='KESAS Science'
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=$PSScriptRoot
$ip=$null
if (![Net.IPAddress]::TryParse($Address,[ref]$ip) -or $ip.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) { throw 'Use the literal public IPv4 address of this Windows VPS.' }
$bytes=$ip.GetAddressBytes()
if ($bytes[0] -eq 0 -or $bytes[0] -ge 224) { throw 'Use a unicast IPv4 address.' }
if ($GamePort -eq $PackPort) { throw 'Use distinct game and HTTPS ports.' }
if (Test-Path -LiteralPath "$root/deployment.json") { throw 'Already configured. Preserve deployment.json, secrets, Server/Config, Server/Universe and Server/Aetherfront during upgrades.' }
if (Test-Path -LiteralPath "$root/secrets") { throw 'Existing secrets found. Inspect an interrupted setup before retrying; do not replace the certificate.' }
$null=New-Item -ItemType Directory -Path "$root/secrets"
$identity=[Security.Principal.WindowsIdentity]::GetCurrent().Name
& icacls.exe "$root/secrets" /inheritance:r /grant:r "${identity}:(OI)(CI)F" '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not restrict certificate directory permissions.' }
& "$root/CertificateTool/ApfCertificateTool.exe" --server-pfx $Address "$root/secrets/pack.pfx" "$root/secrets/pin.txt"
if ($LASTEXITCODE -ne 0) { throw 'Certificate generation failed.' }
$pin=(Get-Content -LiteralPath "$root/secrets/pin.txt" -Raw).Trim()
$utf8=New-Object Text.UTF8Encoding($false)
function Write-ConfigXml([string]$File,[hashtable]$Values) {
    $doc=New-Object Xml.XmlDocument
    $element=$doc.CreateElement($File+'Definition');$null=$doc.AppendChild($element)
    foreach($key in $Values.Keys) { $node=$doc.CreateElement($key);$node.InnerText=[string]$Values[$key];$null=$element.AppendChild($node) }
    $doc.Save("$root/Server/Config/$File.xml")
}
Write-ConfigXml 'ConnectionSettings' @{ListenAddress='0.0.0.0';Port=$GamePort;Upnp='false'}
Write-ConfigXml 'GeneralSettings' @{ServerName=$Name;Description='KESAS RC1 science multiplayer';GameMode='Science';MaxPlayers=8;Password='';ModControl='false';NumberOfAsteroids=0;NumberOfComets=0;AutoDekessler=0}
Write-ConfigXml 'MasterServerSettings' @{RegisterWithMasterServer='false'}
Write-ConfigXml 'DebugSettings' @{CustomMasterServer='';EnableDebugMessages='false'}
Write-ConfigXml 'WebsiteSettings' @{EnableWebsite='false';ListenAddress='127.0.0.1'}
[IO.File]::WriteAllText("$root/Server/Config/APFDirectServer.txt","true`n",$utf8)
$origin="https://${Address}:$PackPort"
[IO.File]::WriteAllText("$root/Server/Config/APFManifestUrl.txt","$origin/APFPack.json`n",$utf8)
$manifest=Get-Content -LiteralPath "$root/Server/Config/APFPack.json" -Raw | ConvertFrom-Json
$manifest.serverId='kesas-'+[Guid]::NewGuid().ToString('N').Substring(0,16)
[IO.File]::WriteAllText("$root/Server/Config/APFPack.json",($manifest|ConvertTo-Json -Depth 30),$utf8)
$settings=@{address=$Address;gamePort=$GamePort;packPort=$PackPort;packOrigin=$origin;certificateSha256=$pin;serverId=$manifest.serverId;certificateExpires=(Get-Date).AddDays(180).ToUniversalTime().ToString('o')}
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$patch="$root/KESAS-Client-Patches-rc1.zip"
Copy-Item -LiteralPath "$root/ClientTemplate.zip" -Destination $patch
$zip=[IO.Compression.ZipFile]::Open($patch,[IO.Compression.ZipArchiveMode]::Update)
try {
    foreach($entry in @{'GameData/LunaMultiplayer/Data/APFServer.txt'="${Address}:$GamePort`n";'GameData/LunaMultiplayer/Data/APFTlsCertificatePins.txt'="$pin`n"}.GetEnumerator()) {
        if($zip.GetEntry($entry.Key)) { throw "Client template unexpectedly contains $($entry.Key)" }
        $stream=$zip.CreateEntry($entry.Key).Open();$writer=New-Object IO.StreamWriter($stream,$utf8)
        try {$writer.Write($entry.Value)} finally {$writer.Dispose()}
    }
} finally {$zip.Dispose()}
$hash=(Get-FileHash -LiteralPath $patch -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$patch.sha256","$hash  KESAS-Client-Patches-rc1.zip`n",$utf8)
[IO.File]::WriteAllText("$root/deployment.json",($settings|ConvertTo-Json),$utf8)
Write-Host "Configured $Name. Give testers KESAS-rc1.ckan and KESAS-Client-Patches-rc1.zip."
Write-Host "Allow inbound UDP $GamePort and TCP $PackPort in Windows Firewall and the VPS provider firewall."
Write-Host 'Keep secrets/ private. Start with Start-Server.ps1. Certificate expires in 180 days.'
