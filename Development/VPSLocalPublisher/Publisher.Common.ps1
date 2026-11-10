#requires -Version 7.5
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$script:TrustedSids=@('S-1-5-18','S-1-5-32-544')

function Set-PublisherDirectory([string]$Path,[string[]]$Readers=@(),[string]$InboxWriter='',[switch]$RootOnlyReaders) {
    if([IO.Directory]::Exists($Path) -and ((Get-Item -LiteralPath $Path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Publisher directory is a reparse point'}
    $null=[IO.Directory]::CreateDirectory($Path)
    $acl=[Security.AccessControl.DirectorySecurity]::new();$acl.SetAccessRuleProtection($true,$false)
    $acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
    foreach($sid in $script:TrustedSids){$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),'FullControl','ContainerInherit,ObjectInherit','None','Allow'))}
    foreach($sid in $Readers){$inherit=if($RootOnlyReaders){'None'}else{'ContainerInherit,ObjectInherit'};$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),'ReadAndExecute',$inherit,'None','Allow'))}
    if($InboxWriter){
        $sid=[Security.Principal.SecurityIdentifier]::new($InboxWriter)
        $rights=[Security.AccessControl.FileSystemRights]'ReadAndExecute,WriteData,AppendData,Synchronize'
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,$rights,'None','None','Allow'))
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,'Modify','ObjectInherit','InheritOnly','Allow'))
    }
    Set-Acl -LiteralPath $Path -AclObject $acl
}

function Set-PublisherFile([string]$Path,[string[]]$Readers=@()) {
    if((Get-Item -LiteralPath $Path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Publisher file is a reparse point'}
    $acl=[Security.AccessControl.FileSecurity]::new();$acl.SetAccessRuleProtection($true,$false);$acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
    foreach($sid in $script:TrustedSids){$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),'FullControl','Allow'))}
    foreach($sid in $Readers){$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),'ReadAndExecute','Allow'))}
    Set-Acl -LiteralPath $Path -AclObject $acl
}

function Assert-PublisherPath([string]$Path,[string]$Root,[switch]$WritableInbox) {
    $full=[IO.Path]::GetFullPath($Path);$base=[IO.Path]::GetFullPath($Root).TrimEnd('\')
    if($full -ine $base -and -not $full.StartsWith($base+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Publisher path escaped protected root'}
    $write=[Security.AccessControl.FileSystemRights]'WriteData,AppendData,WriteAttributes,WriteExtendedAttributes,Delete,DeleteSubdirectoriesAndFiles,ChangePermissions,TakeOwnership'
    $item=Get-Item -LiteralPath $full -Force
    while($item){
        if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Publisher path is a reparse point'}
        $acl=Get-Acl -LiteralPath $item.FullName
        if($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin $script:TrustedSids){throw 'Publisher path owner differs'}
        foreach($rule in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])){
            if($rule.AccessControlType -ne 'Allow' -or $rule.IdentityReference.Value -in $script:TrustedSids){continue}
            if($WritableInbox -and $item.FullName -ieq $full){continue}
            if($rule.FileSystemRights -band $write){throw 'Publisher execution/configuration path is writable by another principal'}
        }
        if($item.FullName -ieq $base){break}
        $item=if($item -is [IO.FileInfo]){$item.Directory}else{$item.Parent}
    }
}

function Write-PublisherJson([string]$Path,$Value) {
    $temporary=$Path+'.'+[Guid]::NewGuid().ToString('N')+'.tmp'
    [IO.File]::WriteAllText($temporary,($Value|ConvertTo-Json -Depth 10),[Text.UTF8Encoding]::new($false))
    [IO.File]::Move($temporary,$Path,$true)
}

function Read-PublisherJson([string]$Path) {
    $stream=[IO.FileStream]::new($Path,'Open','Read',([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    try{if($stream.Length -gt 1MB){throw 'Publisher JSON exceeds bound'};$reader=[IO.StreamReader]::new($stream);try{return ($reader.ReadToEnd()|ConvertFrom-Json -DateKind String)}finally{$reader.Dispose()}}finally{$stream.Dispose()}
}

function Protect-PublisherSecret([string]$Path,[string]$Name,[string]$Value) {
    if([string]::IsNullOrWhiteSpace($Value)){throw 'Publisher credential unavailable'}
    $bytes=[Text.Encoding]::UTF8.GetBytes($Value)
    try{$cipher=[Security.Cryptography.ProtectedData]::Protect($bytes,[Text.Encoding]::ASCII.GetBytes('AETHERFRONT VPS PUBLISHER V3 '+$Name),[Security.Cryptography.DataProtectionScope]::LocalMachine)}finally{[Array]::Clear($bytes)}
    [IO.File]::WriteAllBytes($Path,$cipher)
}

function Read-PublisherSecret([string]$Path,[string]$Name) {
    $plain=[Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($Path),[Text.Encoding]::ASCII.GetBytes('AETHERFRONT VPS PUBLISHER V3 '+$Name),[Security.Cryptography.DataProtectionScope]::LocalMachine)
    try{return [Text.UTF8Encoding]::new($false,$true).GetString($plain)}finally{[Array]::Clear($plain)}
}

function Set-PublisherInteractiveKeyAccess($Config) {
    $sids=@([AetherfrontPublisherSessionAccess]::InteractiveLogons($Config.writerSid))
    Set-PublisherDirectory $Config.approvalRoot $sids
    foreach($file in Get-ChildItem -LiteralPath $Config.approvalRoot -File -Force){
        if($file.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Approval key is a reparse point'}
        $acl=[Security.AccessControl.FileSecurity]::new();$acl.SetAccessRuleProtection($true,$false);$acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
        foreach($sid in $script:TrustedSids){$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),'FullControl','Allow'))}
        foreach($sid in $sids){$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),'Read','Allow'))}
        Set-Acl -LiteralPath $file.FullName -AclObject $acl
    }
    return $sids.Count
}
