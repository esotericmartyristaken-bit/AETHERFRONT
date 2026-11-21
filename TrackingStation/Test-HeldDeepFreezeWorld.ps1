#requires -Version 7.5
param([string]$CandidateRuntime)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$apf=Split-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) -Parent
$root=Join-Path $apf 'Backups/KESAS-Bot/2026-11-21T005513416Z-35fe51'
$bin=Join-Path $apf 'UpdateBuilds/RC64-FullChain-20261113/Build-v5/Server'
if($CandidateRuntime){$bin=(Resolve-Path -LiteralPath $CandidateRuntime).Path}
$flags=[Reflection.BindingFlags]'Static,Public,NonPublic'
$a=[Reflection.Assembly]::LoadFrom((Join-Path $bin 'Server.dll'))
$null=[Reflection.Assembly]::LoadFrom((Join-Path $bin 'AetherfrontPersonalServer.dll'))
$codec=$a.GetType('LmpCommon.DeepFreezeScenarioCodec',$true)
$merge=$a.GetType('LmpCommon.DeepFreezeScenarioMerge',$true)
$validator=$a.GetType('Server.Aetherfront.AetherfrontDeepFreezeCheckpointValidator',$true)
$coord=$a.GetType('Server.Aetherfront.AetherfrontDeepFreezeCheckpointCoordinator',$true)
$vesselType=$a.GetType('Server.System.Vessel.Classes.Vessel',$true)
$readAssignments=$coord.GetMethod('ReadAssignments',$flags)
$assignmentType=$validator.GetNestedType('VesselAssignments',[Reflection.BindingFlags]'Public,NonPublic')
$parts=$assignmentType.GetField('Parts',[Reflection.BindingFlags]'Instance,Public,NonPublic')
$generic=[Collections.Generic.Dictionary[string,string]].GetGenericTypeDefinition()
$assignments=[Activator]::CreateInstance($generic.MakeGenericType([guid],$assignmentType))
$utf8=[Text.UTF8Encoding]::new($false,$true)
$scenario=[IO.File]::ReadAllBytes((Join-Path $root 'DeepFreeze/Scenario.txt'))
$r=Get-Content -LiteralPath 'C:/ProgramData/Aetherfront-KesasDiagnostics/Latest.json' -Raw | ConvertFrom-Json
$hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($scenario))
if (!$r.checksPassed -or $hash -ine $r.live.scenarioSha256) { throw 'Fixture does not match the verified live scenario' }
$original=$codec.GetMethod('Parse',$flags).Invoke($null,@($scenario,$scenario.Length))
$vessels=[Collections.Generic.Dictionary[guid,byte[]]]::new()
$awake=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$liveParts=[Collections.Generic.HashSet[uint32]]::new()
foreach ($file in Get-ChildItem -LiteralPath (Join-Path $root 'Vessels') -Filter '*.txt' -File) {
    $id=[guid]::Empty
    if (![guid]::TryParse($file.BaseName,[ref]$id)) { continue }
    $bytes=[IO.File]::ReadAllBytes($file.FullName)
    $vessel=[Activator]::CreateInstance($vesselType,@($utf8.GetString($bytes)))
    $assignment=$readAssignments.Invoke($null,@($id,$vessel))
    $assignments.Add($id,$assignment)
    foreach ($part in $parts.GetValue($assignment).GetEnumerator()) {
        $null=$liveParts.Add($part.Key)
        $awake.UnionWith($part.Value)
    }
    if ($original.Vessels.ContainsKey($id)) { $vessels.Add($id,$bytes) }
}
$held=@($original.Vessels.Keys|Where-Object {!$vessels.ContainsKey($_)})
$crewById=@($original.Kerbals.Values|Where-Object {$_.VesselGuid -in $held})
$conflicts=@($crewById|Where-Object {$awake.Contains($_.KerbalName)})
$reusedParts=@($original.Parts.Values|Where-Object {$_.VesselGuid -in $held -and $liveParts.Contains($_.FlightId)})
$keys=[guid[]]@($original.Vessels.Keys|Where-Object {$vessels.ContainsKey($_)})
$out=[ordered]@{authoritativeScenarioSha256=$hash;fixtureVessels=$assignments.Count;presentCommitted=$keys.Count;
    held=$held.Count;heldCrew=$crewById.Count;heldCrewAlsoAwake=$conflicts.Count;heldPartsAlsoInPresentVessels=$reusedParts.Count;
    validatedPresent=0;failure=$null}
try {
    for($i=0;$i -lt $keys.Count;$i+=64) {
        $batch=[guid[]]@($keys|Select-Object -Skip $i -First 64)
        $candidate=$merge.GetMethod('Select',$flags).Invoke($null,@($original,$batch))
        $protos=[Collections.Generic.Dictionary[guid,byte[]]]::new()
        foreach($id in $batch){$protos.Add($id,$vessels[$id])}
        $crew=[Collections.Generic.Dictionary[string,byte[]]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach($name in $candidate.Kerbals.Keys) {
            if ([IO.Path]::GetFileName($name) -cne $name) { throw 'Invalid roster filename' }
            $crew.Add($name,[IO.File]::ReadAllBytes((Join-Path $root ('Kerbals/'+$name+'.txt'))))
        }
        $null=$validator.GetMethod('ValidateWithAssignments',$flags).Invoke($null,@($original,$candidate,$protos,$crew,$assignments))
        $out.validatedPresent+=$batch.Count
    }
}catch{
    $out.failure=$_.Exception.GetBaseException().Message
}
$out|ConvertTo-Json -Compress
if($out.failure -or $conflicts.Count -or $reusedParts.Count){exit 2}
if(!$CandidateRuntime){return}

$copy=New-Item -ItemType Directory -Path (Join-Path ([IO.Path]::GetTempPath()) ('KESAS-Hold-'+[guid]::NewGuid().ToString('N')))
foreach($name in 'Vessels','Kerbals','DeepFreeze'){Copy-Item -LiteralPath (Join-Path $root $name) -Destination $copy.FullName -Recurse}
$storeType=$a.GetType('Server.Aetherfront.AetherfrontServerStore',$true)
$store=[Activator]::CreateInstance($storeType,@($copy.FullName,'held-world-test'))
$system=$a.GetType('Server.Aetherfront.AetherfrontServerSystem',$true)
$system.GetField('_store',$flags).SetValue($null,$store)
function Inventory {
    @(Get-ChildItem -LiteralPath $copy.FullName -File -Recurse | Sort-Object FullName | ForEach-Object {
        [IO.Path]::GetRelativePath($copy.FullName,$_.FullName)+' '+(Get-FileHash -LiteralPath $_.FullName).Hash
    }) -join "`n"
}
$before=Inventory
$instance=[Reflection.BindingFlags]'Instance,Public,NonPublic'
$ctor=$coord.GetConstructor($instance,$null,[type[]]@([string]),$null)
$native=$ctor.Invoke(@($copy.FullName))
$validate=$coord.GetMethod('ValidateRecoveredWorld',$instance)
$validate.Invoke($native,$null)
if((Inventory) -cne $before){throw 'Native startup validation mutated the retained scenario, vessel, or roster projection'}
foreach($id in $held){
    $guid=[guid]::Parse($id.ToString())
    if($coord.GetMethod('CanReplaceVessel',$instance).Invoke($native,@($guid,'','')) -or
       $coord.GetMethod('AcceptVesselRevision',$instance).Invoke($native,@($guid,[long]0))){throw 'Native guards did not protect a hold'}
}
foreach($kerbal in $crewById){
    if(!$coord.GetMethod('IsCommittedKerbal',$instance).Invoke($native,@($kerbal.KerbalName))){throw 'A held crew identity lost native protection'}
}
$victim=$crewById[0].KerbalName
$path=Join-Path $copy.FullName ('Kerbals/'+$victim+'.txt')
$text=[IO.File]::ReadAllText($path)
[IO.File]::WriteAllText($path,($text -replace '(?m)^(\s*state\s*=\s*)Dead\s*$', '${1}Available'))
$failed=$false
try {$validate.Invoke($ctor.Invoke(@($copy.FullName)),$null)}
catch {
    if ($_.Exception.GetBaseException().Message -notmatch 'mismatched frozen roster state'){throw}
    $failed=$true
}
if(!$failed){throw 'Startup accepted a mismatched held roster'}
'PASS: native startup accepted the verified hold without writes, reserved all held vessel IDs and crew, and rejected a changed roster.'
