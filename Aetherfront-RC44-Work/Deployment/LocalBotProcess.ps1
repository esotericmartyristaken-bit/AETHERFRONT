#requires -Version 7.4
function Read-LocalBotReadinessRecord([string]$Path) {
    # A newly created singleton file can be observed before its write completes.
    # Missing/incomplete metadata is not ready, never permission to stop a PID.
    try{
        $stream=[IO.File]::Open($Path,'Open','Read',([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
        try{
            if($stream.Length -gt 8192){return $null}
            $reader=[IO.StreamReader]::new($stream)
            try{$text=$reader.ReadToEnd()}finally{$reader.Dispose()}
        }finally{$stream.Dispose()}
    }catch [IO.IOException]{if(($_.Exception.GetBaseException().HResult -band 65535) -in @(2,3,32,33)){return $null};throw}
    try{$value=$text|ConvertFrom-Json -ErrorAction Stop}catch{return $null}
    if($null -eq $value -or $value -isnot [pscustomobject] -or -not $value.PSObject.Properties['pid'] -or -not $value.PSObject.Properties['startedAt']){return $null}
    if(($value.pid -isnot [int] -and $value.pid -isnot [long]) -or $value.pid -le 0){return $null}
    try{$null=[DateTimeOffset]$value.startedAt}catch{return $null}
    return $value
}

function Get-LocalBotLaunchRecord($Settings,$Process) {
    $directory=Join-Path $Settings.botRoot 'state'
    $number=[int]$Process.ProcessId
    if($number -le 0){throw 'Invalid bot process PID'}
    $files=@(Get-ChildItem -LiteralPath $directory -Filter "local-bot-launch-*-$number.json" -File)
    if($files.Count -gt 32){throw 'Too many launch records for one bot PID'}
    # Migration from the old shared pointer and an interrupted legacy replace.
    $legacy=Join-Path $directory 'local-bot-launch.json'
    if([IO.File]::Exists($legacy)){$files+=Get-Item -LiteralPath $legacy}
    $temporary=@(Get-ChildItem -LiteralPath $directory -Filter 'local-bot-launch.json.*.tmp' -File)
    if($temporary.Count -gt 64){throw 'Too many interrupted legacy launch records'}
    $files+=$temporary
    $created=([DateTime]$Process.CreationDate).ToUniversalTime()
    foreach($file in $files|Sort-Object LastWriteTimeUtc -Descending){
        if($file.Length -gt 8192){continue}
        try{
            $value=[IO.File]::ReadAllText($file.FullName)|ConvertFrom-Json
            if($value.pid -eq $number -and [Math]::Abs(($created-([DateTimeOffset]$value.startedAt).UtcDateTime).TotalSeconds) -lt 2){return $value}
        }catch{continue} # A concurrently written record cannot authorize a stop.
    }
    return $null
}

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
    $launch=Get-LocalBotLaunchRecord $Settings $process
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
