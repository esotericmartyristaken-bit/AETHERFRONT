#requires -Version 7.4
param([Parameter(Mandatory)][string]$TestRoot,[Parameter(Mandatory)][string]$UpdateToolRoot)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath($TestRoot)
if(-not [IO.Path]::IsPathFullyQualified($TestRoot) -or $root.StartsWith('\\') -or (Test-Path -LiteralPath $root)){throw 'Use a new disposable absolute local test directory.'}
for($cursor=[IO.Path]::GetDirectoryName($root);$cursor;$cursor=[IO.Path]::GetDirectoryName($cursor)){
    if((Test-Path -LiteralPath $cursor) -and ([IO.File]::GetAttributes($cursor) -band [IO.FileAttributes]::ReparsePoint)){throw 'Unsafe test directory'}
}
$null=[IO.Directory]::CreateDirectory($root)
$repository=Split-Path -Parent $PSScriptRoot
$node=(Get-Command node.exe -ErrorAction Stop).Source
if(-not (Test-Path -LiteralPath "$UpdateToolRoot/ApfUpdateTool.exe") -or -not (Test-Path -LiteralPath "$(Split-Path -Parent $UpdateToolRoot)/ClientTemplate.zip")){throw 'Private native publication prerequisites are absent.'}
$original=$env:APF_TEST_UPDATE_TOOL
$report=[ordered]@{schemaVersion=1;startedAt=[DateTime]::UtcNow.ToString('o');complete=$false;nativePublicationExercised=$false;suites=@();sourceCommit=(& git -C $repository rev-parse HEAD).Trim()}
try {
    $env:APF_TEST_UPDATE_TOOL=[IO.Path]::GetFullPath($UpdateToolRoot)
    foreach($suite in @('bot','control')){
        $junit=Join-Path $root "$suite.xml"
        $working=if($suite -ceq 'bot'){Join-Path $repository 'APFDiscordBot'}else{$repository}
        Push-Location -LiteralPath $working
        try {
            if($suite -ceq 'bot'){& $node --test --test-reporter=junit "--test-reporter-destination=$junit"}
            else{& $node --test --test-reporter=junit "--test-reporter-destination=$junit" 'ControlPlane/test/*.test.mjs'}
            $exitCode=$LASTEXITCODE
        }finally{Pop-Location}
        $xml=[xml](Get-Content -LiteralPath $junit -Raw)
        $cases=@($xml.SelectNodes('//testcase'))
        $failed=@($cases|Where-Object {$_.SelectSingleNode('failure') -or $_.SelectSingleNode('error')}).Count
        $skipped=@($cases|Where-Object {$_.SelectSingleNode('skipped')}).Count
        $report.suites+=@([ordered]@{name=$suite;tests=$cases.Count;passed=$cases.Count-$failed-$skipped;failed=$failed;skipped=$skipped;exitCode=$exitCode})
        if($suite -ceq 'control'){
            $native=@($cases|Where-Object {$_.name -like '*same controller publication path*' -and -not $_.SelectSingleNode('skipped') -and -not $_.SelectSingleNode('failure')})
            $report.nativePublicationExercised=$native.Count -eq 1
        }
        if($exitCode -ne 0 -or $failed -gt 0 -or ($suite -ceq 'control' -and $skipped -gt 0)){throw 'Host control acceptance failed; preserve isolated evidence.'}
    }
    if(-not $report.nativePublicationExercised){throw 'Full controller/native media acceptance was not exercised.'}
    $report.complete=$true
}finally{
    $env:APF_TEST_UPDATE_TOOL=$original
    $report['finishedAt']=[DateTime]::UtcNow.ToString('o')
    [IO.File]::WriteAllText((Join-Path $root 'results.json'),($report|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
}
$report|ConvertTo-Json -Depth 8 -Compress
