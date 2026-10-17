#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CodeResult,
    [Parameter(Mandatory)][string]$AssetStage,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{64}$')][string]$AssetPlanSha256,
    [string]$AssetTestResult='',
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{64}$')][string]$AssetTestResultSha256,
    [string]$PotatoPolicyPlanPath='',
    [ValidatePattern('^(?:[a-f0-9]{64})?$')][string]$PotatoPolicyPlanSha256='',
    [string]$CandidateRoot='',
    [switch]$Run,
    [switch]$FullCycle,
    [ValidateSet('CLR','KSP-Mono')][string]$Runtime='CLR'
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$owned=[IO.Path]::GetFullPath($PSScriptRoot)
$work=Split-Path $owned
$workspace=Split-Path $work
if($owned -ine [IO.Path]::GetFullPath((Join-Path $workspace 'Aetherfront-RC26-Work/ProfileTests'))){throw 'Run only from the owned RC26 ProfileTests directory.'}
if($FullCycle -and !$Run){throw 'FullCycle requires explicit Run.'}
if($Run -and !$CandidateRoot){throw 'A frozen signed CandidateRoot is required; no fixture or test will be launched without it.'}
if($Run -and (!$PotatoPolicyPlanPath -or !$PotatoPolicyPlanSha256)){throw 'Run requires explicit -PotatoPolicyPlanPath and -PotatoPolicyPlanSha256; no old-candidate fallback.'}
$codeFile=[IO.Path]::GetFullPath($CodeResult)
$codeRoot=Split-Path $codeFile
if(!$codeRoot.StartsWith([IO.Path]::GetFullPath((Join-Path $work 'BuildAttempts/Code-')),[StringComparison]::OrdinalIgnoreCase)){throw 'CodeResult must be an immutable RC26 Code attempt.'}
$code=Get-Content -LiteralPath $codeFile -Raw|ConvertFrom-Json
$expectedEngine='be8a27a5f9a568fa938d98fa4ba4ad9b5e6f1f5a56772e58510a6773828741b0'
$expectedCore='0a4b6bd7c25ee4e18aba88ec8d41cb66b8c0034da3ba02d2d090ddb798bdc6c0'
if($code.engineSha256 -ieq 'bfafcbbb46e3ef92fe88c8a488fe618295d38981597e118397e9fff1ce4cbc26'){throw 'Retired bfaf candidate is forbidden after the child-junction security finding.'}
if($expectedEngine -cnotmatch '^[a-f0-9]{64}$'){throw 'Await the reviewed new CODE-RESULT and Engine pin before compilation or execution.'}
if(!$code.completed -or !$code.engineBuilt -or !$code.coreScanGrafted -or $code.version -cne 'kesas-rc26' -or $code.engineSha256 -ine $expectedEngine){throw 'Code result is not the agreed frozen RC26 code.'}
foreach($file in $code.outputs){
    $path=[IO.Path]::GetFullPath((Join-Path $codeRoot $file.path))
    if(!$path.StartsWith($codeRoot+'\',[StringComparison]::OrdinalIgnoreCase) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine $file.sha256){throw 'Frozen code output hash mismatch.'}
}
if((Get-FileHash -LiteralPath (Join-Path $codeRoot 'Patched/ApfCore.dll') -Algorithm SHA256).Hash -ine $expectedCore){throw 'Expected the scanner-grafted RC26 core.'}
if(![IO.Path]::IsPathRooted($AssetStage)){throw 'AssetStage must be an absolute stage directory or AssetPlan manifest path.'}
$assetPlanPath=[IO.Path]::GetFullPath($AssetStage)
if(Test-Path -LiteralPath $assetPlanPath -PathType Container){$assetPlanPath=[IO.Path]::GetFullPath((Join-Path $assetPlanPath 'ASSET-PLAN.json'))}
$assetDirectory=Split-Path $assetPlanPath
$assetRoot=[IO.Path]::GetFullPath((Join-Path $work 'Assets'))
if(!$assetPlanPath.StartsWith($assetRoot+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'AssetStage must stay in the owned RC26 Assets directory.'}
if(!$AssetTestResult){$AssetTestResult=Join-Path $assetDirectory 'ASSET-TEST-RESULT.json'}
if(![IO.Path]::IsPathRooted($AssetTestResult)){throw 'AssetTestResult must be absolute.'}
$assetTestPath=[IO.Path]::GetFullPath($AssetTestResult)
if(!$assetTestPath.StartsWith($assetRoot+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Asset test evidence must stay in the owned RC26 Assets directory.'}
if((Get-FileHash -LiteralPath $assetPlanPath -Algorithm SHA256).Hash -ine $AssetPlanSha256 -or (Get-FileHash -LiteralPath $assetTestPath -Algorithm SHA256).Hash -ine $AssetTestResultSha256){throw 'Asset plan/test evidence changed from the explicitly reviewed hashes.'}
$assetPlan=Get-Content -LiteralPath $assetPlanPath -Raw|ConvertFrom-Json
$assetTest=Get-Content -LiteralPath $assetTestPath -Raw|ConvertFrom-Json
$allowedAssetIds=@('kesas-modularlaunchpads-8feabdb7','kesas-umbraspaceindustries-2ed9115b','kesas-tundraspacecenter-7018b381','kesas-interstellartechnologies-173d8c64','kesas-rc25-modfixes')
$actualAssetIds=@($assetPlan.media|ForEach-Object{$_.declaration.id})
if($assetPlan.schemaVersion -ne 1 -or $assetPlan.version -cne 'kesas-rc26' -or @($assetPlan.media).Count -ne 5 -or $assetPlan.signed -or $assetPlan.published -or $assetPlan.installed -or !$assetPlan.requiresReleaseOwnerSignature -or @($actualAssetIds|Select-Object -Unique).Count -ne 5 -or @($actualAssetIds|Where-Object{$_ -cnotin $allowedAssetIds}).Count -ne 0){throw 'Only the five explicitly reviewed additive asset declarations are permitted.'}
if(!$assetTest.passed -or $assetTest.planSha256 -cne $AssetPlanSha256 -or $assetTest.sourceCatalogSha256 -cne $assetPlan.sourceCatalogSha256 -or $assetTest.packagesVerified -ne 5 -or $assetTest.aliasesVerified -ne 7 -or $assetTest.preciseBindingsVerified -ne 8 -or !$assetTest.originalZipPrefixesPreserved -or !$assetTest.originalCentralDirectoryRecordsPreserved -or !$assetTest.bothProfilesUseIdenticalSharedVariants -or !$assetTest.untouchedPackagesPreserved){throw 'Complete hash-bound passing TestAssets original-entry preservation proof is required.'}
$assetArchives=@(foreach($item in $assetPlan.media){
    if([IO.Path]::GetFileName($item.filename) -cne $item.filename){throw 'Asset filename is not a leaf filename.'}
    $path=[IO.Path]::GetFullPath((Join-Path $assetDirectory ('Media/'+$item.filename)))
    if(!$path.StartsWith($assetDirectory+'\',[StringComparison]::OrdinalIgnoreCase) -or (Get-Item -LiteralPath $path).Length -ne $item.declaration.sizeBytes -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine $item.declaration.sha256){throw 'Reviewed AssetStage archive hash/size mismatch.'}
    [pscustomobject]@{path=$path;sha256=$item.declaration.sha256}
})
$policyInputs=@()
$policyPlanFile=''
if($PotatoPolicyPlanPath -or $PotatoPolicyPlanSha256){
    if(!$PotatoPolicyPlanPath -or !$PotatoPolicyPlanSha256 -or ![IO.Path]::IsPathRooted($PotatoPolicyPlanPath)){throw 'Policy plan requires both an explicit absolute path and reviewed SHA256.'}
    $policyPlanFile=[IO.Path]::GetFullPath($PotatoPolicyPlanPath)
    $policyDirectory=Split-Path $policyPlanFile
    $modFixes=[IO.Path]::GetFullPath((Join-Path $work 'ModFixes'))
    if((Split-Path $policyDirectory) -ine $modFixes -or [IO.Path]::GetFileName($policyDirectory) -cnotmatch '^Staged-RC26(?:-[A-Za-z0-9-]+)?$'){throw 'Policy plan must be in an explicitly reviewed immutable ModFixes/Staged-RC26 stage.'}
    if((Get-FileHash -LiteralPath $policyPlanFile -Algorithm SHA256).Hash -ine $PotatoPolicyPlanSha256){throw 'Policy plan differs from its explicitly approved SHA256.'}
    $policyPlan=Get-Content -LiteralPath $policyPlanFile -Raw|ConvertFrom-Json
    if($policyPlan.schemaVersion -ne 1 -or $policyPlan.version -cne 'kesas-rc26' -or $policyPlan.profile -cne 'potato' -or $policyPlan.sourceCatalogSha256 -cne $assetPlan.sourceCatalogSha256 -or $policyPlan.declaration.id -cne 'kesas-potato-policy' -or $policyPlan.oldDeclaration.id -cne 'kesas-potato-policy'){throw 'Policy plan must bind the same signed RC25 catalog and the sole Potato-only policy package.'}
    if($policyPlan.archive.sha256 -cne $policyPlan.declaration.sha256 -or $policyPlan.archive.sizeBytes -ne $policyPlan.declaration.sizeBytes -or $policyPlan.source.sha256 -cne $policyPlan.changedEntry.sha256 -or $policyPlan.source.sizeBytes -ne $policyPlan.changedEntry.sizeBytes -or $policyPlan.changedEntry.path -cne 'ZZZ_KESASPotato/DisableExhaust.cfg'){throw 'Policy source/archive/changed-entry byte records disagree.'}
    $policyInputs=@([pscustomobject]@{path=$policyPlanFile;sha256=$PotatoPolicyPlanSha256})
    foreach($name in @('archive','source','testEvidence')){
        $record=$policyPlan.$name
        if($record.path -cmatch '\\' -or [IO.Path]::IsPathRooted($record.path) -or @($record.path -split '/'|Where-Object{$_ -in @('','.','..')}).Count -ne 0 -or $record.sha256 -cnotmatch '^[a-f0-9]{64}$'){throw 'Unsafe or unbound policy input path/hash.'}
        $path=[IO.Path]::GetFullPath((Join-Path $policyDirectory $record.path))
        if(!$path.StartsWith($policyDirectory+'\',[StringComparison]::OrdinalIgnoreCase) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine $record.sha256){throw 'Policy input escaped its stage or changed from the reviewed bytes.'}
        $probe=$path
        while($probe.Length -ge $work.Length){if(((Get-Item -LiteralPath $probe -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw 'Policy input has a reparse-point ancestor.'};if($probe -ieq $work){break};$probe=Split-Path $probe}
        if($name -ne 'testEvidence' -and (Get-Item -LiteralPath $path).Length -ne $record.sizeBytes){throw 'Policy input size mismatch.'}
        $policyInputs+=[pscustomobject]@{path=$path;sha256=$record.sha256}
    }
    if(@($policyInputs.path|Sort-Object -Unique).Count -ne 4){throw 'Policy plan/source/archive/evidence paths must be distinct.'}
    $policyEvidencePath=[IO.Path]::GetFullPath((Join-Path $policyDirectory $policyPlan.testEvidence.path))
    $policyEvidence=Get-Content -LiteralPath $policyEvidencePath -Raw|ConvertFrom-Json
    if($policyEvidence.schemaVersion -ne 1 -or $policyEvidence.version -cne 'kesas-rc26' -or $policyEvidence.profile -cne 'potato' -or !$policyEvidence.passed -or !$policyEvidence.unchangedOtherEntries -or !$policyEvidence.fullProfileUnchanged -or $policyEvidence.sourceCatalogSha256 -cne $policyPlan.sourceCatalogSha256 -or $policyEvidence.oldArchiveSha256 -cne $policyPlan.oldDeclaration.sha256 -or $policyEvidence.newArchiveSha256 -cne $policyPlan.declaration.sha256 -or $policyEvidence.sourceSha256 -cne $policyPlan.source.sha256 -or ('fixturesOnly' -in $policyEvidence.PSObject.Properties.Name -and $policyEvidence.fixturesOnly)){throw 'Passing non-synthetic policy/MM evidence must bind exact source, baseline/new archive and unchanged Full.'}
}
if($CandidateRoot){
    if(![IO.Path]::IsPathRooted($CandidateRoot)){throw 'CandidateRoot must be absolute.'}
    $CandidateRoot=[IO.Path]::GetFullPath($CandidateRoot)
    if(!$CandidateRoot.StartsWith($work+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'CandidateRoot must stay in RC26 work.'}
    foreach($relative in @('Published/APFClientUpdate.json','Published/APFClient-kesas-rc26.zip','Client/GameData/LunaMultiplayer/Launcher/install-catalog.json')){
        if(!(Test-Path -LiteralPath (Join-Path $CandidateRoot $relative) -PathType Leaf)){throw "Frozen candidate is incomplete: $relative"}
    }
}
$attempt=Join-Path $owned ('Runs/'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8))
[IO.Directory]::CreateDirectory($attempt)|Out-Null
$output=Join-Path $attempt 'bin'
$managed=Join-Path $workspace 'Aetherfront-KSP-Smoke/KSP_x64_Data/Managed'
$sdk=Join-Path $workspace 'tools/dotnet-sdk8/dotnet.exe'
$environment=@{
    DOTNET_CLI_HOME=(Join-Path $attempt 'cli-home');NUGET_PACKAGES=(Join-Path $attempt 'packages');
    NUGET_HTTP_CACHE_PATH=(Join-Path $attempt 'http-cache');DOTNET_CLI_TELEMETRY_OPTOUT='1';
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1';DOTNET_CLI_USE_MSBUILD_SERVER='0';
    DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE='true';MSBUILDDISABLENODEREUSE='1';
    DOTNET_GENERATE_ASPNET_CERTIFICATE='false';DOTNET_ADD_GLOBAL_TOOLS_TO_PATH='0';DOTNET_NOLOGO='1'
}
$previousEnvironment=@{}
foreach($name in $environment.Keys){$previousEnvironment[$name]=[Environment]::GetEnvironmentVariable($name,'Process');[Environment]::SetEnvironmentVariable($name,$environment[$name],'Process')}
try{
    & $sdk build (Join-Path $owned 'ProfileTests.csproj') -c Release --nologo -v:minimal -nodeReuse:false `
        -p:UseSharedCompilation=false -p:AutomaticallyUseReferenceAssemblyPackages=false -p:MSBuildEnableWorkloadResolver=false `
        "-p:Rc26CodeRoot=$codeRoot" "-p:GameManaged=$managed" "-p:FrameworkPathOverride=$managed" `
        "-p:TargetFrameworkRootPath=$attempt/frameworks" "-p:RestoreConfigFile=$owned/NuGet.Config" `
        "-p:OutputPath=$output/" "-p:BaseIntermediateOutputPath=$attempt/obj/" 2>&1 |
        Tee-Object -FilePath (Join-Path $attempt 'build.log')
    if($LASTEXITCODE -ne 0){throw 'Profile harness compilation failed.'}
    $baseline=Join-Path $workspace 'Aetherfront-RC25-Work/Candidate/Client/KSP_x64_Data/Managed'
    $bcl=@(foreach($name in @('System.IO.Compression.dll','System.IO.Compression.FileSystem.dll','System.Numerics.dll')){
        $source=Join-Path $baseline $name;$target=Join-Path $output $name
        if(!(Test-Path -LiteralPath $target)){Copy-Item -LiteralPath $source -Destination $target}
        $sourceHash=(Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
        $targetHash=(Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
        if($targetHash -cne $sourceHash){throw "Harness must use exact starter BCL: $name"}
        [pscustomobject]@{name=$name;source=$source;sha256=$sourceHash}
    })
    $runtimeAssemblies=@(foreach($record in @(@{name='KesasInstall.dll';source=(Join-Path $codeRoot 'Engine/KesasInstall.dll');sha256=$expectedEngine},@{name='ApfCore.dll';source=(Join-Path $codeRoot 'Patched/ApfCore.dll');sha256=$expectedCore})){
        $target=Join-Path $output $record.name
        $actual=(Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
        if($actual -cne $record.sha256){throw "Compiled harness copied the wrong runtime assembly: $($record.name)"}
        [pscustomobject]@{name=$record.name;source=$record.source;target=$target;sha256=$actual}
    })
    $executable=Join-Path $output 'ProfileTests.exe'
    $inputs=@(foreach($path in @((Join-Path $owned 'Program.cs'),(Join-Path $owned 'PotatoPolicyContract.cs'),(Join-Path $owned 'ProfileTests.csproj'),$PSCommandPath,$codeFile,(Join-Path $codeRoot 'Engine/KesasInstall.dll'),(Join-Path $codeRoot 'Patched/ApfCore.dll'),$assetPlanPath,$assetTestPath,$executable)){
        [pscustomobject]@{path=$path;sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()}
    })
    $inputs+=@($assetArchives)
    $inputs+=@($policyInputs)
    $proof=[ordered]@{compiled=$true;executed=$false;fullCycleRequested=$FullCycle.IsPresent;runtime=$Runtime;codeResult=$codeFile;candidate=$CandidateRoot;assetPlan=$assetPlanPath;assetPlanSha256=$AssetPlanSha256;assetTest=$assetTestPath;assetTestSha256=$AssetTestResultSha256;potatoPolicyPlan=$policyPlanFile;potatoPolicyPlanSha256=$PotatoPolicyPlanSha256;policyRequiredForExecution=$true;inputs=$inputs;exactStarterBcl=$bcl;runtimeAssemblies=$runtimeAssemblies;attempt=$attempt}
    [IO.File]::WriteAllText((Join-Path $attempt 'runner-result.json'),($proof|ConvertTo-Json -Depth 8))
    if(!$Run){$proof|ConvertTo-Json -Depth 8;return}
    $arguments=@($CandidateRoot,$codeFile,$assetPlanPath,$AssetPlanSha256,$assetTestPath,$AssetTestResultSha256,$policyPlanFile,$PotatoPolicyPlanSha256)
    if($FullCycle){$arguments+='--chain'}
    Push-Location $workspace
    try{
        if($Runtime -ceq 'KSP-Mono'){
            & (Join-Path $workspace 'Aetherfront-RC22-Work/MonoHost/bin/Release/net472/MonoHost.exe') $executable @arguments 2>&1 | Tee-Object -FilePath (Join-Path $attempt 'test.log')
        }else{& $executable @arguments 2>&1 | Tee-Object -FilePath (Join-Path $attempt 'test.log')}
        $exitCode=$LASTEXITCODE
    }finally{Pop-Location}
    $proof.executed=$true;$proof.exitCode=$exitCode
    $proof.sourceInputsUnchanged=@($inputs|Where-Object{(Get-FileHash -LiteralPath $_.path -Algorithm SHA256).Hash -ine $_.sha256}).Count -eq 0
    [IO.File]::WriteAllText((Join-Path $attempt 'runner-result.json'),($proof|ConvertTo-Json -Depth 8))
    if($exitCode -ne 0 -or !$proof.sourceInputsUnchanged){throw 'Profile migration failed or pinned inputs changed; evidence retained.'}
}finally{
    foreach($name in $previousEnvironment.Keys){[Environment]::SetEnvironmentVariable($name,$previousEnvironment[$name],'Process')}
}
