#requires -Version 7.4
[CmdletBinding()]param()
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. "$root/KESAS-VPS-Server/Update.Common.ps1"
. "$PSScriptRoot/Freeze-LocalSource.ps1"
$passed=0
function Check([bool]$Value,[string]$Label){if(-not $Value){throw $Label};$script:passed++;Write-Output "PASS $Label"}
function Reject([scriptblock]$Action,[string]$Label){$caught=$false;try{& $Action|Out-Null}catch{$caught=$true};Check $caught $Label}
Assert-ReleaseJsonUnique '{"a":1,"nested":[{"a":2}]}'
Check $true 'ordinary JSON and repeated names in separate objects accepted'
foreach($json in @('{"a":1,"a":2}','{"a":1,"\u0061":2}','{"x":[{"a":1,"a":2}]}','{"a":1,}','{/*comment*/"a":1}','{"a":1} garbage')){Reject {Assert-ReleaseJsonUnique $json} 'duplicate, escaped duplicate, nested duplicate or malformed JSON rejected'}
foreach($path in @('../x','x/../y','/x','x\y','x//y','x/CON.txt','x/COM¹.txt','x/a.','x/a ','x/a:b','x/NUL')){Reject {Get-UpdateRelativePath $path} 'unsafe Windows relative path rejected'}
Check ((Get-UpdateRelativePath 'safe path/file.json') -ceq 'safe path/file.json') 'spaces accepted without changing path identity'
$git=(Get-Command git.exe -ErrorAction Stop).Source
$temp=Join-Path ([IO.Path]::GetTempPath()) ('APF-publisher-speed-'+[Guid]::NewGuid().ToString('N'))
$null=[IO.Directory]::CreateDirectory($temp)
try{
    $repo=Join-Path $temp 'repo';$null=[IO.Directory]::CreateDirectory($repo)
    & $git -C $repo init --quiet
    [IO.File]::WriteAllText("$repo/.gitattributes","*.ps1 text eol=crlf`n")
    [IO.File]::WriteAllText("$repo/one.ps1","first`r`nsecond`r`n")
    [IO.File]::WriteAllText("$repo/file with spaces.ps1","third`r`n")
    & $git -C $repo add -- .
    & $git -C $repo -c user.name=Fixture -c user.email=fixture@example.invalid commit --quiet -m fixture
    $commit=(& $git -C $repo rev-parse HEAD).Trim()
    $dest=Join-Path $temp 'copied';$null=[IO.Directory]::CreateDirectory($dest)
    $clock=[Diagnostics.Stopwatch]::StartNew()
    $files=@(Copy-ReviewedLocalSource $git $repo $dest $commit @('.gitattributes','one.ps1','file with spaces.ps1'))
    $clock.Stop()
    Check ($files.Count -eq 3) 'batched source freeze accepts spaces and Git CRLF attributes'
    foreach($file in $files){Check ((Get-FileHash -LiteralPath (Join-Path $repo $file.path)).Hash -ceq (Get-FileHash -LiteralPath (Join-Path $dest $file.path)).Hash) 'frozen bytes identical'}
    [IO.File]::AppendAllText("$repo/one.ps1",'dirty')
    Reject {Copy-ReviewedLocalSource $git $repo "$temp/dirty-copy" $commit @('one.ps1')} 'modified tracked source rejected'
    [IO.File]::WriteAllText("$repo/untracked.ps1",'untracked')
    Reject {Copy-ReviewedLocalSource $git $repo "$temp/untracked-copy" $commit @('untracked.ps1')} 'untracked executable source rejected'
    Check (-not [IO.Directory]::Exists("$temp/dirty-copy") -and -not [IO.Directory]::Exists("$temp/untracked-copy")) 'all source blobs checked before any copy'
    [IO.File]::WriteAllText("$repo/one.ps1",'can-write-again')
    Check $true 'source handles released after success and rejection'
    $text='{"files":['+((1..2000|ForEach-Object{'{"path":"file'+$_+'","sha256":"'+('a'*64)+'","sizeBytes":123}'}) -join ',')+']}'
    $clockJson=[Diagnostics.Stopwatch]::StartNew();1..20|ForEach-Object{Assert-ReleaseJsonUnique $text};$clockJson.Stop()
    $clockPath=[Diagnostics.Stopwatch]::StartNew();1..1000|ForEach-Object{$null=Assert-UpdatePath "$repo/one.ps1"};$clockPath.Stop()
    # Compare equivalent trusted legacy implementations, not fabricated delays.
    $legacyText=(& $git -C $root show HEAD:KESAS-VPS-Server/Update.Common.ps1) -join "`n"
    $tokens=$null;$errors=$null;$ast=[Management.Automation.Language.Parser]::ParseInput($legacyText,[ref]$tokens,[ref]$errors)
    foreach($name in @('Assert-ReleaseJsonUnique','Assert-UpdatePath','Get-UpdateRelativePath')){
        $definition=$ast.Find({param($node)$node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name},$true).Extent.Text
        $definition=[regex]::Replace($definition,'(?m)^.*if\(''Aetherfront\.Deployment\.FastChecks''[^\r\n]*\r?\n','')
        foreach($replace in @('Assert-ReleaseJsonUnique','Assert-UpdatePath','Get-UpdateRelativePath')){$definition=$definition.Replace($replace,('Legacy-'+$replace))}
        . ([scriptblock]::Create($definition))
    }
    $legacyJson=[Diagnostics.Stopwatch]::StartNew();Legacy-Assert-ReleaseJsonUnique $text;$legacyJson.Stop()
    $legacyPath=[Diagnostics.Stopwatch]::StartNew();1..100|ForEach-Object{$null=Legacy-Assert-UpdatePath "$repo/one.ps1"};$legacyPath.Stop()
    foreach($path in @('safe path/file.json','x/COM¹.txt','x/a.','x/CON.txt','x/a:b','../x','x\\y','x//y')){
        $old=$true;$new=$true;try{$null=Legacy-Get-UpdateRelativePath $path}catch{$old=$false};try{$null=Get-UpdateRelativePath $path}catch{$new=$false}
        Check ($old -eq $new) 'managed and legacy path accept/reject match'
    }
    Write-Output (ConvertTo-Json @{passed=$passed;sourceFreeze3FilesMs=$clock.Elapsed.TotalMilliseconds;json2000Files20PassesMs=$clockJson.Elapsed.TotalMilliseconds;legacyJson2000Files1PassMs=$legacyJson.Elapsed.TotalMilliseconds;path1000PassesMs=$clockPath.Elapsed.TotalMilliseconds;legacyPath100PassesMs=$legacyPath.Elapsed.TotalMilliseconds;utc=[DateTime]::UtcNow.ToString('o')} -Compress)
}finally{
    # Delete only this newly-created, resolved temporary fixture, never a caller path.
    $resolved=[IO.Path]::GetFullPath($temp)
    if(-not $resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\APF-publisher-speed-',[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe fixture cleanup path'}
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
