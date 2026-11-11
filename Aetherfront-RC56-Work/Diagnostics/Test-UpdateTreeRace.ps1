#requires -Version 7.5
param([Parameter(Mandatory)][string]$FixtureParent)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '../../KESAS-VPS-Server/Update.Common.ps1')
$root=Join-Path $FixtureParent ('TreeRace-'+[Guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $root
$live=Join-Path $root 'Live';$null=New-Item -ItemType Directory -Path $live
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Threading.Tasks;
public static class UpdateTreeRaceFixture {
 public static Task Run(string root) {return Task.Run(()=>{
  var canonical=Path.Combine(root,"status.json");File.WriteAllText(canonical,"{}");
  for(int i=0;i<1000;i++) {
   var temporary=canonical+"~RF"+i.ToString("D8")+".TMP";
   File.WriteAllText(temporary,"{}");
   // Windows may transiently deny replacement during metadata inspection.
   // Retry the fixture writer only; the walk itself must succeed unassisted.
   for(int attempt=0;;attempt++) {
    try {File.Replace(temporary,canonical,null);break;}
    catch(IOException) {if(attempt>=100)throw;System.Threading.Thread.Sleep(1);}
   }
  }
 });}
}
'@
$writer=[UpdateTreeRaceFixture]::Run($live)
$walks=0
do {Assert-UpdateTree $live;$walks++}while(-not $writer.IsCompleted -or $walks -lt 100)
$writer.GetAwaiter().GetResult()
Assert-UpdateTree $live
$target=Join-Path $root 'Target';$null=New-Item -ItemType Directory -Path $target
$junction=Join-Path $live 'RefusedJunction'
$null=New-Item -ItemType Junction -Path $junction -Target $target
$reparseRefused=$false
try {Assert-UpdateTree $live}catch {if($_.Exception.Message -notlike 'Refusing reparse point:*'){throw};$reparseRefused=$true}
if(-not $reparseRefused){throw 'Reparse-point guard was bypassed'}
$missingReadRefused=$false
try {Read-UpdateText (Join-Path $root 'missing-required-input.json')}catch {$missingReadRefused=$true}
if(-not $missingReadRefused){throw 'Missing required input was accepted'}
$result=@{observedAtUtc=[DateTime]::UtcNow.ToString('o');fixture=$root;atomicReplacements=1000;treeWalks=$walks;racePassed=$true;reparseRefused=$reparseRefused;missingRequiredReadRefused=$missingReadRefused}
$json=$result|ConvertTo-Json
[IO.File]::WriteAllText((Join-Path $root 'Result.json'),$json,[Text.UTF8Encoding]::new($false))
$json
