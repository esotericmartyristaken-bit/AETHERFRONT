#requires -Version 5.1
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '../../KESAS-VPS-Server/Update.Common.ps1')
Assert-ReleaseJsonUnique '{"a":1,"list":[{"a":2}]}'
$passed=1
foreach($text in @('{"a":1,"a":2}','{"a":1,"\u0061":2}','{"nested":{"x":1,"x":2}}','{"bad":')){
    $caught=$false;try{Assert-ReleaseJsonUnique $text}catch{$caught=$true};if(-not $caught){throw 'Invalid/duplicate JSON accepted'};$passed++
}
foreach($path in @('a/../b','a/NUL.txt','a/COM1.txt','a/name.','a/name ','a/x:y','a\\b')){
    $caught=$false;try{$null=Get-UpdateRelativePath $path}catch{$caught=$true};if(-not $caught){throw 'Unsafe path accepted'};$passed++
}
if((Get-UpdateRelativePath 'safe folder/file.txt') -cne 'safe folder/file.txt'){throw 'Valid relative path changed'};$passed++
$null=Assert-UpdatePath $PSCommandPath;$passed++
Write-Output "PASS $passed compatibility checks; PowerShell $($PSVersionTable.PSVersion); edition $($PSVersionTable.PSEdition)"
