param([Parameter(Mandatory=$true)][string]$Archive,[Parameter(Mandatory=$true)][string]$ExpectedVersion)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

# Match the native publisher's Windows path and expansion limits before queueing.
# This is a preflight, not a replacement for its independent final validation.
function Require-ReleasePath([string]$Name) {
    if([string]::IsNullOrWhiteSpace($Name) -or $Name.Contains('\') -or $Name.StartsWith('/')){throw 'Unsafe release path'}
    foreach($segment in $Name.Split('/')) {
        if(!$segment -or $segment -in @('.','..') -or $segment -match '[<>:"|?*\x00-\x1f]|[. ]$' -or
            $segment -match '^(?i:CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM[1-9\u00b9\u00b2\u00b3]|LPT[1-9\u00b9\u00b2\u00b3])(?:\.|$)') {throw 'Unsafe release path'}
    }
}
$archivePath=[IO.Path]::GetFullPath($Archive)
if((Get-Item -LiteralPath $archivePath).Length -gt 2GB){throw 'Release archive exceeds 2 GB'}
$zip=[IO.Compression.ZipFile]::OpenRead($archivePath)
try {
    if($zip.Entries.Count -gt 20000){throw 'Release archive has too many entries'}
    $paths=[Collections.Generic.Dictionary[string,bool]]::new([StringComparer]::OrdinalIgnoreCase)
    [long]$expanded=0
    foreach($item in $zip.Entries) {
        $directory=$item.FullName.EndsWith('/')
        $name=if($directory){$item.FullName.Substring(0,$item.FullName.Length-1)}else{$item.FullName}
        Require-ReleasePath $name
        if($directory -and $item.Length -ne 0){throw 'Release directory contains file data'}
        if(($item.ExternalAttributes -shr 16 -band 0xF000) -eq 0xA000 -or ($item.ExternalAttributes -band 0x400)){throw 'Release contains a link or reparse point'}
        if($paths.ContainsKey($name)){throw 'Duplicate or case-colliding release entry'}
        $paths.Add($name,$directory)
        if($item.Length -gt 2GB-$expanded){throw 'Expanded release exceeds 2 GB'}
        $expanded+=$item.Length
    }
    foreach($name in @($paths.Keys)) {
        $parent=$name
        while($parent.Contains('/')) {
            $parent=$parent.Substring(0,$parent.LastIndexOf('/'))
            if($paths.ContainsKey($parent) -and !$paths[$parent]){throw 'Release file conflicts with a directory'}
        }
    }
    $entry=$zip.GetEntry('RELEASE.json')
    if(!$entry -or $entry.Length -gt 65536){throw 'Missing or oversized RELEASE.json'}
    $reader=[IO.StreamReader]::new($entry.Open())
    try{$release=$reader.ReadToEnd() | ConvertFrom-Json}finally{$reader.Dispose()}
    if($release.version -cne $ExpectedVersion){throw 'Release version does not match staged version'}
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($file in $release.files) {
        Require-ReleasePath $file.path
        if(!$seen.Add($file.path) -or
            ($file.path -cnotin @('ClientTemplate.zip','APFPack-template.json','AetherfrontTechnology.json') -and
             $file.path -cnotmatch '^(ServerPayload|PackHostPayload|CertificateToolPayload)/' -and
             $file.path -cnotmatch '^MediaPayload/kesas-[a-z0-9-]+-[a-f0-9]{64}\.zip$')){throw 'Unsupported or duplicate release path'}
        if($file.path -match '^(ServerPayload|PackHostPayload|CertificateToolPayload)/(Config|Universe|Aetherfront|Logs|secrets)(/|$)'){throw 'Protected server state in release'}
        $item=$zip.GetEntry($file.path)
        if(!$item -or $file.sha256 -notmatch '^[a-fA-F0-9]{64}$'){throw 'Missing release entry/hash'}
        $stream=$item.Open();$sha=[Security.Cryptography.SHA256]::Create()
        try {
            $buffer=New-Object byte[] 81920
            [long]$readTotal=0
            while(($count=$stream.Read($buffer,0,$buffer.Length)) -gt 0) {
                if($count -gt $item.Length-$readTotal){throw 'Release entry exceeds its declared length'}
                $null=$sha.TransformBlock($buffer,0,$count,$buffer,0)
                $readTotal+=$count
            }
            if($readTotal -ne $item.Length){throw 'Truncated release entry'}
            $null=$sha.TransformFinalBlock($buffer,0,0)
            $hash=[BitConverter]::ToString($sha.Hash).Replace('-','')
        }finally{$stream.Dispose();$sha.Dispose()}
        if($hash -ine $file.sha256){throw "Release hash mismatch: $($file.path)"}
    }
    if($zip.Entries.Count -ne $seen.Count+1){throw 'Undeclared or duplicate release entries'}
    foreach($name in @('ClientTemplate.zip','APFPack-template.json','AetherfrontTechnology.json')) {
        if(!$seen.Contains($name)){throw "Missing required release file: $name"}
    }
    Write-Output "Verified $ExpectedVersion"
}finally{$zip.Dispose()}
