param([Parameter(Mandatory=$true)][string]$Archive,[Parameter(Mandatory=$true)][string]$ExpectedVersion)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Assert-ReleaseJsonUnique([string]$Json) {
    $null=ConvertFrom-Json -InputObject $Json -ErrorAction Stop
    $stack=New-Object 'Collections.Generic.Stack[object]'
    foreach($token in [regex]::Matches($Json,'"(?:\\.|[^"\\])*"|[{}\[\]:,]')){
        $value=$token.Value
        if($value -ceq '{'){$stack.Push([pscustomobject]@{array=$false;key=$true;seen=(New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal))});continue}
        if($value -ceq '['){$stack.Push([pscustomobject]@{array=$true;key=$false;seen=$null});continue}
        if($value -ceq '}' -or $value -ceq ']'){$null=$stack.Pop();continue}
        if(-not $stack.Count){continue}
        $top=$stack.Peek()
        if($value -ceq ',' -and -not $top.array){$top.key=$true;continue}
        if($value.StartsWith('"') -and $top.key){
            $name=ConvertFrom-Json -InputObject $value
            if(-not $top.seen.Add([string]$name)){throw 'Duplicate release JSON property'}
            $top.key=$false
        }
    }
}

# Match the native publisher's Windows path and expansion limits before queueing.
# This is a preflight, not a replacement for its independent final validation.
function Require-ReleasePath([string]$Name) {
    if([string]::IsNullOrWhiteSpace($Name) -or $Name.Contains('\') -or $Name.StartsWith('/')){throw 'Unsafe release path'}
    foreach($segment in $Name.Split('/')) {
        if(!$segment -or $segment -in @('.','..') -or $segment -match '[<>:"|?*\x00-\x1f]|[. ]$' -or
            $segment -match '^(?i:CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM[1-9\u00b9\u00b2\u00b3]|LPT[1-9\u00b9\u00b2\u00b3])(?:\.|$)') {throw 'Unsafe release path'}
    }
}

function Require-ReferenceFields($Value,[string[]]$Fields) {
    if($null -eq $Value -or $Value -isnot [pscustomobject] -or @($Value.PSObject.Properties).Count -ne $Fields.Count -or
        @($Value.PSObject.Properties | Where-Object Name -cnotin $Fields).Count){throw 'Unsupported media reference schema'}
}
function Read-ReleaseJsonEntry($Entry) {
    if($null -eq $Entry -or $Entry.Length -gt 1MB){throw 'Missing or oversized media reference/catalog JSON'}
    $inputStream=$Entry.Open();$memory=[IO.MemoryStream]::new()
    try{
        $buffer=[byte[]]::new(81920);[long]$total=0
        while(($count=$inputStream.Read($buffer,0,$buffer.Length)) -gt 0){
            if($count -gt $Entry.Length-$total -or $count -gt 1MB-$total){throw 'Media reference/catalog JSON exceeds declared size'}
            $total+=$count;$memory.Write($buffer,0,$count)
        }
        if($total -ne $Entry.Length){throw 'Truncated media reference/catalog JSON'}
        $text=([Text.UTF8Encoding]::new($false,$true)).GetString($memory.ToArray()).TrimStart([char]0xFEFF)
        Assert-ReleaseJsonUnique $text
        return ($text | ConvertFrom-Json)
    }finally{$inputStream.Dispose();$memory.Dispose()}
}
function Require-ReleaseMediaReferences($Document,$Catalog) {
    # Independent validation: the fixed privileged verifier cannot depend on
    # remotely selected publisher functions or a locally writable catalog.
    Require-ReferenceFields $Document @('schemaVersion','applicationVersion','repository','tag','assets')
    if(($Document.schemaVersion -isnot [int] -and $Document.schemaVersion -isnot [long]) -or $Document.schemaVersion -ne 1 -or
        $Document.applicationVersion -isnot [string] -or $Document.applicationVersion -cne $ExpectedVersion -or
        $ExpectedVersion -cnotmatch '^kesas-rc[1-9][0-9]{0,14}$' -or $Catalog.version -isnot [string] -or $Catalog.version -cne $ExpectedVersion -or
        $Document.repository -isnot [string] -or $Document.repository -cne 'esotericmartyristaken-bit/AETHERFRONT' -or
        $Document.tag -isnot [string] -or $Document.tag -cne 'pack-media' -or
        $Catalog.gitHub.enabled -isnot [bool] -or -not $Catalog.gitHub.enabled -or
        $Catalog.gitHub.repository -isnot [string] -or $Catalog.gitHub.repository -cne $Document.repository -or
        $Catalog.gitHub.mediaTag -isnot [string] -or $Catalog.gitHub.mediaTag -cne $Document.tag -or
        $Document.assets -isnot [array] -or $Document.assets.Count -lt 1 -or $Document.assets.Count -gt 1000){throw 'Unsupported media reference authority, version, or bounds'}
    $names=@{};[long]$total=0
    foreach($asset in $Document.assets){
        Require-ReferenceFields $asset @('name','sizeBytes','sha256')
        if($asset.name -isnot [string] -or $asset.name.Length -gt 200 -or
            $asset.name -cnotmatch '^kesas-[a-z0-9-]+-([a-f0-9]{64})\.zip$' -or $Matches[1] -cne $asset.sha256 -or
            $asset.sha256 -isnot [string] -or $asset.sha256 -cnotmatch '^[a-f0-9]{64}$' -or
            ($asset.sizeBytes -isnot [int] -and $asset.sizeBytes -isnot [long]) -or $asset.sizeBytes -le 0 -or $asset.sizeBytes -ge 2GB -or $names.ContainsKey($asset.name) -or
            $asset.name -match '(?i)(^|[ ._()-])(secrets?|private(?:key)?|credentials?|universe|state|deployment)([ ._()-]|$)'){throw 'Invalid or duplicate immutable media reference'}
        if($seen.Contains('MediaPayload/'+$asset.name)){throw 'Media cannot be both embedded and referenced'}
        $names[$asset.name]=$true;$total+=$asset.sizeBytes
        if($total -gt 8GB){throw 'Referenced media batch exceeds 8 GiB'}
        $matchesFound=0
        foreach($profileName in @('full','potato')){
            $profile=$Catalog.$profileName
            if($null -eq $profile -or $null -eq $profile.managedPackages){throw 'Missing signed catalog package list'}
            foreach($package in @($profile.managedPackages)){
                $uri=$null
                if(-not [Uri]::TryCreate([string]$package.archiveUrl,[UriKind]::Absolute,[ref]$uri)){throw 'Invalid catalog media URL'}
                $publicUrl='https://github.com/esotericmartyristaken-bit/AETHERFRONT/releases/download/pack-media/'+$asset.name
                $packageName=[Uri]::UnescapeDataString(($uri.AbsolutePath -split '/')[-1])
                if($packageName -ine $asset.name){continue}
                if($package.archiveUrl -isnot [string] -or $packageName -cne $asset.name -or $package.archiveUrl.Contains('%') -or
                    ($uri.AbsolutePath -cne ('/'+$asset.name) -and $package.archiveUrl -cne $publicUrl) -or
                    $uri.Scheme -cne 'https' -or $uri.UserInfo -or $uri.Query -or $uri.Fragment -or
                    $package.archiveUrl -match '(?i)\\|%2f|%5c|(?:^|/)(?:\.|%2e){1,2}(?:/|$)' -or
                    $package.sha256 -isnot [string] -or $package.sha256 -cne $asset.sha256 -or
                    ($package.sizeBytes -isnot [int] -and $package.sizeBytes -isnot [long]) -or $package.sizeBytes -ne $asset.sizeBytes){throw 'Media reference differs from a referring signed catalog package'}
                $matchesFound++
            }
        }
        if(-not $matchesFound){throw 'Media reference has no matching signed catalog package'}
    }
}
function Read-ReleaseMediaCatalog($ClientEntry) {
    # ZipArchive buffers an entire non-seekable nested ZIP. Spool only this
    # hash-verified template to private disk instead; never load it into RAM.
    $temporary=[IO.Path]::Combine([IO.Path]::GetTempPath(),'Aetherfront-media-verify-'+[Guid]::NewGuid().ToString('N'))
    for($cursor=[IO.Path]::GetDirectoryName($temporary);$cursor;$cursor=[IO.Path]::GetDirectoryName($cursor)){
        if([IO.File]::GetAttributes($cursor) -band [IO.FileAttributes]::ReparsePoint){throw 'Media verification staging contains a reparse point'}
    }
    $clientPath=[IO.Path]::Combine($temporary,'ClientTemplate.zip')
    $null=[IO.Directory]::CreateDirectory($temporary)
    try{
        $acl=[Security.AccessControl.DirectorySecurity]::new()
        $acl.SetAccessRuleProtection($true,$false)
        foreach($sid in @([Security.Principal.WindowsIdentity]::GetCurrent().User,([Security.Principal.SecurityIdentifier]::new('S-1-5-18')),([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')))){
            $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,'FullControl','ContainerInherit,ObjectInherit','None','Allow'))
        }
        if($PSVersionTable.PSEdition -eq 'Desktop'){[IO.Directory]::SetAccessControl($temporary,$acl)}else{[IO.FileSystemAclExtensions]::SetAccessControl([IO.DirectoryInfo]::new($temporary),$acl)}
        $inputStream=$ClientEntry.Open()
        try{
            $outputStream=[IO.FileStream]::new($clientPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
            try{
                $buffer=[byte[]]::new(1MB);[long]$total=0
                while(($count=$inputStream.Read($buffer,0,$buffer.Length)) -gt 0){
                    if($count -gt $ClientEntry.Length-$total){throw 'Client template exceeds declared size'}
                    $total+=$count;$outputStream.Write($buffer,0,$count)
                }
                if($total -ne $ClientEntry.Length){throw 'Truncated client template'}
            }finally{$outputStream.Dispose()}
        }finally{$inputStream.Dispose()}
        $client=[IO.Compression.ZipFile]::OpenRead($clientPath)
        try{
            $name='GameData/LunaMultiplayer/Launcher/install-catalog.json'
            $catalogs=@($client.Entries | Where-Object {$_.FullName -ieq $name})
            if($catalogs.Count -ne 1 -or $catalogs[0].FullName -cne $name){throw 'Missing or ambiguous application catalog'}
            return (Read-ReleaseJsonEntry $catalogs[0])
        }finally{$client.Dispose()}
    }finally{
        if([IO.File]::Exists($clientPath)){[IO.File]::Delete($clientPath)}
        [IO.Directory]::Delete($temporary,$false)
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
    try{$releaseText=$reader.ReadToEnd();Assert-ReleaseJsonUnique $releaseText;$release=$releaseText | ConvertFrom-Json}finally{$reader.Dispose()}
    if(@($release.PSObject.Properties).Count -ne 2 -or @($release.PSObject.Properties | Where-Object Name -cnotin @('version','files')).Count -or $release.version -isnot [string] -or $release.files -isnot [array]){throw 'Unsupported release manifest schema'}
    if($release.version -cne $ExpectedVersion){throw 'Release version does not match staged version'}
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($file in $release.files) {
        if(@($file.PSObject.Properties).Count -ne 2 -or @($file.PSObject.Properties | Where-Object Name -cnotin @('path','sha256')).Count -or $file.path -isnot [string] -or $file.sha256 -isnot [string]){throw 'Unsupported release file schema'}
        Require-ReleasePath $file.path
        if(!$seen.Add($file.path) -or
            ($file.path -cnotin @('ClientTemplate.zip','APFPack-template.json','AetherfrontTechnology.json','MediaReferences.json') -and
             $file.path -cnotmatch '^(ServerPayload|PackHostPayload|CertificateToolPayload|UpdateToolPayload)/' -and
             $file.path -cnotmatch '^MediaPayload/kesas-[a-z0-9-]+-[a-f0-9]{64}\.zip$')){throw 'Unsupported or duplicate release path'}
        if($file.path -match '^(ServerPayload|PackHostPayload|CertificateToolPayload|UpdateToolPayload)/(Config|Universe|Aetherfront|Logs|secrets)(/|$)'){throw 'Protected server state in release'}
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
    if($seen.Contains('MediaReferences.json')){
        $document=Read-ReleaseJsonEntry ($zip.GetEntry('MediaReferences.json'))
        $catalog=Read-ReleaseMediaCatalog ($zip.GetEntry('ClientTemplate.zip'))
        Require-ReleaseMediaReferences $document $catalog
    }
    Write-Output "Verified $ExpectedVersion"
}finally{$zip.Dispose()}
