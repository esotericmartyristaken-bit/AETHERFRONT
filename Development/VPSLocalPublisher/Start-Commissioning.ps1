#requires -Version 7.5
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Bundle,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{64}$')][string]$BundleSha256,
    [Parameter(Mandatory)][string]$NativeArchive,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{64}$')][string]$NativeSha256,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{40}$')][string]$ReleaseCommit,
    [Parameter(Mandatory)][ValidatePattern('^S-1-5-21-[0-9-]+$')][string]$OperatorSid,
    [switch]$PrepareOnly)
$ErrorActionPreference='Stop'
$powerShell='C:\Program Files\PowerShell\7\pwsh.exe'
$bundlePath=(Resolve-Path -LiteralPath $Bundle).Path
$archivePath=(Resolve-Path -LiteralPath $NativeArchive).Path
if((Get-FileHash -LiteralPath $bundlePath).Hash.ToLowerInvariant() -cne $BundleSha256){throw 'Commissioning bundle differs from reviewed hash'}
if((Get-FileHash -LiteralPath $archivePath).Hash.ToLowerInvariant() -cne $NativeSha256){throw 'Native release differs from reviewed hash'}
$values=@{bundle=$bundlePath;bundleHash=$BundleSha256;archive=$archivePath;archiveHash=$NativeSha256;commit=$ReleaseCommit;sid=$OperatorSid}
$data=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(($values|ConvertTo-Json -Compress)))
$body=@'
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$v=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('VALUES'))|ConvertFrom-Json
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
if($identity.User.Value -cne $v.sid -or -not [Security.Principal.WindowsPrincipal]::new($identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'The approved VPS operator elevation is required'}
$parent='C:\ProgramData\Aetherfront-VPSLocalPublisher'
if(-not [IO.Directory]::Exists($parent)){throw 'Reviewed protected publisher root is missing'}
$item=Get-Item -LiteralPath $parent -Force
if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Setup parent is a reparse point'}
$acl=Get-Acl -LiteralPath $parent
if($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin @('S-1-5-18','S-1-5-32-544')){throw 'Setup parent owner differs'}
$directory=Join-Path $parent ('Commission-v3-'+[Guid]::NewGuid().ToString('N'))
$security=[Security.AccessControl.DirectorySecurity]::new();$security.SetAccessRuleProtection($true,$false);$security.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
foreach($sid in @('S-1-5-18','S-1-5-32-544')){$security.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),'FullControl','ContainerInherit,ObjectInherit','None','Allow'))}
$null=[IO.FileSystemAclExtensions]::Create([IO.DirectoryInfo]::new($directory),$security)
$log=Join-Path $directory 'commissioning.log'
Start-Transcript -LiteralPath $log -Force | Out-Null
try {
    $stream=[IO.FileStream]::new($v.bundle,'Open','Read','Read')
    try {
        if($stream.Length -le 0 -or $stream.Length -gt 4MB){throw 'Setup bundle exceeds bound'}
        if([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() -cne $v.bundleHash){throw 'Setup bundle changed'}
        $stream.Position=0;$zip=[IO.Compression.ZipArchive]::new($stream,[IO.Compression.ZipArchiveMode]::Read,$true)
        try {
            $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
            foreach($entry in $zip.Entries){
                if($entry.FullName -cnotmatch '^[A-Za-z][A-Za-z0-9.-]+\.(?:ps1|mjs|cs|json)$' -or -not $seen.Add($entry.FullName) -or $entry.Length -gt 1MB){throw 'Unexpected setup entry'}
                $input=$entry.Open();$output=[IO.FileStream]::new((Join-Path $directory $entry.FullName),'CreateNew','Write','None')
                try{$input.CopyTo($output)}finally{$output.Dispose();$input.Dispose()}
            }
        } finally {$zip.Dispose()}
    } finally {$stream.Dispose()}
    $manifest=Get-Content -LiteralPath (Join-Path $directory 'setup-manifest.json') -Raw | ConvertFrom-Json
    if($manifest.schemaVersion -ne 3 -or $manifest.files.Count -ne ($seen.Count-1)){throw 'Setup manifest differs'}
    foreach($file in $manifest.files){
        if(-not $seen.Contains($file.name) -or (Get-FileHash -LiteralPath (Join-Path $directory $file.name)).Hash.ToLowerInvariant() -cne $file.sha256){throw 'Setup source hash differs'}
    }
    & (Join-Path $directory 'Install-VPSLocalPublisher.ps1') -NativeArchive $v.archive -ExpectedNativeHash $v.archiveHash -ReleaseCommit $v.commit -ExpectedOperatorSid $v.sid
} catch {
    $errorPath=Join-Path $parent 'Status-v3/commissioning-error.json'
    if([IO.Directory]::Exists([IO.Path]::GetDirectoryName($errorPath))){[IO.File]::WriteAllText($errorPath,(@{status='failed';code=$_.Exception.GetType().Name;message=$_.Exception.Message;privateLog=$log;observedAtUtc=[DateTime]::UtcNow.ToString('o')}|ConvertTo-Json))}
    throw
} finally {Stop-Transcript | Out-Null}
'@
$body=$body.Replace('VALUES',$data)
$encoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($body))
if($PrepareOnly){[pscustomobject]@{bundleSha256=$BundleSha256;nativeSha256=$NativeSha256;releaseCommit=$ReleaseCommit;commandLength=$encoded.Length;uacLaunched=$false};return}
# Exactly one normal Windows consent request. No runas credential, bypass,
# remote listener, retry loop or privileged general-purpose command endpoint.
$process=Start-Process -FilePath $powerShell -Verb RunAs -WindowStyle Hidden -ArgumentList @('-NoLogo','-NoProfile','-NonInteractive','-EncodedCommand',$encoded) -PassThru
[pscustomobject]@{launcherPid=$process.Id;bundleSha256=$BundleSha256;statusRoot='C:\ProgramData\Aetherfront-VPSLocalPublisher\Status-v3';uacRequests=1}
