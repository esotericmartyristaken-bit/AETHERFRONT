#requires -Version 7.4
# Extract only pure/local maintenance helpers. Never execute installer entry code.
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$source=Join-Path $PSScriptRoot '../Repair-HostControl.ps1'
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($source,[ref]$tokens,[ref]$errors)
if($errors.Count){throw ($errors|Out-String)}
$helpers=@('Assert-RepairHash','New-RepairSnapshot','Backup-RepairBootstrap','Install-RepairBootstrap','Restore-RepairBootstrap','Suspend-RepairTasks','Restore-RepairTaskState','Test-RepairPreservation')
foreach($name in $helpers){
    $definition=@($ast.FindAll({param($node)$node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name},$true))
    if($definition.Count -ne 1){throw "Missing isolated helper: $name"}
    Invoke-Expression $definition[0].Extent.Text
}
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('repair-control-test-'+[Guid]::NewGuid().ToString('N'))
$null=[IO.Directory]::CreateDirectory($fixture)
$script:copyFailure=$null
function Assert-UpdatePath([string]$Path){
    $full=[IO.Path]::GetFullPath($Path)
    if(-not $full.StartsWith($fixture+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Test path escapes isolated fixture'}
    return $full
}
function Protect-RepairDirectory([string]$Path){$null=Assert-UpdatePath $Path;$null=[IO.Directory]::CreateDirectory($Path)}
function Copy-UpdateAtomic([string]$Source,[string]$Destination){
    $null=Assert-UpdatePath $Source;$null=Assert-UpdatePath $Destination
    if($Destination -ceq $script:copyFailure){throw 'Injected isolated copy failure'}
    [IO.File]::Copy($Source,$Destination,$true)
}
function Protect-Code([string]$Path){$null=Assert-UpdatePath $Path}
function Assert([bool]$Condition,[string]$Message){if(-not $Condition){throw $Message}}
function New-FakeTask([bool]$Running,[bool]$Enabled){
    $task=[pscustomobject]@{State=$(if($Running){4}else{3});Enabled=$Enabled;starts=0}
    $task|Add-Member ScriptMethod Stop {param($flags)$this.State=3}
    $task|Add-Member ScriptMethod GetInstances {param($flags)return [pscustomobject]@{Count=$(if($this.State -eq 4){1}else{0})}}
    $task|Add-Member ScriptMethod Run {param($parameters)$this.State=4;$this.starts++;return $null}
    return $task
}
try{
    $inputDirectory=Join-Path $fixture 'source';$snapshot=Join-Path $fixture 'snapshot';$bootstrap=Join-Path $fixture 'bootstrap';$backup=Join-Path $fixture 'backup'
    foreach($directory in @($inputDirectory,$bootstrap)){$null=[IO.Directory]::CreateDirectory($directory)}
    $names=@('anchor.mjs','engine.mjs','new-module.mjs');$hashes=@{}
    foreach($name in $names){[IO.File]::WriteAllText((Join-Path $inputDirectory $name),"reviewed-$name");$hashes[$name]=(Get-FileHash -LiteralPath (Join-Path $inputDirectory $name)).Hash.ToLowerInvariant()}
    foreach($name in $names[0..1]){[IO.File]::WriteAllText((Join-Path $bootstrap $name),"prior-$name")}
    New-RepairSnapshot $inputDirectory $snapshot $names $hashes
    $rows=@(Backup-RepairBootstrap $bootstrap $backup $names)
    Assert ($rows.Count -eq 3 -and -not $rows[2].existed) 'Complete backup must record absent targets'
    [IO.File]::WriteAllText((Join-Path $inputDirectory 'anchor.mjs'),'changed after snapshot')
    $script:copyFailure=Join-Path $bootstrap 'engine.mjs';$failed=$false
    try{$null=Install-RepairBootstrap $bootstrap $snapshot $names $hashes}catch{$failed=$_.Exception.Message -ceq 'Injected isolated copy failure'}
    Assert $failed 'Partial installation failure was not injected'
    Assert ((Get-FileHash -LiteralPath (Join-Path $bootstrap 'anchor.mjs')).Hash.ToLowerInvariant() -ceq $hashes['anchor.mjs']) 'Installation must use reviewed snapshot, not changed source'
    $script:copyFailure=$null
    # Simulate a newly introduced file and a changed DACL before recovery.
    [IO.File]::WriteAllText((Join-Path $bootstrap 'new-module.mjs'),'partial new file')
    $changedAcl=Get-Acl -LiteralPath (Join-Path $bootstrap 'anchor.mjs');$changedAcl.SetAccessRuleProtection($true,$true)
    Set-Acl -LiteralPath (Join-Path $bootstrap 'anchor.mjs') -AclObject $changedAcl
    Restore-RepairBootstrap $bootstrap $backup $rows
    foreach($row in $rows){if($row.existed){Assert-RepairHash (Join-Path $bootstrap $row.name) $row.sha256;Assert ((Get-Acl -LiteralPath (Join-Path $bootstrap $row.name)).GetSecurityDescriptorSddlForm([Security.AccessControl.AccessControlSections]'Owner,Group,Access') -ceq $row.sddl) 'Rollback did not restore ACL'}}
    Assert (-not (Test-Path -LiteralPath (Join-Path $bootstrap 'new-module.mjs'))) 'Rollback must remove newly introduced files'
    $mismatchRejected=$false
    try{New-RepairSnapshot $inputDirectory (Join-Path $fixture 'rejected-snapshot') $names $hashes}catch{$mismatchRejected=$_.Exception.Message -ceq 'Reviewed repair snapshot differs'}
    Assert $mismatchRejected 'Snapshot must reject source changes against captured hashes'
    $tasks=@([pscustomobject]@{task=(New-FakeTask $true $true);running=$true;enabled=$true},[pscustomobject]@{task=(New-FakeTask $false $false);running=$false;enabled=$false})
    Suspend-RepairTasks $tasks
    Assert (@($tasks|Where-Object {$_.task.State -eq 4 -or $_.task.Enabled}).Count -eq 0) 'Maintenance must suspend both task triggers'
    Restore-RepairTaskState $tasks
    Assert ($tasks[0].task.State -eq 4 -and $tasks[0].task.Enabled -and $tasks[0].task.starts -eq 1 -and $tasks[1].task.State -eq 3 -and -not $tasks[1].task.Enabled -and $tasks[1].task.starts -eq 0) 'Prior running/enabled state must be restored independently'
    Assert (Test-RepairPreservation @{game=1788;pack=12108} @{game=1788;pack=12108}) 'Preservation equality rejected'
    Assert (-not (Test-RepairPreservation @{game=1788;pack=12108} @{game=1789;pack=12108})) 'Preservation must reject changed process identity'
    $text=$ast.Extent.Text
    Assert ($text.IndexOf('New-RepairSnapshot $PSScriptRoot') -lt $text.IndexOf('Suspend-RepairTasks $priorTasks')) 'Snapshot must precede control stop'
    Assert ($text.IndexOf('$backupRows=@(Backup-RepairBootstrap') -lt $text.IndexOf('Suspend-RepairTasks $priorTasks')) 'Complete backup must precede control stop'
    Assert ($text.IndexOf('Restore-RepairBootstrap $bootstrap $backup $backupRows') -lt $text.IndexOf('Restore-RepairTaskState $priorTasks')) 'Rollback files must precede task restart'
    Assert ($text.Contains('show "${SourceCommit}:SOURCE-MANIFEST.json"')) 'Expected hashes must come from immutable commit'
    'PASS: parse; pinned snapshot; source race; partial-copy rollback; ACL/absence restore; task-state restore; preservation; maintenance ordering'
}finally{
    $resolved=[IO.Path]::GetFullPath($fixture)
    if($resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()),[StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($resolved) -like 'repair-control-test-*'){Remove-Item -LiteralPath $resolved -Recurse -Force}
}
