$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$UpdateUtf8=New-Object Text.UTF8Encoding($false)

function Assert-UpdatePath([string]$Path) {
    $full=[IO.Path]::GetFullPath($Path)
    # Absolute caller-supplied paths need the same Win32 name checks as ZIP
    # entries. GetFullPath alone accepts alternate data streams and device names.
    $pathRoot=[IO.Path]::GetPathRoot($full)
    $tail=$full.Substring($pathRoot.Length).Replace('\','/').TrimEnd('/')
    if($tail){$null=Get-UpdateRelativePath $tail}
    $cursor=$full
    while($cursor){
        try{$attributes=[IO.File]::GetAttributes($cursor)}
        catch [IO.FileNotFoundException]{$attributes=0}
        catch [IO.DirectoryNotFoundException]{$attributes=0}
        if($attributes -band [IO.FileAttributes]::ReparsePoint){throw "Refusing reparse point: $cursor"}
        $cursor=[IO.Path]::GetDirectoryName($cursor)
    }
    return $full
}
function Assert-UpdateTree([string]$Path) {
    $full=Assert-UpdatePath $Path
    if(![IO.Directory]::Exists($full)){return}
    $pending=New-Object 'Collections.Generic.Stack[string]'
    $pending.Push($full)
    while($pending.Count){
        foreach($item in [IO.Directory]::EnumerateFileSystemEntries($pending.Pop())){
            $attributes=[IO.File]::GetAttributes($item)
            if($attributes -band [IO.FileAttributes]::ReparsePoint){throw "Refusing reparse point: $item"}
            if($attributes -band [IO.FileAttributes]::Directory){$pending.Push($item)}
        }
    }
}
function New-UpdateDirectory([string]$Path) {
    $full=Assert-UpdatePath $Path
    $null=[IO.Directory]::CreateDirectory($full)
}
function Read-UpdateText([string]$Path,[long]$MaximumBytes=16777216) {
    $full=Assert-UpdatePath $Path
    $textInput=New-Object IO.FileStream($full,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try{
        if($MaximumBytes -lt 0 -or $textInput.Length -gt $MaximumBytes){throw 'Update text exceeds its safety limit'}
        $reader=New-Object IO.StreamReader($textInput,$UpdateUtf8,$true)
        try{return $reader.ReadToEnd()}finally{$reader.Dispose()}
    }finally{$textInput.Dispose()}
}
function Get-UpdateRelativePath([string]$Name) {
    if([string]::IsNullOrWhiteSpace($Name) -or $Name.Contains('\') -or $Name.StartsWith('/')){throw "Unsafe archive/release path: $Name"}
    foreach($segment in $Name.Split('/')){
        if(!$segment -or $segment -eq '.' -or $segment -eq '..' -or $segment -match '[<>:"|?*\x00-\x1f]' -or $segment -match '[. ]$' -or $segment -match '^(?i:CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM[1-9\u00b9\u00b2\u00b3]|LPT[1-9\u00b9\u00b2\u00b3])(?:\.|$)'){throw "Unsafe archive/release path: $Name"}
    }
    return $Name
}
function Get-UpdateChildPath([string]$Root,[string]$Relative) {
    $null=Get-UpdateRelativePath $Relative
    $base=[IO.Path]::GetFullPath($Root).TrimEnd('\')
    $path=[IO.Path]::GetFullPath([IO.Path]::Combine($base,$Relative.Replace('/','\')))
    if(!$path.StartsWith($base+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Path escapes update root'}
    return (Assert-UpdatePath $path)
}
function Write-UpdateAtomic([string]$Path,[string]$Text) {
    $full=Assert-UpdatePath $Path
    New-UpdateDirectory ([IO.Path]::GetDirectoryName($full))
    $temp=$full+'.tmp-'+[Guid]::NewGuid().ToString('N')
    try{
        $bytes=$UpdateUtf8.GetBytes($Text)
        $stream=New-Object IO.FileStream($temp,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
        try{$stream.Write($bytes,0,$bytes.Length);$stream.Flush($true)}finally{$stream.Dispose()}
        if([IO.File]::Exists($full)){[IO.File]::Replace($temp,$full,[System.Management.Automation.Language.NullString]::Value)}else{[IO.File]::Move($temp,$full)}
    }finally{if([IO.File]::Exists($temp)){[IO.File]::Delete($temp)}}
}
function Copy-UpdateAtomic([string]$Source,[string]$Destination) {
    $sourcePath=Assert-UpdatePath $Source
    $target=Assert-UpdatePath $Destination
    New-UpdateDirectory ([IO.Path]::GetDirectoryName($target))
    $temp=$target+'.tmp-'+[Guid]::NewGuid().ToString('N')
    try{
        $copyInput=New-Object IO.FileStream($sourcePath,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
        try{
            $copyOutput=New-Object IO.FileStream($temp,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
            try{$copyInput.CopyTo($copyOutput);$copyOutput.Flush($true)}finally{$copyOutput.Dispose()}
        }finally{$copyInput.Dispose()}
        if([IO.File]::Exists($target)){[IO.File]::Replace($temp,$target,[System.Management.Automation.Language.NullString]::Value)}else{[IO.File]::Move($temp,$target)}
    }finally{if([IO.File]::Exists($temp)){[IO.File]::Delete($temp)}}
}
function Copy-UpdateTree([string]$Source,[string]$Destination) {
    Assert-UpdateTree $Source
    Assert-UpdateTree $Destination
    New-UpdateDirectory $Destination
    foreach($item in Get-ChildItem -LiteralPath $Source -Force){
        $target=[IO.Path]::Combine($Destination,$item.Name)
        if($item.PSIsContainer){Copy-UpdateTree $item.FullName $target}else{Copy-UpdateAtomic $item.FullName $target}
    }
}
function Expand-UpdateArchive([string]$Archive,[string]$Destination) {
    $base=Assert-UpdatePath $Destination
    Assert-UpdatePath $Archive | Out-Null
    if((New-Object IO.FileInfo($Archive)).Length -gt 2GB){throw 'Archive exceeds 2 GB'}
    if([IO.Directory]::Exists($base) -and @(Get-ChildItem -LiteralPath $base -Force).Count){throw 'Archive destination must be empty'}
    $zip=[IO.Compression.ZipFile]::OpenRead($Archive)
    $seen=New-Object 'Collections.Generic.Dictionary[string,bool]' ([StringComparer]::OrdinalIgnoreCase)
    $entries=New-Object 'Collections.Generic.List[object]'
    [long]$size=0
    try{
        if($zip.Entries.Count -gt 20000){throw 'Archive has too many entries'}
        foreach($entry in $zip.Entries){
            $directory=$entry.FullName.EndsWith('/')
            $name=$entry.FullName
            if($directory){$name=$name.Substring(0,$name.Length-1)}
            $name=Get-UpdateRelativePath $name
            if($directory -and $entry.Length -ne 0){throw 'Archive directory entry contains file data'}
            if(($entry.ExternalAttributes -shr 16 -band 0xF000) -eq 0xA000 -or ($entry.ExternalAttributes -band 0x400)){throw 'Archive contains a symbolic link or reparse point'}
            if($seen.ContainsKey($name)){throw "Duplicate archive path: $name"}
            $seen.Add($name,$directory)
            $size+=$entry.Length
            if($size -gt 2GB){throw 'Expanded archive exceeds 2 GB'}
            $entries.Add(@{entry=$entry;name=$name;directory=$directory})
        }
        foreach($item in $entries){
            $parent=$item.name
            while($parent.Contains('/')){
                $parent=$parent.Substring(0,$parent.LastIndexOf('/'))
                if($seen.ContainsKey($parent) -and !$seen[$parent]){throw 'Archive file conflicts with a directory'}
            }
        }
        New-UpdateDirectory $base
        foreach($item in $entries){
            $path=Get-UpdateChildPath $base $item.name
            if($item.directory){New-UpdateDirectory $path;continue}
            New-UpdateDirectory ([IO.Path]::GetDirectoryName($path))
            $entryInput=$item.entry.Open()
            try{
                $entryOutput=New-Object IO.FileStream($path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
                try{
                    $buffer=New-Object byte[] 81920
                    [long]$expanded=0
                    while(($read=$entryInput.Read($buffer,0,$buffer.Length)) -gt 0){
                        if($read -gt $item.entry.Length-$expanded){throw 'Expanded archive entry exceeds its declared length'}
                        $expanded+=$read;$entryOutput.Write($buffer,0,$read)
                    }
                    if($expanded -ne $item.entry.Length){throw 'Expanded archive entry is truncated'}
                }finally{$entryOutput.Dispose()}
            }finally{$entryInput.Dispose()}
        }
    }finally{$zip.Dispose()}
}
function Get-UpdateDeployment([string]$Root) {
    $path=Get-UpdateChildPath $Root 'deployment.json'
    $cfg=Read-UpdateText $path 32768 | ConvertFrom-Json
    $ip=$null
    if(![Net.IPAddress]::TryParse([string]$cfg.address,[ref]$ip) -or $ip.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork){throw 'Invalid deployment IPv4 address'}
    if($ip.GetAddressBytes()[0] -eq 0 -or $ip.GetAddressBytes()[0] -ge 224){throw 'Invalid deployment IPv4 address'}
    if($cfg.gamePort -lt 1024 -or $cfg.gamePort -gt 65535 -or $cfg.packPort -lt 1024 -or $cfg.packPort -gt 65535 -or $cfg.gamePort -eq $cfg.packPort){throw 'Invalid deployment ports'}
    if($cfg.packOrigin -cne "https://$($cfg.address):$($cfg.packPort)" -or $cfg.certificateSha256 -notmatch '^[a-fA-F0-9]{64}$'){throw 'Invalid deployment HTTPS origin or certificate pin'}
    return $cfg
}
function Protect-UpdateSecrets([string]$Path) {
    Assert-UpdateTree $Path
    New-UpdateDirectory $Path
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent().User
    $allowedSids=@($identity,(New-Object Security.Principal.SecurityIdentifier('S-1-5-18')),(New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')))
    $directories=@((Get-Item -LiteralPath $Path -Force))+@(Get-ChildItem -LiteralPath $Path -Force -Directory -Recurse)
    foreach($directory in $directories){
        $acl=Get-Acl -LiteralPath $directory.FullName
        $acl.SetAccessRuleProtection($true,$false)
        foreach($rule in $acl.GetAccessRules($true,$false,[Security.Principal.SecurityIdentifier])){$acl.RemoveAccessRuleSpecific($rule)}
        foreach($sid in $allowedSids){
            $rule=New-Object Security.AccessControl.FileSystemAccessRule($sid,'FullControl','ContainerInherit,ObjectInherit','None','Allow')
            $acl.AddAccessRule($rule)
        }
        if($PSVersionTable.PSEdition -eq 'Desktop'){[IO.Directory]::SetAccessControl($directory.FullName,$acl)}else{[IO.FileSystemAclExtensions]::SetAccessControl((New-Object IO.DirectoryInfo($directory.FullName)),$acl)}
    }
    foreach($file in Get-ChildItem -LiteralPath $Path -Force -File -Recurse){
        $fileAcl=Get-Acl -LiteralPath $file.FullName
        $fileAcl.SetAccessRuleProtection($true,$false)
        foreach($rule in $fileAcl.GetAccessRules($true,$false,[Security.Principal.SecurityIdentifier])){$fileAcl.RemoveAccessRuleSpecific($rule)}
        foreach($sid in $allowedSids){$fileAcl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($sid,'FullControl','Allow')))}
        if($PSVersionTable.PSEdition -eq 'Desktop'){[IO.File]::SetAccessControl($file.FullName,$fileAcl)}else{[IO.FileSystemAclExtensions]::SetAccessControl((New-Object IO.FileInfo($file.FullName)),$fileAcl)}
    }
}
function Invoke-UpdateTool([string]$Root,[string[]]$Arguments) {
    $tool=Get-UpdateChildPath $Root 'UpdateTool/ApfUpdateTool.exe'
    $start=New-Object Diagnostics.ProcessStartInfo
    $start.FileName=$tool;$start.UseShellExecute=$false;$start.CreateNoWindow=$true
    $start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    $start.Arguments=(@($Arguments | ForEach-Object {ConvertTo-UpdateNativeArgument $_}) -join ' ')
    $process=[Diagnostics.Process]::Start($start)
    try{
        $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $outputText=$stdout.GetAwaiter().GetResult();$errorText=$stderr.GetAwaiter().GetResult()
        if($outputText){Write-Host $outputText.TrimEnd()}
        if($errorText){Write-Warning $errorText.TrimEnd()}
        if($process.ExitCode -ne 0){throw "Update tool failed (exit $($process.ExitCode))"}
    }finally{$process.Dispose()}
}
function ConvertTo-UpdateNativeArgument([string]$Value) {
    $quoted=New-Object Text.StringBuilder
    $null=$quoted.Append('"');$slashes=0
    foreach($character in $Value.ToCharArray()){
        if($character -eq '\'){$slashes++;continue}
        if($character -eq '"'){$null=$quoted.Append(('\'*(2*$slashes+1)));$null=$quoted.Append('"')}
        else{$null=$quoted.Append(('\'*$slashes));$null=$quoted.Append($character)}
        $slashes=0
    }
    $null=$quoted.Append(('\'*(2*$slashes)));$null=$quoted.Append('"')
    return $quoted.ToString()
}
function Get-BundledUpdateRelease([string]$Root) {
    $rootPath=Assert-UpdatePath $Root
    $releases=@(Get-ChildItem -LiteralPath $rootPath -File -Filter 'KESAS-Release-kesas-rc*.zip' | Where-Object {$_.Name -cmatch '^KESAS-Release-kesas-rc[1-9][0-9]*\.zip$'})
    if(!$releases.Count){throw 'No bundled KESAS release archive found. Supply -ReleaseZip explicitly'}
    if($releases.Count -ne 1){throw 'More than one bundled KESAS release archive found. Supply -ReleaseZip explicitly'}
    return (Assert-UpdatePath $releases[0].FullName)
}
function Get-UpdateServiceProcesses([string]$Root,$Config) {
    $gamePath=[IO.Path]::GetFullPath("$Root/Server/Server.exe")
    $packPath=[IO.Path]::GetFullPath("$Root/PackHost/ApfPackHost.exe")
    $endpoints=@(Get-NetUDPEndpoint -LocalPort $Config.gamePort -ErrorAction SilentlyContinue)+@(Get-NetTCPConnection -LocalPort $Config.packPort -State Listen -ErrorAction SilentlyContinue)
    $ids=New-Object 'Collections.Generic.HashSet[int]'
    foreach($endpoint in $endpoints){
        $process=Get-Process -Id $endpoint.OwningProcess -ErrorAction Stop
        if($process.Path -ine $gamePath -and $process.Path -ine $packPath){throw 'Deployment ports are owned by an unrelated process; no shutdown requested'}
        if($ids.Add($process.Id)){$process}
    }
    foreach($process in Get-Process -Name Server,ApfPackHost -ErrorAction SilentlyContinue){
        if(($process.Path -ieq $gamePath -or $process.Path -ieq $packPath) -and $ids.Add($process.Id)){$process}
    }
}
function Wait-UpdateServerStopped([string]$Root,$Config) {
    $processes=@(Get-UpdateServiceProcesses $Root $Config)
    & "$Root/Stop-Server.ps1"
    $deadline=(Get-Date).AddSeconds(40)
    do{
        $busy=$false
        foreach($process in $processes){if(!$process.HasExited){$busy=$true}}
        $busy=$busy -or [bool](Get-NetUDPEndpoint -LocalPort $Config.gamePort -ErrorAction SilentlyContinue) -or [bool](Get-NetTCPConnection -LocalPort $Config.packPort -State Listen -ErrorAction SilentlyContinue)
        $lockPath=Get-UpdateChildPath $Root '.server.lock'
        if([IO.File]::Exists($lockPath)){
            try{$probe=New-Object IO.FileStream($lockPath,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None);$probe.Dispose()}
            catch [IO.IOException]{$busy=$true}
        }
        if(!$busy){return}
        if((Get-Date)-gt $deadline){throw 'Server did not stop cleanly; no update will be applied'}
        Start-Sleep -Milliseconds 500
    }while($true)
}
function Start-UpdatedServer([string]$Root,$Config) {
    $stamp=Get-Date -Format 'yyyyMMdd-HHmmss-fff'
    New-UpdateDirectory "$Root/LauncherLogs"
    $launcher=Start-Process powershell.exe -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',"`"$Root/Start-Server.ps1`"",'-LauncherLog',"`"$Root/LauncherLogs/$stamp.log`"",'-PublicationActivation') -WindowStyle Hidden -PassThru
    $deadline=(Get-Date).AddSeconds(30)
    while(!(Get-NetUDPEndpoint -LocalPort $Config.gamePort -ErrorAction SilentlyContinue) -or !(Get-NetTCPConnection -LocalPort $Config.packPort -State Listen -ErrorAction SilentlyContinue)){
        if($launcher.HasExited -or (Get-Date)-gt $deadline){throw 'Updated server failed to start; inspect LauncherLogs'}
        Start-Sleep -Milliseconds 500
    }
    $null=@(Get-UpdateServiceProcesses $Root $Config)
    & "$Root/CertificateTool/ApfCertificateTool.exe" --probe-server "https://127.0.0.1:$($Config.packPort)/health" $Config.certificateSha256
    if($LASTEXITCODE -ne 0){throw 'Updated HTTPS service failed its pinned health check'}
}
function Assert-UpdateKeyPair([string]$Root) {
    $privatePath=Get-UpdateChildPath $Root 'secrets/update-signing-private.xml'
    $publicPath=Get-UpdateChildPath $Root 'secrets/update-signing-public.xml'
    $privateRsa=New-Object Security.Cryptography.RSACryptoServiceProvider
    $publicRsa=New-Object Security.Cryptography.RSACryptoServiceProvider
    try{
        $privateRsa.PersistKeyInCsp=$false;$publicRsa.PersistKeyInCsp=$false
        $privateRsa.FromXmlString((Read-UpdateText $privatePath 32768))
        $publicRsa.FromXmlString((Read-UpdateText $publicPath 32768))
        if($privateRsa.PublicOnly -or !$publicRsa.PublicOnly -or $privateRsa.KeySize -lt 2048 -or $privateRsa.ToXmlString($false) -cne $publicRsa.ToXmlString($false)){throw 'Signing keys are not a matching private/public keypair. Preserve them and investigate'}
    }finally{$privateRsa.Dispose();$publicRsa.Dispose()}
}
function Assert-UpdateActiveFeed([string]$Root,$Config) {
    $feedPath=Get-UpdateChildPath $Root 'Updates/APFClientUpdate.json'
    if(![IO.File]::Exists($feedPath)){return}
    $feed=Read-UpdateText $feedPath | ConvertFrom-Json
    [long]$number=0
    if($feed.version -cnotmatch '^kesas-rc[1-9][0-9]*$' -or ![long]::TryParse($feed.version.Substring(8),[ref]$number)){throw 'Invalid active feed version; investigate without replacing the feed'}
    $archiveName="APFClient-$($feed.version).zip"
    if($feed.packageUrl -cne "$($Config.packOrigin)/updates/$($feed.version)/$archiveName"){throw 'Active update feed points outside the deployment update channel'}
    $archive=Get-UpdateChildPath $Root "Updates/$($feed.version)/$archiveName"
    Invoke-UpdateTool $Root @('--verify','--manifest',$feedPath,'--archive',$archive,'--public-key-xml',"$Root/secrets/update-signing-public.xml")
}
