#requires -Version 7.4
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$repository=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
. (Join-Path $repository 'KESAS-VPS-Server/Update.Common.ps1')
foreach($relative in @('KESAS-VPS-Server/Publish-Release.ps1','APFDiscordBot/scripts/Verify-KesasRelease.ps1')){
    $errors=$null
    $ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $repository $relative),[ref]$null,[ref]$errors)
    if($errors.Count){throw 'Reset source parse failed'}
    foreach($definition in $ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst]},$false)){
        if($definition.Name -in @('Get-PublisherAccountResetIntent','Initialize-PublisherAccountReset','Invoke-PublisherAccountReset','Require-ReleaseAccountReset')){
            . ([scriptblock]::Create($definition.Extent.Text))
        }
    }
}
Initialize-PublisherAccountReset
$noEnrollment=[System.Management.Automation.Language.NullString]::Value
$rsa=[Security.Cryptography.RSA]::Create(2048)
try{$parameters=$rsa.ExportParameters($false);$publicKey=[Convert]::ToBase64String($parameters.Modulus)+'.'+[Convert]::ToBase64String($parameters.Exponent)}finally{$rsa.Dispose()}
$accountId='key-'+[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($publicKey))).ToLowerInvariant()
$name='ResetFixture'
$retired='Retired-'+$accountId.Substring(4,16)
$intent=@{schemaVersion=1;applicationVersion='kesas-rc41';playerName=$name;expectedAccountId=$accountId;preserveAccountData=$true}|ConvertTo-Json -Compress
$binding=@{version=1;accounts=@(@{accountId=$accountId;publicKey=$publicKey;playerNames=@($name)},@{accountId='other-account';publicKey='untouched-public-fixture';playerNames=@('Other')})}|ConvertTo-Json -Depth 10 -Compress
$world=@{Revision=123;Accounts=@(@{AccountId=$accountId;DisplayName=$name;Suspended=$true;ReceivedFoundingGrant=$true},@{AccountId='other-account';DisplayName='Other'});PersonalProgression=@{Version=1;StartingBalancesConfigured=$true;StartingFundsMicro=1000;StartingReputation=0;Players=@(@{AccountId=$accountId;Science=37;EarnedScience=71;SharedScienceCredited=5;TradableScience=12;FundsMicro=9007199254740993;Reputation=4;TechnologyUnlocks=@('start','earned-tech');ScienceSources=@(@{Source='earned';Value=71});NextCommandSequence=42})};Factions=@(@{Name='Unrelated faction';Members=@(@{AccountId='other-account';DisplayName='Other'})});Trades=@(@{AccountId=$accountId});Loans=@(@{AccountId=$accountId});Crafts=@(1..7|ForEach-Object {@{Id=$_;CreatorAccountId=$accountId;OwnerAccountId=$accountId;Name=$name}});Objectives=@(@{AccountId=$accountId});Events=@(@{ActorAccountId=$accountId;Message=('Historical '+$name)});Precision='NUMERIC_TOKEN'}|ConvertTo-Json -Depth 20 -Compress
$world=$world.Replace('"NUMERIC_TOKEN"','0.1234567890123456789012345678')
$world=$world.Substring(0,$world.Length-1)+',"LargeInteger":9007199254740993,"SmallDecimal":1.234567890123456789e-12}'
$script:checks=0
function Check([bool]$Condition,[string]$Message){$script:checks++;if(-not $Condition){throw $Message}}
function Reject([scriptblock]$Action,[string]$Message){$rejected=$false;try{& $Action|Out-Null}catch{$rejected=$true};Check $rejected $Message}
$parsed=Get-PublisherAccountResetIntent $intent 'kesas-rc41'
Check ($parsed.expectedAccountId -ceq $accountId) 'Valid native intent rejected'
$plan=[ApfAccountResetPlan]::Build($binding,$world,$noEnrollment,$name,$accountId)
Check ($plan[0] -notmatch [regex]::Escape($accountId)) 'Binding not removed'
Check ($plan[1].Contains($accountId)) 'Preserved account removed'
Check ($plan[0] -ceq [Text.Json.Nodes.JsonNode]::Parse($binding).ToJsonString().Replace([Text.Json.Nodes.JsonNode]::Parse($binding)['accounts'][0].ToJsonString()+',','')) 'Unrelated binding changed'
$expected=$world.Replace('"DisplayName":"'+$name+'"','"DisplayName":"'+$retired+'"').Replace('"Revision":123','"Revision":124')
Check ($plan[1] -ceq [Text.Json.Nodes.JsonNode]::Parse($expected).ToJsonString()) 'World changed beyond target DisplayName and Revision, including raw numeric tokens'
$beforeNode=[Text.Json.Nodes.JsonNode]::Parse($world);$afterNode=[Text.Json.Nodes.JsonNode]::Parse($plan[1])
Check ($beforeNode['PersonalProgression'].ToJsonString() -ceq $afterNode['PersonalProgression'].ToJsonString()) 'Personal ledger changed'
Check ($afterNode['Crafts'].Count -eq 7 -and $beforeNode['Crafts'].ToJsonString() -ceq $afterNode['Crafts'].ToJsonString()) 'Seven craft ownership records changed'
Check ($beforeNode['Events'].ToJsonString() -ceq $afterNode['Events'].ToJsonString()) 'Historical event references changed'
Check ($plan[1].Contains('0.1234567890123456789012345678') -and $plan[1].Contains('9007199254740993') -and $plan[1].Contains('1.234567890123456789e-12')) 'Unrelated numeric precision changed'
Check (($plan[1]|ConvertFrom-Json).Revision -eq 124) 'Revision not incremented once'
$aliased=$binding.Replace('"playerNames":["'+$name+'"]','"playerNames":["'+$name+'","Alias"]')
Reject {[ApfAccountResetPlan]::Build($aliased,$world,$noEnrollment,$name,$accountId)} 'Aliases accepted'
Reject {[ApfAccountResetPlan]::Build($binding.Replace($publicKey,'changed-key'),$world,$noEnrollment,$name,$accountId)} 'Changed key accepted'
Reject {[ApfAccountResetPlan]::Build($binding,$world,$binding,$name,$accountId)} 'Enrollment conflict accepted'
foreach($collision in @($retired,$retired.ToUpperInvariant(),$name.ToLowerInvariant())){
    $changed=$world.Replace('"DisplayName":"Other"','"DisplayName":"'+$collision+'"')
    Reject {[ApfAccountResetPlan]::Build($binding,$changed,$noEnrollment,$name,$accountId)} 'World or faction name collision accepted'
    $changed=$binding.Replace('"playerNames":["Other"]','"playerNames":["'+$collision+'"]')
    Reject {[ApfAccountResetPlan]::Build($changed,$world,$noEnrollment,$name,$accountId)} 'Binding collision accepted'
}
$changed=$world.Replace('"Name":"Unrelated faction"','"Name":"Unrelated faction","DisplayName":"'+$retired+'"')
Reject {[ApfAccountResetPlan]::Build($binding,$changed,$noEnrollment,$name,$accountId)} 'Faction DisplayName collision accepted'
foreach($memberId in @($accountId,'other-account')){
    $changed=$world.Replace('"Members":[','"Members":[{"AccountId":"'+$memberId+'","DisplayName":"'+$name+'"},')
    Reject {[ApfAccountResetPlan]::Build($binding,$changed,$noEnrollment,$name,$accountId)} 'Faction member reservation accepted'
}
$changed=$world.Replace('"Members":[','"Members":[{"AccountId":"other-account","DisplayName":"'+$retired.ToUpperInvariant()+'"},')
Reject {[ApfAccountResetPlan]::Build($binding,$changed,$noEnrollment,$name,$accountId)} 'Faction member retired-name collision accepted'
$enrollment=$binding.Replace($name,'Unrelated').Replace($accountId,'enrollment-other').Replace('"Other"','"'+$retired+'"')
Reject {[ApfAccountResetPlan]::Build($binding,$world,$enrollment,$name,$accountId)} 'Retired-name enrollment conflict accepted'
Reject {[ApfAccountResetPlan]::Build($binding,$world,$noEnrollment,$name.ToLowerInvariant(),$accountId)} 'Case alias accepted'
Reject {[ApfAccountResetPlan]::Build($binding,$world,$noEnrollment,$name,$accountId.Replace('key-','KEY-'))} 'Account alias accepted'
Reject {[ApfAccountResetPlan]::Build($binding,$world.Replace('"Revision":123','"Revision":9223372036854775807'),$noEnrollment,$name,$accountId)} 'Revision overflow accepted'
foreach($bad in @($intent.Replace('"schemaVersion":1','"schemaVersion":1.0'),$intent.Replace('"preserveAccountData":true','"preserveAccountData":false'),$intent.Replace('preserveAccountData','requireZeroProgress'),$intent.Replace('kesas-rc41','kesas-rc40'),$intent.Replace($name,'../escape'),$intent.Substring(0,$intent.Length-1)+',"path":"arbitrary"}',$intent.Substring(0,$intent.Length-1)+',"playerName":"duplicate"}')){
    Reject {Get-PublisherAccountResetIntent $bad 'kesas-rc41'} 'Malformed manifest accepted'
}
$temporaryBase=[IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$fixture=Join-Path $temporaryBase ('apf-account-reset-'+[Guid]::NewGuid().ToString('N'))
try{
    New-UpdateDirectory "$fixture/Server/Universe/Scenarios"
    New-UpdateDirectory "$fixture/Server/Universe/Crafts"
    New-UpdateDirectory "$fixture/Server/Aetherfront"
    New-UpdateDirectory "$fixture/Server/Config"
    Write-UpdateAtomic "$fixture/Server/Universe/IdentityBindings.json" $binding
    Write-UpdateAtomic "$fixture/Server/Aetherfront/state.json" $world
    $beforeWorld=(Get-FileHash -LiteralPath "$fixture/Server/Aetherfront/state.json").Hash
    Invoke-PublisherAccountReset $fixture $intent 'kesas-rc41' -ValidateOnly
    Check ((Get-FileHash -LiteralPath "$fixture/Server/Aetherfront/state.json").Hash -ceq $beforeWorld) 'ValidateOnly wrote state'
    $script:originalRead=(Get-Command Read-UpdateText).ScriptBlock
    $script:watchedWorld=[IO.Path]::GetFullPath("$fixture/Server/Aetherfront/state.json");$script:worldReads=0
    function Read-UpdateText([string]$Path,[long]$MaximumBytes=1MB){
        $text=& $script:originalRead $Path $MaximumBytes
        if([IO.Path]::GetFullPath($Path) -ceq $script:watchedWorld){$script:worldReads++;if($script:worldReads -gt 1){return $text.Replace('"Revision":123','"Revision":124')}}
        return $text
    }
    try{
        Invoke-PublisherAccountReset $fixture $intent 'kesas-rc41' -ValidateOnly
        Check ($script:worldReads -eq 1) 'Online validation reread a live world snapshot'
        $script:worldReads=0
        Reject {Invoke-PublisherAccountReset $fixture $intent 'kesas-rc41'} 'Offline world snapshot change accepted'
        Check ((Get-FileHash -LiteralPath "$fixture/Server/Aetherfront/state.json").Hash -ceq $beforeWorld) 'Snapshot rejection wrote state'
    }finally{Set-Item -LiteralPath Function:Read-UpdateText -Value $script:originalRead}
    Write-UpdateAtomic "$fixture/Server/Universe/Scenarios/scenario.txt" ('owner = '+$name)
    Reject {Invoke-PublisherAccountReset $fixture $intent 'kesas-rc41'} 'Scenario reference accepted'
    Check ((Get-FileHash -LiteralPath "$fixture/Server/Aetherfront/state.json").Hash -ceq $beforeWorld) 'Rejected reset wrote state'
    [IO.File]::Delete("$fixture/Server/Universe/Scenarios/scenario.txt")
    New-UpdateDirectory "$fixture/Server/Universe/Scenarios/$retired"
    Reject {Invoke-PublisherAccountReset $fixture $intent 'kesas-rc41' -ValidateOnly} 'Online validation accepted retired-name scenario directory'
    Reject {Invoke-PublisherAccountReset $fixture $intent 'kesas-rc41'} 'Reset accepted retired-name scenario directory'
    Check ((Get-FileHash -LiteralPath "$fixture/Server/Aetherfront/state.json").Hash -ceq $beforeWorld) 'Retired-name scenario rejection wrote state'
    [IO.Directory]::Delete("$fixture/Server/Universe/Scenarios/$retired")
    New-UpdateDirectory "$fixture/Server/Universe/Crafts/$name"
    Reject {Invoke-PublisherAccountReset $fixture $intent 'kesas-rc41'} 'Craft directory accepted'
    [IO.Directory]::Delete("$fixture/Server/Universe/Crafts/$name")
    Invoke-PublisherAccountReset $fixture $intent 'kesas-rc41'
    Check ((Read-UpdateText "$fixture/Server/Aetherfront/state.json") -ceq $plan[1]) 'Fixture world commit differs from pure plan'
    Check ((Read-UpdateText "$fixture/Server/Universe/IdentityBindings.json") -ceq $plan[0]) 'Fixture binding commit differs from pure plan'
    Reject {Invoke-PublisherAccountReset $fixture $intent 'kesas-rc41'} 'Reset replay accepted'
    # Both independent schema validators must agree; the verifier is never invoked as a publisher.
    $zipPath=Join-Path $fixture 'intent-fixture.zip'
    $zip=[IO.Compression.ZipFile]::Open($zipPath,[IO.Compression.ZipArchiveMode]::Create)
    try{foreach($pair in @{valid=$intent;invalid=$intent.Replace('"preserveAccountData":true','"preserveAccountData":"true"');legacy=$intent.Replace('preserveAccountData','requireZeroProgress')}.GetEnumerator()){$writer=[IO.StreamWriter]::new($zip.CreateEntry($pair.Key).Open());try{$writer.Write($pair.Value)}finally{$writer.Dispose()}}}finally{$zip.Dispose()}
    $zip=[IO.Compression.ZipFile]::OpenRead($zipPath)
    try{Require-ReleaseAccountReset ($zip.GetEntry('valid')) 'kesas-rc41';$script:checks++;Reject {Require-ReleaseAccountReset ($zip.GetEntry('invalid')) 'kesas-rc41'} 'Verifier accepted a string boolean';Reject {Require-ReleaseAccountReset ($zip.GetEntry('legacy')) 'kesas-rc41'} 'Verifier accepted zero-progress reset schema'}finally{$zip.Dispose()}
}finally{
    $resolved=[IO.Path]::GetFullPath($fixture)
    if(-not $resolved.StartsWith($temporaryBase.TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -cnotmatch '^apf-account-reset-[a-f0-9]{32}$'){throw 'Unsafe fixture cleanup target'}
    if([IO.Directory]::Exists($resolved)){Remove-Item -LiteralPath $resolved -Recurse -Force}
}
Write-Output "PASS $script:checks isolated account-reset checks; no production, authentication, publication or service operation performed."
