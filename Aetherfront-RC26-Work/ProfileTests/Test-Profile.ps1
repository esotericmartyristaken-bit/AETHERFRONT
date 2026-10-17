#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CodeResult,
    [Parameter(Mandatory)][string]$AssetStage,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{64}$')][string]$AssetPlanSha256,
    [string]$AssetTestResult='',
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{64}$')][string]$AssetTestResultSha256,
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
    DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE='true';MSBUILDDISABLENODEREUSE='1'
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
    $executable=Join-Path $output 'ProfileTests.exe'
    $inputs=@(foreach($path in @((Join-Path $owned 'Program.cs'),(Join-Path $owned 'ProfileTests.csproj'),$PSCommandPath,$codeFile,(Join-Path $codeRoot 'Engine/KesasInstall.dll'),(Join-Path $codeRoot 'Patched/ApfCore.dll'),$assetPlanPath,$assetTestPath,$executable)){
        [pscustomobject]@{path=$path;sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()}
    })
    $inputs+=@($assetArchives)
    $proof=[ordered]@{compiled=$true;executed=$false;fullCycleRequested=$FullCycle.IsPresent;runtime=$Runtime;codeResult=$codeFile;candidate=$CandidateRoot;assetPlan=$assetPlanPath;assetPlanSha256=$AssetPlanSha256;assetTest=$assetTestPath;assetTestSha256=$AssetTestResultSha256;inputs=$inputs;exactStarterBcl=$bcl;attempt=$attempt}
    [IO.File]::WriteAllText((Join-Path $attempt 'runner-result.json'),($proof|ConvertTo-Json -Depth 8))
    if(!$Run){$proof|ConvertTo-Json -Depth 8;return}
    $arguments=@($CandidateRoot,$codeFile,$assetPlanPath,$AssetPlanSha256,$assetTestPath,$AssetTestResultSha256)
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
