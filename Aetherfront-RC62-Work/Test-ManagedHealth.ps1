$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '../KESAS-VPS-Server/Update.ManagedHealth.ps1')
$script:calls=0;$script:behavior='good';$checks=0
function Check([bool]$Value,[string]$Text){if(-not $Value){throw $Text};$script:checks++;Write-Output "PASS $Text"}
function Invoke-UpdatedHealthFetch([int]$Port,[string]$Pin){
 $script:calls++
 if($script:behavior -eq 'transient' -and $script:calls -eq 1){throw 'Service warming'}
 if($script:behavior -eq 'wrong-service'){return '{"ok":true,"service":"other"}'}
 if($script:behavior -eq 'not-ok'){return '{"ok":false,"service":"apf-pack-host"}'}
 if($script:behavior -eq 'invalid-json'){return '{'}
 return '{"ok":true,"service":"apf-pack-host"}'
}
$pin='34b4bf414b4923fdf8150d0c56a541d393a45bf76dd50ae10e93afc5d1358d09'
$proof=Assert-UpdatedHealth 18866 $pin;Check ($proof.verified -and $proof.attempt -eq 1 -and $calls -eq 1) 'Valid body and exact pin request accepted'
$script:behavior='transient';$script:calls=0;$proof=Assert-UpdatedHealth 18866 $pin;Check ($proof.attempt -eq 2 -and $calls -eq 2) 'Bounded transient startup retry succeeds'
foreach($case in @('wrong-service','not-ok','invalid-json')){
 $script:behavior=$case;$script:calls=0;$refused=$false
 try{$null=Assert-UpdatedHealth 18866 $pin}catch{$refused=$true}
 Check ($refused -and $calls -eq 3) ('Unsafe health response refused: '+$case)
}
$script:calls=0;$refused=$false;try{$null=Assert-UpdatedHealth 18866 'bad'}catch{$refused=$true};Check ($refused -and $calls -eq 0) 'Invalid certificate pin rejected before network'
$script:calls=0;$refused=$false;try{$null=Assert-UpdatedHealth 0 $pin}catch{$refused=$true};Check ($refused -and $calls -eq 0) 'Invalid endpoint rejected before network'
Write-Output "PASS $checks managed activation health checks; no processes restarted or policies changed"
