#requires -Version 7.0
[CmdletBinding(PositionalBinding=$false)]
param(
    [Parameter(Mandatory)][string]$Settings,
    [Parameter(Mandatory)][string]$RequestId,
    [Parameter(Mandatory)][int]$Attempt,
    [Parameter(Mandatory)][string]$Publisher,
    [Parameter(Mandatory)][string]$Archive,
    [AllowEmptyString()][string]$Notes='',
    [switch]$AllowActivePlayers
)

$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
$PSNativeCommandUseErrorActionPreference=$false
Set-StrictMode -Version Latest
# The agent launches this fixed wrapper hidden, with protected file handles.
# No diagnostic depends on the agent, its lifetime, or a parent-owned pipe.
[Console]::SetOut([IO.TextWriter]::Null)
[Console]::SetError([IO.TextWriter]::Null)
$nativeDiagnosticDirectory=$null
$nativeInvocationStarted=$false
$nativeStartedAt=[DateTime]::UtcNow.ToString('o')
$nativeUtf8=[Text.UTF8Encoding]::new($false)
$nativeOutput=[IO.MemoryStream]::new(65536)
$nativeOutputTruncated=$false
$nativeTruncation=$nativeUtf8.GetBytes("`n[output truncated at 65536 bytes]`n")
$nativeExitCode=1
$nativePublisherFailure=$null
$nativeStage='publisher-execution'

function Assert-NativeNoReparse([string]$Path) {
    for($cursor=$Path;$cursor;$cursor=[IO.Path]::GetDirectoryName($cursor)){
        if([IO.File]::GetAttributes($cursor) -band [IO.FileAttributes]::ReparsePoint){throw 'Unsafe protected path.'}
    }
}

function Assert-NativeProtection([string]$Path,[switch]$Private,[switch]$ControlRoot,[switch]$Inherited) {
    $acl=Get-Acl -LiteralPath $Path
    $trusted=@('S-1-5-32-544','S-1-5-18')
    if(-not $Inherited -and $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin $trusted){throw 'Protected path has an untrusted owner.'}
    if($Inherited -and $acl.AreAccessRulesProtected){throw 'Diagnostics must inherit private permissions.'}
    $dangerous=[Security.AccessControl.FileSystemRights]'Write,Delete,DeleteSubdirectoriesAndFiles,ChangePermissions,TakeOwnership'
    if($ControlRoot){$dangerous=[Security.AccessControl.FileSystemRights]'Delete,DeleteSubdirectoriesAndFiles,ChangePermissions,TakeOwnership'}
    foreach($rule in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])){
        if($rule.AccessControlType -ne 'Allow' -or ($rule.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly)){continue}
        if($rule.IdentityReference.Value -notin $trusted -and ($Private -or ($rule.FileSystemRights -band $dangerous))){throw 'Protected path permissions are unsafe.'}
    }
}

function Get-NativeExactPath([string]$Path,[string]$Expected) {
    if(-not [IO.Path]::IsPathFullyQualified($Path) -or $Path -match '^[\\/]{2}') {throw 'A fixed local absolute path is required.'}
    $full=Assert-UpdatePath $Path
    if(-not [string]::Equals($full,$Expected,[StringComparison]::OrdinalIgnoreCase)){throw 'Unexpected protected path.'}
    return $full
}

function New-NativePrivateDirectory([string]$Path) {
    $full=Assert-UpdatePath $Path
    $null=[IO.Directory]::CreateDirectory($full)
    Assert-NativeProtection $full -Private -Inherited
    return $full
}

function Write-NativeAtomic([string]$Name,[byte[]]$Bytes,[int]$MaximumBytes) {
    if($Name -cnotin @('stdout.log','error.json','receipt.json') -or $Bytes.Length -gt $MaximumBytes){throw 'Invalid diagnostic write.'}
    $destination=Assert-UpdatePath (Join-Path $nativeDiagnosticDirectory $Name)
    $temporary=Assert-UpdatePath (Join-Path $nativeDiagnosticDirectory ('.'+$Name+'.'+[Guid]::NewGuid().ToString('N')+'.tmp'))
    try{
        $stream=[IO.FileStream]::new($temporary,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
        try{$stream.Write($Bytes,0,$Bytes.Length);$stream.Flush($true)}finally{$stream.Dispose()}
        if([IO.File]::Exists($destination)){[IO.File]::Replace($temporary,$destination,[Management.Automation.Language.NullString]::Value)}
        else{[IO.File]::Move($temporary,$destination)}
    }finally{if([IO.File]::Exists($temporary)){[IO.File]::Delete($temporary)}}
}

function Write-NativeReceipt([string]$Status,$ExitCode,$Reason) {
    $completedAt=if($Status -ceq 'running'){$null}else{[DateTime]::UtcNow.ToString('o')}
    $receipt=[ordered]@{
        schemaVersion=1;requestId=$RequestId;attempt=$Attempt;stage=$nativeStage
        status=$Status;exitCode=$ExitCode;reason=$Reason;startedAt=$nativeStartedAt;completedAt=$completedAt
    }
    Write-NativeAtomic 'receipt.json' ($nativeUtf8.GetBytes(($receipt|ConvertTo-Json -Compress))) 4096
}

function Limit-NativeText([string]$Text,[int]$Length) {
    if($Text.Length -gt $Length){return $Text.Substring(0,$Length)}
    return $Text
}

function Write-NativeError([Management.Automation.ErrorRecord]$Failure) {
    $exception=$Failure.Exception.GetBaseException()
    $invocation=$Failure.InvocationInfo
    $details=[ordered]@{
        ExceptionType=(Limit-NativeText $exception.GetType().FullName 256)
        Message=(Limit-NativeText ([string]$exception.Message) 1024)
        ScriptStackTrace=(Limit-NativeText ([string]$Failure.ScriptStackTrace) 2048)
        InvocationInfo=if($null -eq $invocation){$null}else{[ordered]@{
            ScriptName=(Limit-NativeText ([string]$invocation.ScriptName) 512)
            ScriptLineNumber=[int]$invocation.ScriptLineNumber
            Line=(Limit-NativeText ([string]$invocation.Line) 1024)
        }}
    }
    # Even worst-case JSON escaping of these fields remains below 32768 bytes.
    Write-NativeAtomic 'error.json' ($nativeUtf8.GetBytes(($details|ConvertTo-Json -Depth 4 -Compress))) 32768
}

function Write-NativeOutput($Record) {
    if($script:nativeOutputTruncated){return}
    $text=[string]$Record
    $textWasClipped=$text.Length -gt 65536
    if($textWasClipped){$text=$text.Substring(0,65536)}
    $bytes=$nativeUtf8.GetBytes($text+[Environment]::NewLine)
    $remaining=65536-$nativeTruncation.Length-[int]$nativeOutput.Length
    $length=[Math]::Min($remaining,$bytes.Length)
    # Never leave a partial UTF-8 code point at the truncation boundary.
    if($length -lt $bytes.Length){while($length -gt 0 -and ($bytes[$length] -band 0xc0) -eq 0x80){$length--}}
    $nativeOutput.Write($bytes,0,$length)
    if($textWasClipped -or $length -lt $bytes.Length){
        $nativeOutput.Write($nativeTruncation,0,$nativeTruncation.Length)
        $script:nativeOutputTruncated=$true
    }
    try{Write-NativeAtomic 'stdout.log' ($nativeOutput.ToArray()) 65536}catch{
        # Diagnostic disk/rename failures must not interrupt the running publisher.
    }
    # Continue consuming all streams after the cap; never terminate the publisher.
}

try{
    if($PSVersionTable.PSVersion.Major -ne 7 -or -not $IsWindows){throw 'Windows PowerShell 7 is required.'}
    if($RequestId.Length -ne 64 -or $RequestId -cnotmatch '\A[a-f0-9]{64}\z' -or $Attempt -lt 1 -or $Attempt -gt 1000){throw 'Invalid diagnostic identity.'}
    $nativeControlRoot=[IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'Aetherfront-Control'))
    $nativeBootstrap=Join-Path $nativeControlRoot 'bootstrap'
    $nativeFrozen=Join-Path $nativeBootstrap 'native'
    $nativeCommon=Join-Path $nativeFrozen 'Update.Common.ps1'
    # Bootstrap trust must be checked before dot-sourcing its path validator.
    Assert-NativeNoReparse $nativeCommon
    Assert-NativeProtection $nativeControlRoot -ControlRoot
    foreach($path in @($nativeBootstrap,$nativeFrozen,$nativeCommon)){Assert-NativeProtection $path}
    . $nativeCommon *> $null
    $null=Assert-UpdatePath $nativeControlRoot
    $nativeManaged=Join-Path $nativeControlRoot 'managed/releases'
    $nativeWrapper=[IO.Path]::GetFullPath($PSCommandPath)
    $nativeManagedPrefix=[IO.Path]::GetFullPath($nativeManaged).TrimEnd([IO.Path]::DirectorySeparatorChar)+[IO.Path]::DirectorySeparatorChar
    if($nativeWrapper.StartsWith($nativeManagedPrefix,[StringComparison]::OrdinalIgnoreCase)){
        $nativeRelative=$nativeWrapper.Substring($nativeManagedPrefix.Length).Replace([IO.Path]::DirectorySeparatorChar,[char]'/')
        if($nativeRelative -cnotmatch '^control-[1-9][0-9]{0,8}-[a-f0-9]{64}/ControlPlane/Invoke-NativePublisher.ps1$'){throw 'Unexpected versioned wrapper path.'}
        Assert-NativeProtection $nativeManaged
        Assert-NativeNoReparse $nativeWrapper
    }else{$null=Get-NativeExactPath $PSCommandPath (Join-Path $nativeBootstrap 'Invoke-NativePublisher.ps1')}
    Assert-NativeProtection $PSCommandPath
    $nativeCache=Assert-UpdatePath (Join-Path $nativeControlRoot 'native-cache')
    if(-not [IO.Directory]::Exists($nativeCache)){throw 'Protected native cache is missing.'}
    Assert-NativeProtection $nativeCache -Private
    $nativeDiagnostics=New-NativePrivateDirectory (Join-Path $nativeCache 'diagnostics')
    $nativeRequestDirectory=New-NativePrivateDirectory (Join-Path $nativeDiagnostics $RequestId)
    $nativeAttemptDirectory=Assert-UpdatePath (Join-Path $nativeRequestDirectory ([string]$Attempt))
    if([IO.Directory]::Exists($nativeAttemptDirectory) -or [IO.File]::Exists($nativeAttemptDirectory)){throw 'Diagnostic attempt already exists; preserve its history.'}
    # Rename an empty private directory to claim an attempt atomically. Move
    # refuses an existing destination, including a concurrent wrapper's claim.
    $nativePending=New-NativePrivateDirectory (Join-Path $nativeRequestDirectory ('.attempt-'+[Guid]::NewGuid().ToString('N')))
    try{
        [IO.Directory]::Move($nativePending,$nativeAttemptDirectory)
        $nativeDiagnosticDirectory=$nativeAttemptDirectory
    }finally{if([IO.Directory]::Exists($nativePending)){[IO.Directory]::Delete($nativePending,$false)}}
    Write-NativeAtomic 'stdout.log' ([byte[]]::new(0)) 65536
    Write-NativeReceipt 'running' $null $null

    $nativeSettings=Get-NativeExactPath $Settings (Join-Path $nativeControlRoot 'host.json')
    Assert-NativeProtection $nativeSettings
    $nativeSettingsText=Read-UpdateText $nativeSettings 32768
    Assert-ReleaseJsonUnique $nativeSettingsText
    $nativeConfiguration=$nativeSettingsText|ConvertFrom-Json
    $null=Get-NativeExactPath ([string]$nativeConfiguration.root) $nativeControlRoot
    $nativeDeployment=[string]$nativeConfiguration.nativeRoot
    if(-not [IO.Path]::IsPathFullyQualified($nativeDeployment) -or $nativeDeployment -match '^[\\/]{2}'){throw 'Invalid native deployment root.'}
    $nativeDeployment=Assert-UpdatePath $nativeDeployment
    if(-not [IO.Directory]::Exists($nativeDeployment)){throw 'Native deployment root is missing.'}
    if($Notes.Length -gt 500 -or $Notes -match '[\p{Cc}\p{Cf}]'){throw 'Invalid release notes.'}
    $nativeSelectionFile=Assert-UpdatePath (Join-Path $nativeCache 'current-native.json')
    Assert-NativeProtection $nativeSelectionFile -Private
    $nativeSelectionText=Read-UpdateText $nativeSelectionFile 32768
    Assert-ReleaseJsonUnique $nativeSelectionText
    $nativeSelection=$nativeSelectionText|ConvertFrom-Json
    $nativeSelected=[string]$nativeSelection.directory
    if(-not [IO.Path]::IsPathFullyQualified($nativeSelected) -or $nativeSelected -match '^[\\/]{2}'){throw 'Invalid selected native directory.'}
    $nativeSelected=Assert-UpdatePath $nativeSelected
    $nativeControlCache=Join-Path $nativeCache 'control'
    if($nativeSelected.StartsWith($nativeManagedPrefix,[StringComparison]::OrdinalIgnoreCase)){
        $relative=$nativeSelected.Substring($nativeManagedPrefix.Length).Replace([IO.Path]::DirectorySeparatorChar,[char]'/')
        if($relative -cnotmatch '^control-[1-9][0-9]{0,8}-[a-f0-9]{64}/KESAS-VPS-Server$'){throw 'Unexpected versioned publisher path.'}
        for($cursor=$nativeSelected;$cursor -ine $nativeControlRoot;$cursor=[IO.Path]::GetDirectoryName($cursor)){Assert-NativeProtection $cursor}
        $expectedWrapper=Join-Path ([IO.Path]::GetDirectoryName($nativeSelected)) 'ControlPlane/Invoke-NativePublisher.ps1'
        $null=Get-NativeExactPath $PSCommandPath $expectedWrapper
    }elseif(-not [string]::Equals($nativeSelected,$nativeFrozen,[StringComparison]::OrdinalIgnoreCase)){
        $prefix=$nativeControlCache+[IO.Path]::DirectorySeparatorChar
        if(-not $nativeSelected.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or
            [IO.Path]::GetFileName($nativeSelected) -ine 'KESAS-VPS-Server' -or
            [string]::Equals([IO.Path]::GetDirectoryName($nativeSelected),$nativeControlCache,[StringComparison]::OrdinalIgnoreCase)){
            throw 'Publisher is outside the protected native selection.'
        }
        for($cursor=$nativeSelected;$cursor -ine $nativeCache;$cursor=[IO.Path]::GetDirectoryName($cursor)){
            Assert-NativeProtection $cursor -Private
        }
    }
    $nativePublisher=Get-NativeExactPath $Publisher (Join-Path $nativeSelected 'Publish-Release.ps1')
    Assert-NativeProtection $nativePublisher
    if(-not [IO.File]::Exists($nativePublisher)){throw 'Selected publisher is missing.'}
    $nativeArchive=Get-NativeExactPath $Archive (Join-Path $nativeCache ('applications/'+$RequestId+'.zip'))
    Assert-NativeProtection ([IO.Path]::GetDirectoryName($nativeArchive)) -Private
    Assert-NativeProtection $nativeArchive -Private
    if(-not [IO.File]::Exists($nativeArchive)){throw 'Protected application archive is missing.'}
    $env:APF_SERVER_TASK='KESAS Server'
    $env:APF_CONTROL_ROOT=$nativeControlRoot
    $nativeArguments=@{DeploymentRoot=$nativeDeployment;ReleaseZip=$nativeArchive;Notes=$Notes}
    # Only the internal agent supplies this switch after its existing guards.
    # The wrapper neither derives authorization from request notes nor adds it.
    if($AllowActivePlayers){$nativeArguments.AllowActivePlayers=$true}
    $global:LASTEXITCODE=$null
    $nativeInvocationStarted=$true
    # The publisher owns its lease. Acquiring it here would deadlock invocation.
    & $nativePublisher @nativeArguments *>&1 | ForEach-Object {
        if($_ -is [Management.Automation.InformationRecord] -and $_.Tags -contains 'Aetherfront-PublisherStage' -and
            [string]$_.MessageData -cin @('guard-admission','preparation-signing','media-publication','native-activation','github-completion')){
            $script:nativeStage=[string]$_.MessageData
            try{Write-NativeReceipt 'running' $null $null}catch{}
        }
        if($_ -is [Management.Automation.ErrorRecord]){$script:nativePublisherFailure=$_}
        Write-NativeOutput $_
    }
    if($null -ne $nativePublisherFailure){throw $nativePublisherFailure}
    if($null -ne $global:LASTEXITCODE -and $global:LASTEXITCODE -ne 0){
        $nativeExitCode=[int]$global:LASTEXITCODE
        Write-NativeReceipt 'failed' $nativeExitCode 'publisher-exit'
    }else{
        $nativeExitCode=0
        Write-NativeReceipt 'succeeded' 0 $null
    }
}catch{
    $nativeFailure=$_
    $nativeExitCode=1
    if($null -eq $nativeDiagnosticDirectory -and $RequestId -cmatch '\A[a-f0-9]{64}\z' -and $Attempt -ge 1 -and $Attempt -le 1000){
        try{
            $startupRoot=Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) ('Aetherfront-Control/native-cache/diagnostics/'+$RequestId+'/agent-'+$Attempt)
            Assert-NativeNoReparse $startupRoot
            Assert-NativeProtection $startupRoot -Private
            $startupFile=Join-Path $startupRoot 'wrapper-startup-error.json'
            $startupDetails=[ordered]@{message=(Limit-NativeText ([string]$nativeFailure.Exception.Message) 2048);stack=(Limit-NativeText ([string]$nativeFailure.ScriptStackTrace) 4096);stage='wrapper-validation';exitCode=1}
            $startupBytes=$nativeUtf8.GetBytes(($startupDetails|ConvertTo-Json -Compress))
            $startupStream=[IO.FileStream]::new($startupFile,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
            try{$startupStream.Write($startupBytes,0,$startupBytes.Length);$startupStream.Flush($true)}finally{$startupStream.Dispose()}
        }catch{}
    }
    if($null -ne $nativeDiagnosticDirectory){
        try{Write-NativeError $nativeFailure}catch{}
        $nativeReason=if($nativeInvocationStarted){'publisher-error'}else{'wrapper-validation-failed'}
        try{Write-NativeReceipt 'failed' 1 $nativeReason}catch{}
    }
}finally{
    $nativeOutput.Dispose()
}
exit $nativeExitCode
