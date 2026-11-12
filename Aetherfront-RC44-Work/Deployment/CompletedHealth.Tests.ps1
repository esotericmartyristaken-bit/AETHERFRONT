$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'CompletedHealth.ps1')
$root=Join-Path ([IO.Path]::GetTempPath()) ('CompletedHealth-'+[Guid]::NewGuid().ToString('N'))
$script:attempt=0;$script:mode='valid'
function Invoke-CompletedHealthFetch([string]$Destination) {
    $script:attempt++
    if($script:mode -eq 'fail' -or ($script:mode -eq 'transient' -and $script:attempt -lt 3)){throw 'Simulated pinned transport failure'}
    $body=if($script:mode -eq 'bad-body'){'{"ok":true,"service":"wrong-service"}'}else{'{"ok":true,"service":"apf-pack-host"}'}
    [IO.File]::WriteAllText($Destination,$body)
    'fixture-hash'
}
$result=Assert-CompletedLocalHttps $root
if(-not $result.verified -or $result.attempt -ne 1){throw 'Valid health failed'}
$script:attempt=0;$script:mode='transient';$result=Assert-CompletedLocalHttps $root
if(-not $result.verified -or $result.attempt -ne 3){throw 'Bounded transient retry failed'}
foreach($mode in @('bad-body','fail')){
    $script:attempt=0;$script:mode=$mode;$rejected=$false
    try{Assert-CompletedLocalHttps $root|Out-Null}catch{$rejected=$true}
    if(-not $rejected -or $script:attempt -ne 3){throw "Failure did not fail closed: $mode"}
}
'PASS 4 final pinned-health fixtures; fixed HTTPS/pin enforcement separately covered by native candidate HTTPS tests. No production request or restart.'
