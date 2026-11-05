#requires -Version 7.4
param([Parameter(Mandatory)][string]$EvidenceRoot)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. "$repo/KESAS-VPS-Server/Update.Common.ps1"
. "$repo/Aetherfront-RC44-Work/Deployment/Invoke-ScienceCompensation.ps1"
Add-Type -Path "$repo/Aetherfront-RC44-Work/Deployment/ScienceCompensation.cs"
$id='key-'+('1'*64);$other='key-'+('2'*64)
$plan=@{schemaVersion=1;applicationVersion='kesas-rc48';serverId='fixture-science';transactionId='fixture-942';accountId=$id;playerName='Fixture';amount=942}|ConvertTo-Json -Compress
$bindings=@{version=1;accounts=@(@{accountId=$id;playerNames=@('Fixture')})}|ConvertTo-Json -Depth 8 -Compress
$fixture=@{ServerId='fixture-science';Revision=100;StoreCommitSequence=200;Accounts=@(@{AccountId=$id;DisplayName='Fixture';Suspended=$false});PersonalProgression=@{Version=1;TotalEarnedScience=300;Players=@(@{AccountId=$id;Science=[decimal]'42.0016105';TradableScience=40;EarnedScience=100;SharedScienceCredited=100;NextCommandSequence=29;ScienceSources=@(@{Id='source';CumulativeScience=100});TechnologyUnlocks=@('start')},@{AccountId=$other;Science=14;EarnedScience=200})};UnknownPreserved=@{Value=[decimal]'1.1234567890123456789012345678'}}|ConvertTo-Json -Depth 20 -Compress
$script:checks=0
function Check([bool]$Ok,[string]$Message){if(-not $Ok){throw $Message};$script:checks++}
function Reject([scriptblock]$Action,[string]$Message){$rejected=$false;try{& $Action|Out-Null}catch{$rejected=$true};Check $rejected $Message}
function Build([string]$State=$fixture,[string]$Binding=$bindings,[string]$Intent=$plan){[ScienceCompensation]::Build($State,$Binding,$Intent,'kesas-rc48')}
$next=Build|ConvertFrom-Json
Check ($next.PersonalProgression.Players[0].Science -eq [decimal]'984.0016105') 'Exact 942 credit missing'
Check ($next.Revision -eq 101 -and $next.StoreCommitSequence -eq 201) 'Durable counters missing'
Check ($next.PersonalProgression.Players[0].TradableScience -eq 40 -and $next.PersonalProgression.TotalEarnedScience -eq 300 -and $next.PersonalProgression.Players[0].EarnedScience -eq 100) 'Compensation became earned or tradable science'
foreach($pair in @(@('fixture-science','wrong-world'),@('"Revision":100','"Revision":-1'),@('"StoreCommitSequence":200','"StoreCommitSequence":9223372036854775807'),@('"Science":42.0016105','"Science":-1'),@('"SharedScienceCredited":100','"SharedScienceCredited":101'),@('"Suspended":false','"Suspended":true'),@('"Fixture"','"Impostor"'))){$bad=$fixture.Replace($pair[0],$pair[1]);Reject {Build -State $bad} "Invalid state accepted: $($pair[0])"}
Reject {Build -State $fixture.Insert(1,'"revision":100,')} 'Duplicate state field accepted'
Reject {Build -Binding $bindings.Replace($id,$other)} 'Wrong identity accepted'
Reject {Build -Intent $plan.Replace('942','10001')} 'Excessive grant accepted'
Reject {Build -Intent $plan.Replace('942','0')} 'Zero grant accepted'
Reject {Build -Intent $plan.Replace('kesas-rc48','kesas-rc47')} 'Wrong release accepted'
Reject {Build -Intent $plan.Insert(1,'"extra":1,')} 'Unknown plan field accepted'
Reject {Build -Intent $plan.Insert(1,'"Amount":942,')} 'Duplicate plan field accepted'
$root=[IO.Path]::GetFullPath($EvidenceRoot)
if(-not $root.StartsWith([IO.Path]::GetFullPath((Join-Path $repo '../UpdateBuilds/RC48-20261105/')),[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $root)){throw 'Use a fresh private RC48 fixture directory'}
New-UpdateDirectory "$root/Server/Aetherfront";New-UpdateDirectory "$root/Server/Universe"
Write-UpdateAtomic "$root/Server/Aetherfront/state.json" $fixture
Write-UpdateAtomic "$root/Server/Universe/IdentityBindings.json" $bindings
Write-UpdateAtomic "$root/plan.json" $plan
$sha=(Get-FileHash -LiteralPath "$root/plan.json").Hash.ToLowerInvariant()
Invoke-ScienceCompensation $root "$root/plan.json" $sha 'kesas-rc48' -ValidateOnly
Check ((Read-UpdateText "$root/Server/Aetherfront/state.json") -ceq $fixture) 'Preflight mutated state'
Reject {Invoke-ScienceCompensation $root "$root/plan.json" ('0'*64) 'kesas-rc48'} 'Wrong hash accepted'
Invoke-ScienceCompensation $root "$root/plan.json" $sha 'kesas-rc48'
$first=Read-UpdateText "$root/Server/Aetherfront/state.json"
Invoke-ScienceCompensation $root "$root/plan.json" $sha 'kesas-rc48'
Check ((Read-UpdateText "$root/Server/Aetherfront/state.json") -ceq $first) 'Replay doubled credit'
$receipt="$root/Server/Aetherfront/ScienceCompensations/fixture-942.json"
Check ((Read-UpdateText $receipt|ConvertFrom-Json).phase -ceq 'applied') 'Missing completion receipt'
# Simulate the existing publisher's whole-world rollback: restored state and
# receipt tree together permit a new activation to grant exactly once.
Move-Item -LiteralPath "$root/Server/Aetherfront" -Destination "$root/failed-Aetherfront"
New-UpdateDirectory "$root/Server/Aetherfront";Write-UpdateAtomic "$root/Server/Aetherfront/state.json" $fixture
Invoke-ScienceCompensation $root "$root/plan.json" $sha 'kesas-rc48'
Check ((Read-UpdateText "$root/Server/Aetherfront/state.json") -ceq $first) 'Rollback retry did not reproduce one credit'
Write-UpdateAtomic $receipt ((Read-UpdateText $receipt).Replace('"applied"','"prepared"'))
Reject {Invoke-ScienceCompensation $root "$root/plan.json" $sha 'kesas-rc48'} 'Ambiguous receipt replayed'
Write-UpdateAtomic "$root/RESULT.json" (@{passed=$checks;failed=0;scope='pure plan and private file transaction; no production services'}|ConvertTo-Json)
"PASS compensation $checks checks"
