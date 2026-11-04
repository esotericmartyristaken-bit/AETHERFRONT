#requires -Version 7.4
function Assert-LocalBotChildIdentity($Settings,[string]$ExpectedSid,$Holder,$Launch,$Process,[string]$OwnerSid) {
    if(-not $Holder -or $Holder.pid -isnot [long] -and $Holder.pid -isnot [int] -or $Holder.pid -le 0){throw 'Invalid bot instance PID'}
    if(-not $Launch -or $Launch.pid -ne $Holder.pid -or -not $Process -or $Process.ProcessId -ne $Holder.pid){throw 'Bot launch/instance/process identities differ'}
    if($OwnerSid -cne $ExpectedSid){throw 'Bot process owner differs from the limited task identity'}
    $runtime=[IO.Path]::GetFullPath([string]$Launch.runtime).TrimEnd('\').Replace('/','\')
    if($runtime -cnotmatch '^C:\\ProgramData\\Aetherfront-Local\\releases\\[a-f0-9]{40}-[a-f0-9]{16}\\BotRuntime$'){throw 'Bot runtime is not an immutable local release'}
    if($Process.ExecutablePath -ine $Settings.node){throw 'Bot image differs from trusted Node executable'}
    $entry=(Join-Path $runtime 'APFDiscordBot/src/index.js').Replace('/','\')
    $command=([string]$Process.CommandLine).Replace('/','\').Trim()
    $node=[string]$Settings.node
    if($command -cne ('"'+$node+'" "'+$entry+'"') -and $command -cne ($node+' "'+$entry+'"')){throw 'Bot command line is not the exact local bot entry point'}
    $created=([DateTime]$Process.CreationDate).ToUniversalTime()
    if([Math]::Abs(($created-([DateTimeOffset]$Launch.startedAt).UtcDateTime).TotalSeconds) -ge 2){throw 'Bot PID generation differs from launch evidence'}
    $locked=([DateTimeOffset]$Holder.startedAt).UtcDateTime
    if($locked -lt $created -or ($locked-$created).TotalSeconds -gt 120){throw 'Bot instance generation differs from launch evidence'}
    return $created
}

function Stop-LocalBotChild($Settings,[string]$ExpectedSid) {
    $lockPath=Join-Path $Settings.botRoot 'state/bot-instance.lock'
    if(-not [IO.File]::Exists($lockPath)){return}
    $holder=Get-Content -LiteralPath $lockPath -Raw|ConvertFrom-Json
    if($holder.pid -isnot [long] -and $holder.pid -isnot [int] -or $holder.pid -le 0){throw 'Invalid bot instance PID'}
    $process=Get-CimInstance Win32_Process -Filter "ProcessId=$($holder.pid)" -ErrorAction Stop
    if(-not $process){return} # Existing singleton code handles the stale lock.
    $launch=Get-Content -LiteralPath (Join-Path $Settings.botRoot 'state/local-bot-launch.json') -Raw|ConvertFrom-Json
    $owner=Invoke-CimMethod -InputObject $process -MethodName GetOwnerSid -ErrorAction Stop
    if($owner.ReturnValue -ne 0){throw 'Cannot verify bot process owner'}
    $created=Assert-LocalBotChildIdentity $Settings $ExpectedSid $holder $launch $process $owner.Sid
    $bundle=[IO.Path]::GetDirectoryName([string]$launch.runtime)
    $acl=Get-Acl -LiteralPath $bundle
    if(-not $acl.AreAccessRulesProtected -or $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin @('S-1-5-18','S-1-5-32-544')){throw 'Bot release is not trusted-owned and protected'}
    foreach($rule in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])){
        if($rule.AccessControlType -eq 'Allow' -and $rule.IdentityReference.Value -notin @('S-1-5-18','S-1-5-32-544')){throw 'Bot release parent is not private'}
    }
    $owned=[Diagnostics.Process]::GetProcessById([int]$holder.pid)
    try{
        # Hold this process generation's handle; never kill a reused PID or tree.
        $null=$owned.Handle
        if([Math]::Abs(($owned.StartTime.ToUniversalTime()-$created).TotalSeconds) -ge 0.01){throw 'Bot process generation changed before stop'}
        $owned.Kill()
        if(-not $owned.WaitForExit(15000)){throw 'Verified old bot child did not stop'}
    }finally{$owned.Dispose()}
}
