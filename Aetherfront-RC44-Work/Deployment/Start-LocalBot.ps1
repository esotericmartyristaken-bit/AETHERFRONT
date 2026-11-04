param([Parameter(Mandatory)][string]$Settings)
$ErrorActionPreference='Stop'
$configuration=Get-Content -LiteralPath $Settings -Raw|ConvertFrom-Json
$runtime=[IO.Path]::GetFullPath($PSScriptRoot)
$configPath=Join-Path $configuration.botRoot 'config.json'
$bot=Get-Content -LiteralPath $configPath -Raw|ConvertFrom-Json
if($bot.updates.hostDeploymentMode -cne 'local-only' -or $bot.updates.githubDeployment.enabled -ne $false){throw 'Local bot requires local-only host deployment configuration'}
Add-Type -AssemblyName System.Security
$credential=Join-Path $configuration.root 'bootstrap/bot-credential.dpapi'
$bytes=[Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($credential),$null,[Security.Cryptography.DataProtectionScope]::LocalMachine)
try{$env:APF_DISCORD_TOKEN=[Text.Encoding]::UTF8.GetString($bytes)}finally{[Array]::Clear($bytes,0,$bytes.Length)}
$env:APF_BOT_ROOT=$configuration.botRoot;$env:APF_BOT_CONFIG=$configPath
$env:APF_CONTROL_RELEASE_ROOT=$runtime
$env:APF_CONTROL_ROOT=$null;$env:APF_MANAGED_CONTROL=$null
Set-Location -LiteralPath $configuration.botRoot
$stamp=[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')+'-'+$PID
$output=Join-Path $configuration.botRoot "state/local-bot-$stamp.stdout.log"
$errors=Join-Path $configuration.botRoot "state/local-bot-$stamp.stderr.log"
$child=$null
try{
    $child=Start-Process -FilePath $configuration.node -ArgumentList @("`"$runtime/APFDiscordBot/src/index.js`"") -WorkingDirectory $configuration.botRoot -WindowStyle Hidden -RedirectStandardOutput $output -RedirectStandardError $errors -PassThru
    # New immutable record per launch. A limited task must never replace an
    # administrator-owned shared pointer left by an older installer.
    $launchPath=Join-Path $configuration.botRoot "state/local-bot-launch-$stamp-$($child.Id).json"
    $temporary=$launchPath+'.tmp'
    [IO.File]::WriteAllText($temporary,(@{pid=$child.Id;startedAt=$child.StartTime.ToUniversalTime().ToString('o');stdout=$output;stderr=$errors;runtime=$runtime}|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
    [IO.File]::Move($temporary,$launchPath)
    $child.WaitForExit();$exitCode=$child.ExitCode;exit $exitCode
}catch{
    [IO.File]::WriteAllText($errors+'.launcher.txt',$_.Exception.ToString())
    throw
}finally{
    $env:APF_DISCORD_TOKEN=$null
    if($child){
        try{if(-not $child.HasExited){$child.Kill();$null=$child.WaitForExit(15000)}}finally{$child.Dispose()}
    }
}
