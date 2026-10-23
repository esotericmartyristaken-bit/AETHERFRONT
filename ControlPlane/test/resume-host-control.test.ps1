#requires -Version 7.4
# Parse/extract the pure identity predicate. Never execute recovery entry code.
$ErrorActionPreference='Stop'
$tokens=$null;$errors=$null
$source=Join-Path $PSScriptRoot '../Resume-HostControlRepair.ps1'
$ast=[Management.Automation.Language.Parser]::ParseFile($source,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Resume script does not parse'}
$definition=@($ast.FindAll({param($node)$node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Test-ResumeIdentity'},$true))
if($definition.Count -ne 1){throw 'Identity predicate missing'}
Invoke-Expression $definition[0].Extent.Text
$started=[DateTime]::Parse('2026-10-23T09:08:48.4952460Z').ToUniversalTime()
$executable='C:\fixture\protected\pwsh.exe';$script='C:\fixture\reviewed\Repair-HostControl.ps1'
$line='"'+$executable+'" -NoProfile -File "'+$script+'" -SourceCommit 660486ea9c180c1716fd75b38edd7f8c24174293'
$process=[pscustomobject]@{ExecutablePath=$executable;CreationDate=$started;CommandLine=$line}
if(-not (Test-ResumeIdentity $process $executable $script $started)){throw 'Exact reviewed installer rejected'}
foreach($change in @(@{ExecutablePath='C:\fixture\other.exe'},@{CreationDate=$started.AddTicks(1)},@{CommandLine=$line.Replace('Repair-HostControl.ps1','Invoke-NativePublisher.ps1')},@{CommandLine=$line.Replace('660486ea9c180c1716fd75b38edd7f8c24174293',('a'*40))})){
    $candidate=[pscustomobject]@{ExecutablePath=$process.ExecutablePath;CreationDate=$process.CreationDate;CommandLine=$process.CommandLine}
    foreach($name in $change.Keys){$candidate.$name=$change[$name]}
    if(Test-ResumeIdentity $candidate $executable $script $started){throw 'Changed installer identity accepted'}
}
if(Test-ResumeIdentity $null $executable $script $started){throw 'Absent installer accepted'}
$text=$ast.Extent.Text
if($text.IndexOf('Native-Idle') -gt $text.IndexOf('Stop-Process -Id $InstallerPid -Force')){throw 'Native guard must precede installer stop'}
if(-not $text.Contains('-NoProfile -NonInteractive -File $installerPath -SourceCommit $SourceCommit')){throw 'Retry must prohibit hidden interactive prompts'}
if(-not $text.Contains('Restore-RepairBootstrap (Join-Path $root')){throw 'Protected rollback source missing'}
'PASS: exact process image/script/source/creation; absent and reused PID rejection; noninteractive retry; protected rollback retained'
