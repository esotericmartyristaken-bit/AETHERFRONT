#requires -Version 7.4
# Execute only extracted local guard functions against disposable state/fake task.
$ErrorActionPreference='Stop'
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot '../Manage-ControlWorker.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Worker management script does not parse'}
foreach($name in @('Assert-NoApplyingHelper','Stop-ControlWorker')){
    $functions=@($ast.FindAll({param($node)$node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name},$true))
    if($functions.Count -ne 1){throw 'Guard function missing'}
    Invoke-Expression $functions[0].Extent.Text
}
$directory=Join-Path ([IO.Path]::GetTempPath()) ('apf-worker-guard-'+[Guid]::NewGuid().ToString('N'))
$settings=@{root=(Join-Path $directory 'control');botRoot=(Join-Path $directory 'bot');nativeRoot=(Join-Path $directory 'native')}
foreach($name in @("$($settings.root)/native-cache/results","$($settings.botRoot)/state",$settings.nativeRoot)){$null=[IO.Directory]::CreateDirectory($name)}
$stateFile=Join-Path $settings.botRoot 'state/kesas-update-state.json'
$resultFile=Join-Path $settings.root ('native-cache/results/'+('a'*64)+'.json')
$task=[pscustomobject]@{State=4;stops=0}
$task|Add-Member ScriptMethod Stop {param($flags)$this.stops++;$this.State=3}
$lease=$null
function Assert([bool]$Value,[string]$Message){if(-not $Value){throw $Message}}
function State([string]$Status){[IO.File]::WriteAllText($stateFile,(@{schemaVersion=1;status=$Status;countdownStartedAt='preserved';deployAt='preserved';force=$false}|ConvertTo-Json -Compress));$task.State=4;$task.stops=0}
function Reject([string]$Mode){$rejected=$false;try{Stop-ControlWorker $settings $task $Mode}catch{$rejected=$true};Assert $rejected 'Unsafe worker stop was admitted';Assert ($task.stops -eq 0) 'Unsafe worker task was stopped'}
try{
    foreach($status in @('idle','failed','queued','deploying')){
        State $status;$before=[IO.File]::ReadAllText($stateFile)
        Stop-ControlWorker $settings $task 'recover-worker'
        Assert ($task.stops -eq 1) 'Lease-proved worker recovery did not stop fake task'
        Assert ([IO.File]::ReadAllText($stateFile) -ceq $before) 'Worker recovery changed durable application state'
    }
    State 'queued';Reject 'stop'
    State 'unknown';Reject 'recover-worker'
    State 'idle'
    $lease=[IO.FileStream]::new((Join-Path $settings.nativeRoot '.publish.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    Reject 'recover-worker';$lease.Dispose();$lease=$null
    foreach($nativePid in @($PID,$null)){
        State 'deploying';[IO.File]::WriteAllText($resultFile,(@{phase='applying';nativePid=$nativePid}|ConvertTo-Json))
        Reject 'recover-worker'
    }
    [IO.File]::WriteAllText($resultFile,(@{phase='healthy';nativePid=$null}|ConvertTo-Json))
    State 'deploying';Stop-ControlWorker $settings $task 'recover-worker'
    Assert ($task.stops -eq 1) 'Healthy native result incorrectly blocked control-only recovery'
    'PASS: queued/deploying recovery; unchanged countdown/state; ordinary stop guard; OS lease; live/ambiguous helper rejection; healthy native result'
}finally{
    if($lease){$lease.Dispose()}
    $resolved=[IO.Path]::GetFullPath($directory)
    if($resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()),[StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($resolved) -like 'apf-worker-guard-*'){Remove-Item -LiteralPath $resolved -Recurse -Force}
}
