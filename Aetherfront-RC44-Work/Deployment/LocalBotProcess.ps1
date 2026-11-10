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

function Test-LocalBotRuntimeContent([string]$Bundle,[string]$Runtime) {
    foreach($relative in @('APFDiscordBot/src','APFDiscordBot/scripts','ControlPlane')){
        $source=Join-Path $Bundle $relative;$target=Join-Path $Runtime $relative
        if(-not [IO.Directory]::Exists($source) -or -not [IO.Directory]::Exists($target)){return $false}
        foreach($directory in @($source,$target)){
            if((Get-Item -LiteralPath $directory -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Bot code directory is a reparse point'}
            if(@(Get-ChildItem -LiteralPath $directory -Directory -Force).Count){return $false}
        }
        $expected=@(Get-ChildItem -LiteralPath $source -File -Force|Sort-Object Name)
        $actual=@(Get-ChildItem -LiteralPath $target -File -Force|Sort-Object Name)
        if($expected.Count -ne $actual.Count){return $false}
        for($index=0;$index -lt $expected.Count;$index++){
            if($expected[$index].Name -cne $actual[$index].Name){return $false}
            foreach($file in @($expected[$index],$actual[$index])){if($file.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Bot code file is a reparse point'}}
            if((Get-FileHash -LiteralPath $expected[$index].FullName).Hash -cne (Get-FileHash -LiteralPath $actual[$index].FullName).Hash){return $false}
        }
    }
    foreach($pair in @(
        @('APFDiscordBot/package.json','APFDiscordBot/package.json'),
        @('APFDiscordBot/package-lock.json','APFDiscordBot/package-lock.json'),
        @('Aetherfront-RC44-Work/Deployment/Start-LocalBot.ps1','Start-LocalBot.ps1')
    )){
        $source=Join-Path $Bundle $pair[0];$target=Join-Path $Runtime $pair[1]
        if(-not [IO.File]::Exists($source) -or -not [IO.File]::Exists($target)){return $false}
        foreach($file in @($source,$target)){if((Get-Item -LiteralPath $file -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Bot launch/package file is a reparse point'}}
        if((Get-FileHash -LiteralPath $source).Hash -cne (Get-FileHash -LiteralPath $target).Hash){return $false}
    }
    return $true
}

function Test-LocalBotStartupBanner([string]$Path) {
    $stream=[IO.FileStream]::new($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    try{
        $reader=[IO.StreamReader]::new($stream)
        try{$buffer=[char[]]::new(65536);$count=$reader.ReadBlock($buffer,0,$buffer.Length);$text=[string]::new($buffer,0,$count)}finally{$reader.Dispose()}
    }finally{$stream.Dispose()}
    return $text.Contains('APF Discord Bot running from ')
}

function Assert-LocalBotTrustedRelease([string]$Runtime) {
    $bundle=[IO.Path]::GetDirectoryName($Runtime)
    $acl=Get-Acl -LiteralPath $bundle
    if(-not $acl.AreAccessRulesProtected -or $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin @('S-1-5-18','S-1-5-32-544')){throw 'Bot release is not trusted-owned and protected'}
    foreach($rule in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])){
        if($rule.AccessControlType -eq 'Allow' -and $rule.IdentityReference.Value -notin @('S-1-5-18','S-1-5-32-544')){throw 'Bot release parent is not private'}
    }
}

function Get-ReusableLocalBot([string]$Bundle,$Settings,$Bot,$Remote,[string]$ExpectedSid) {
    if($Bot.State -ne 4 -or $Remote.Enabled -or $Remote.State -eq 4){return $null}
    $holder=Read-LocalBotReadinessRecord (Join-Path $Settings.botRoot 'state/bot-instance.lock')
    if(-not $holder){return $null}
    $process=Get-CimInstance Win32_Process -Filter "ProcessId=$($holder.pid)" -ErrorAction Stop
    if(-not $process){return $null}
    $launch=Get-LocalBotLaunchRecord $Settings $process
    if(-not $launch){return $null}
    $owner=Invoke-CimMethod -InputObject $process -MethodName GetOwnerSid -ErrorAction Stop
    if($owner.ReturnValue -ne 0){throw 'Cannot verify existing bot process owner'}
    $null=Assert-LocalBotChildIdentity $Settings $ExpectedSid $holder $launch $process $owner.Sid
    $runtime=[IO.Path]::GetFullPath([string]$launch.runtime)
    Assert-LocalBotTrustedRelease $runtime
    $definition=$Bot.Definition
    if($definition.Actions.Count -ne 1){return $null}
    $action=$definition.Actions.Item(1)
    $expectedArguments="-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$runtime/Start-LocalBot.ps1`" -Settings `"$($Settings.root)/host.json`""
    if($action.Path -ine (Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe') -or $action.Arguments -cne $expectedArguments -or $action.WorkingDirectory -ine $Settings.botRoot){return $null}
    if(-not (Test-LocalBotRuntimeContent $Bundle $runtime)){return $null}
    $stateRoot=[IO.Path]::GetFullPath((Join-Path $Settings.botRoot 'state')).TrimEnd('\')+'\'
    if(-not [IO.Path]::GetFullPath($launch.stdout).StartsWith($stateRoot,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($launch.stdout) -cnotmatch '^local-bot-[0-9TZ]+-[0-9]+\.stdout\.log$'){throw 'Existing bot readiness log is outside its state directory'}
    if(-not (Test-LocalBotStartupBanner $launch.stdout)){return $null}
    # Re-read the singleton after content checks to reject a generation switch.
    $current=Read-LocalBotReadinessRecord (Join-Path $Settings.botRoot 'state/bot-instance.lock')
    if(-not $current -or $current.pid -ne $holder.pid -or ([DateTimeOffset]$current.startedAt) -ne ([DateTimeOffset]$holder.startedAt)){throw 'Existing bot generation changed during reuse verification'}
    $latest=Get-CimInstance Win32_Process -Filter "ProcessId=$($holder.pid)" -ErrorAction Stop
    if(-not $latest){throw 'Existing bot exited during reuse verification'}
    $latestOwner=Invoke-CimMethod -InputObject $latest -MethodName GetOwnerSid -ErrorAction Stop
    if($latestOwner.ReturnValue -ne 0){throw 'Cannot recheck existing bot process owner'}
    $null=Assert-LocalBotChildIdentity $Settings $ExpectedSid $current $launch $latest $latestOwner.Sid
    return [pscustomobject]@{runtime=$runtime;limitedIdentity=$ExpectedSid;remoteTaskDisabled=$true;serverTaskChanged=$false;authenticatedStartup=$true;botPid=$holder.pid;reused=$true}
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
    Assert-LocalBotTrustedRelease ([string]$launch.runtime)
    $owned=[Diagnostics.Process]::GetProcessById([int]$holder.pid)
    try{
        # Hold this process generation's handle; never kill a reused PID or tree.
        $null=$owned.Handle
        if([Math]::Abs(($owned.StartTime.ToUniversalTime()-$created).TotalSeconds) -ge 0.01){throw 'Bot process generation changed before stop'}
        $owned.Kill()
        if(-not $owned.WaitForExit(15000)){throw 'Verified old bot child did not stop'}
    }finally{$owned.Dispose()}
}
