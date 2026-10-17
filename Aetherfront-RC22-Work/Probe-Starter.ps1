param([Parameter(Mandatory=$true)][string]$GameRoot)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$runtime=Join-Path $GameRoot ('APF/.starter-probe/'+[Guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $runtime -Force
foreach($name in @('JsonFx.dll','ApfCore.dll','KesasInstall.dll')){
    $source=Join-Path $GameRoot ('GameData/LunaMultiplayer/Plugins/'+$name)
    $destination=Join-Path $runtime $name
    [IO.File]::WriteAllBytes($destination,[IO.File]::ReadAllBytes($source))
    [Reflection.Assembly]::LoadFrom($destination)|Out-Null
}
[Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls
$endpoint=[Net.IPEndPoint]::new([Net.IPAddress]::Parse('217.216.49.250'),18865)
$installer=[KesasInstall.Installer]::new($GameRoot,$endpoint,'kesas-rc22')
if(-not ([Net.ServicePointManager]::SecurityProtocol -band [Net.SecurityProtocolType]::Tls12)){throw 'TLS 1.2 was not enabled.'}
$destination=Join-Path $GameRoot 'APF/native-feed-probe.json'
$method=[KesasInstall.Installer].GetMethod('Download',[Reflection.BindingFlags]'Instance,NonPublic')
$null=$method.Invoke($installer,[object[]]@([Uri]'https://217.216.49.250:18866/updates/APFClientUpdate.json',[string]$destination,[long](4*1024*1024),$null))
$manifest=[ApfCore.ApfClientUpdateManifest]::FromJson([IO.File]::ReadAllText($destination))
if(-not [ApfCore.ApfClientUpdateValidator]::Validate($manifest).IsValid){throw 'Invalid live client feed.'}
$key=[ApfCore.ApfClientUpdateSecurity]::LoadXmlPublicKey([IO.File]::ReadAllText((Join-Path $GameRoot 'GameData/LunaMultiplayer/Data/APFClientUpdatePublicKey.xml')))
try{if(-not [ApfCore.ApfClientUpdateSecurity]::Verify($manifest,$key)){throw 'Live feed signature failed.'}}finally{$key.Dispose()}
Write-Output ('PASS Windows PowerShell 5.1, downloaded-file blocking recovery, selected-IP pinned HTTPS and signed live feed '+$manifest.Version+'.')
$candidate=[ApfCore.ApfClientUpdateManifest]::FromJson([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Candidate/Published/APFClientUpdate.json')))
$matches=[KesasInstall.Installer].GetMethod('ClientFilesMatch',[Reflection.BindingFlags]'Instance,NonPublic')
if(-not $matches.Invoke($installer,[object[]]@($candidate))){throw 'Fresh starter would cause an unnecessary client repair.'}
Write-Output ('PASS complete starter matches all '+$candidate.Files.Count+' signed client files; no first-connect client repair required.')
