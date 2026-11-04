#requires -Version 7.4
. (Join-Path $PSScriptRoot 'LocalBotProcess.ps1')
function Assert-LocalBotIdle($Settings,$Configuration) {
    $state=Get-Content -LiteralPath (Join-Path $Settings.botRoot $Configuration.updates.stateFile) -Raw|ConvertFrom-Json
    if($state.schemaVersion -ne 1 -or $state.status -notin @('idle','failed')){throw 'A bot update was queued during local preparation; preserve it for review'}
    foreach($file in Get-ChildItem -LiteralPath (Join-Path $Settings.root 'native-cache/results') -Filter '*.json' -File){
        $request=Get-Content -LiteralPath $file.FullName -Raw|ConvertFrom-Json
        if($request.phase -notin @('healthy','blocked')){throw 'A native operation appeared during local preparation; preserve it for review'}
    }
}
function Install-LocalBot([string]$Bundle,$Settings,[string]$BotConfig) {
    $configuration=Get-Content -LiteralPath $BotConfig -Raw|ConvertFrom-Json
    if($configuration.updates.hostDeploymentMode -cne 'local-only' -or $configuration.updates.githubDeployment.enabled -ne $false){throw 'Local-only host deployment configuration required'}
    $state=Get-Content -LiteralPath (Join-Path $Settings.botRoot $configuration.updates.stateFile) -Raw|ConvertFrom-Json
    if($state.schemaVersion -ne 1 -or $state.status -notin @('idle','failed')){throw 'Cannot replace a bot with a pending native operation'}
    $scheduler=New-Object -ComObject 'Schedule.Service';$scheduler.Connect();$folder=$scheduler.GetFolder('\')
    $bot=$folder.GetTask('APF Discord Bot');$remote=$folder.GetTask('Aetherfront Native Publisher')
    $definition=$bot.Definition
    if($definition.Principal.LogonType -ne 2 -or $definition.Principal.RunLevel -ne 0){throw 'Local bot expects the existing limited S4U identity; do not elevate it'}
    $account=$definition.Principal.UserId
    $sid=if($account -match '^S-1-'){[Security.Principal.SecurityIdentifier]::new($account)}else{[Security.Principal.NTAccount]::new($account).Translate([Security.Principal.SecurityIdentifier])}
    if($sid.Value -in @('S-1-5-18','S-1-5-32-544')){throw 'Do not run the Discord bot as administrator or SYSTEM'}
    $runtime=Join-Path $Bundle 'BotRuntime'
    if(Test-Path -LiteralPath $runtime){throw 'Use a new immutable local bot runtime'}
    $null=[IO.Directory]::CreateDirectory($runtime)
    foreach($relative in @('APFDiscordBot/src','APFDiscordBot/scripts','ControlPlane')){
        $destination=Join-Path $runtime $relative;$null=[IO.Directory]::CreateDirectory($destination)
        foreach($file in Get-ChildItem -LiteralPath (Join-Path $Bundle $relative) -File){Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $destination $file.Name)}
    }
    foreach($name in @('package.json','package-lock.json')){Copy-Item -LiteralPath (Join-Path $Bundle "APFDiscordBot/$name") -Destination (Join-Path $runtime "APFDiscordBot/$name")}
    Copy-Item -LiteralPath (Join-Path $Bundle 'Aetherfront-RC44-Work/Deployment/Start-LocalBot.ps1') -Destination (Join-Path $runtime 'Start-LocalBot.ps1')
    $start=[Diagnostics.ProcessStartInfo]::new();$start.FileName=$Settings.node;$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.WorkingDirectory=Join-Path $runtime 'APFDiscordBot'
    foreach($argument in @($Settings.npm,'ci','--ignore-scripts','--omit=dev','--no-audit','--no-fund','--cache',(Join-Path $Bundle 'npm-cache'))){$start.ArgumentList.Add($argument)}
    foreach($name in @('APF_DISCORD_TOKEN','GH_TOKEN','GITHUB_TOKEN','NODE_OPTIONS')){$null=$start.Environment.Remove($name)}
    $npm=[Diagnostics.Process]::Start($start)
    try{if(-not $npm.WaitForExit(300000)){try{$npm.Kill($true)}catch{};throw 'Local bot dependency preparation timed out; production remains running'};if($npm.ExitCode -ne 0){throw 'Local bot dependency verification failed; production remains running'}}finally{$npm.Dispose()}
    # Only this sanitized code/dependency subtree is readable by the limited bot.
    # Candidate certificates, operator reports and signing evidence stay private.
    $acl=Get-Acl -LiteralPath $runtime;$acl.SetAccessRuleProtection($true,$true)
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,'ReadAndExecute','ContainerInherit,ObjectInherit','None','Allow'))
    Set-Acl -LiteralPath $runtime -AclObject $acl
    [IO.File]::WriteAllText((Join-Path $Bundle 'prior-bot-task.xml'),$bot.Xml)
    [IO.File]::WriteAllText((Join-Path $Bundle 'prior-remote-task.xml'),$remote.Xml)
    Assert-LocalBotIdle $Settings $configuration
    $remote.Enabled=$false
    # Scheduler Stop can leave a redirected Node child alive. Disable triggers
    # first, then stop only the independently verified singleton generation.
    $bot.Enabled=$false
    if($remote.State -eq 4){$remote.Stop(0)}
    if($bot.State -eq 4){$bot.Stop(0)}
    $timer=[Diagnostics.Stopwatch]::StartNew()
    while($bot.State -eq 4 -or $remote.State -eq 4){if($timer.Elapsed.TotalSeconds -gt 60){throw 'Control tasks did not stop; production untouched'};Start-Sleep -Milliseconds 200}
    Assert-LocalBotIdle $Settings $configuration
    Stop-LocalBotChild $Settings $sid.Value
    $definition.Settings.Enabled=$true
    $definition.Actions.Clear();$action=$definition.Actions.Create(0)
    $action.Path=Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
    $action.Arguments="-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$runtime/Start-LocalBot.ps1`" -Settings `"$($Settings.root)/host.json`""
    $action.WorkingDirectory=$Settings.botRoot
    $definition.Settings.RestartCount=3;$definition.Settings.RestartInterval='PT1M'
    $null=$folder.RegisterTaskDefinition('APF Discord Bot',$definition,6,$account,$null,2)
    $bot=$folder.GetTask('APF Discord Bot');$launched=[DateTime]::UtcNow;$null=$bot.Run($null)
    $timer=[Diagnostics.Stopwatch]::StartNew();$ready=$false;$expectedEntry=(Join-Path $runtime 'APFDiscordBot/src/index.js').Replace('/','\')
    while($timer.Elapsed.TotalSeconds -lt 120){
        $lockPath=Join-Path $Settings.botRoot 'state/bot-instance.lock'
        if([IO.File]::Exists($lockPath)){
            $holder=Get-Content -LiteralPath $lockPath -Raw|ConvertFrom-Json
            if($holder.pid -is [long] -or $holder.pid -is [int]){
                $process=Get-CimInstance Win32_Process -Filter "ProcessId=$($holder.pid)" -ErrorAction Stop
                if($process -and $process.ExecutablePath -ieq $Settings.node -and $process.CommandLine.Replace('/','\').Contains($expectedEntry,[StringComparison]::OrdinalIgnoreCase) -and $process.CreationDate.ToUniversalTime() -ge $launched.AddSeconds(-2)){
                    $launch=Get-LocalBotLaunchRecord $Settings $process
                    $stateRoot=[IO.Path]::GetFullPath((Join-Path $Settings.botRoot 'state')).TrimEnd('\')+'\'
                    if($launch -and $launch.pid -eq $holder.pid -and $launch.runtime -ieq $runtime -and [Math]::Abs(($process.CreationDate.ToUniversalTime()-([DateTimeOffset]$launch.startedAt).UtcDateTime).TotalSeconds) -lt 2 -and [IO.Path]::GetFullPath($launch.stdout).StartsWith($stateRoot,[StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($launch.stdout) -cmatch '^local-bot-[0-9TZ]+-[0-9]+\.stdout\.log$'){
                        $stream=[IO.FileStream]::new($launch.stdout,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
                        try{$reader=[IO.StreamReader]::new($stream);try{$text=$reader.ReadToEnd()}finally{$reader.Dispose()}}finally{$stream.Dispose()}
                        if($text.Contains('APF Discord Bot running from ')){$ready=$true}
                    }
                }
            }
        }
        if($ready){break}
        Start-Sleep -Milliseconds 500
    }
    if(-not $ready){throw 'Local bot did not acknowledge authenticated startup; inspect its private logs; production remains running'}
    [pscustomobject]@{runtime=$runtime;limitedIdentity=$sid.Value;remoteTaskDisabled=$true;serverTaskChanged=$false;authenticatedStartup=$true;botPid=$holder.pid}
}
