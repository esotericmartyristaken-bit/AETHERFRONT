[CmdletBinding()]
param([string]$Template='',[string]$Output='',[switch]$PublicationLockHeld)
. "$PSScriptRoot/Update.Common.ps1"
$root=Assert-UpdatePath $PSScriptRoot
$cfg=Get-UpdateDeployment $root
if(!$Template){$Template=Get-UpdateChildPath $root 'ClientTemplate.zip'}
if(!$Output){$Output=Get-UpdateChildPath $root 'KESAS-Client-Bootstrap.zip'}
$templatePath=Assert-UpdatePath $Template
$outputPath=Assert-UpdatePath $Output
if(!$outputPath.StartsWith($root.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Client export must stay inside this server folder'}
$releaseLock=$null
$temporary=$outputPath+'.tmp-'+[Guid]::NewGuid().ToString('N')
try{
    if(!$PublicationLockHeld){$releaseLock=New-Object IO.FileStream((Get-UpdateChildPath $root '.publish.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)}
    if(!$PublicationLockHeld -and [IO.File]::Exists((Get-UpdateChildPath $root '.update-transaction.json'))){throw 'An interrupted update needs recovery before exporting a bootstrap'}
    $public=Read-UpdateText (Get-UpdateChildPath $root 'secrets/update-signing-public.xml') 32768
    $readerSettings=New-Object Xml.XmlReaderSettings
    $readerSettings.DtdProcessing=[Xml.DtdProcessing]::Prohibit
    $readerSettings.XmlResolver=$null
    $reader=[Xml.XmlReader]::Create((New-Object IO.StringReader($public)),$readerSettings)
    $key=New-Object Xml.XmlDocument
    try{$key.Load($reader)}finally{$reader.Dispose()}
    if($key.DocumentElement.Name -cne 'RSAKeyValue' -or $key.DocumentElement.Attributes.Count -ne 0){throw 'Invalid public signing key'}
    $elements=@($key.DocumentElement.ChildNodes | Where-Object {$_.NodeType -eq [Xml.XmlNodeType]::Element})
    if($elements.Count -ne 2 -or @($elements | Where-Object {$_.Name -cnotin @('Modulus','Exponent') -or $_.Attributes.Count -ne 0}).Count -or @($elements | Where-Object {$_.Name -ceq 'Modulus'}).Count -ne 1){throw 'Public signing key must contain only Modulus and Exponent; private key export refused'}
    $rsa=New-Object Security.Cryptography.RSACryptoServiceProvider
    try{$rsa.PersistKeyInCsp=$false;$rsa.FromXmlString($public);if($rsa.KeySize -lt 2048){throw 'Public signing key is too small'};$public=$rsa.ToXmlString($false)}finally{$rsa.Dispose()}
    New-UpdateDirectory ([IO.Path]::GetDirectoryName($outputPath))
    [IO.File]::Copy($templatePath,$temporary,$false)
    $zip=[IO.Compression.ZipFile]::Open($temporary,[IO.Compression.ZipArchiveMode]::Update)
    try{
        $files=@{
            'GameData/LunaMultiplayer/Data/APFServer.txt'="$($cfg.address):$($cfg.gamePort)`n"
            'GameData/LunaMultiplayer/Data/APFTlsCertificatePins.txt'="$($cfg.certificateSha256)`n"
            'GameData/LunaMultiplayer/Data/APFClientUpdateManifestUrl.txt'="$($cfg.packOrigin)/updates/APFClientUpdate.json`n"
            'GameData/LunaMultiplayer/Data/APFClientUpdatePublicKey.xml'=$public
        }
        $seen=New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
        foreach($entry in $zip.Entries){
            $name=Get-UpdateRelativePath ($entry.FullName.TrimEnd('/'))
            if(!$seen.Add($name)){throw 'Client template contains duplicate archive paths'}
            if($files.ContainsKey($name)){throw "Client template must not contain server-specific settings: $name"}
            if(($entry.ExternalAttributes -shr 16 -band 0xF000) -eq 0xA000 -or ($entry.ExternalAttributes -band 0x400)){throw 'Client template contains a link'}
        }
        foreach($entry in $files.GetEnumerator()){
            $writer=New-Object IO.StreamWriter($zip.CreateEntry($entry.Key).Open(),$UpdateUtf8)
            try{$writer.Write($entry.Value)}finally{$writer.Dispose()}
        }
    }finally{$zip.Dispose()}
    if([IO.File]::Exists($outputPath)){[IO.File]::Replace($temporary,$outputPath,[System.Management.Automation.Language.NullString]::Value)}else{[IO.File]::Move($temporary,$outputPath)}
    $hash=(Get-FileHash -LiteralPath $outputPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-UpdateAtomic "$outputPath.sha256" "$hash  KESAS-Client-Bootstrap.zip`n"
    Write-Host "Exported $outputPath"
}finally{
    if([IO.File]::Exists($temporary)){[IO.File]::Delete($temporary)}
    if($releaseLock){$releaseLock.Dispose()}
}
