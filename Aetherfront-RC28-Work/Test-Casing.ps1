#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$EngineResultPath,
    [string]$BaselinePath='',
    [string]$OldEngine='',
    [switch]$SkipMigration,
    [switch]$SkipMono,
    [switch]$MigrationOnly,
    [string]$Cases=''
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$testWork=$PSScriptRoot
$testWorkspace=Split-Path $testWork
$testResult=Get-Content -LiteralPath $EngineResultPath -Raw | ConvertFrom-Json
if(!$testResult.completed -or $testResult.version -cne 'kesas-rc28'){throw 'A completed RC28 Engine result is required.'}
$testEngine=[IO.Path]::GetFullPath($testResult.enginePath)
if((Get-FileHash -LiteralPath $testEngine -Algorithm SHA256).Hash -ine $testResult.engineSha256){throw 'Frozen Engine hash mismatch.'}
if(!$BaselinePath){$BaselinePath=$testResult.baselinePath}
$BaselinePath=[IO.Path]::GetFullPath($BaselinePath)
$testPlugins=Join-Path $BaselinePath 'GameData/LunaMultiplayer/Plugins'
if(!$OldEngine){$OldEngine=Join-Path $testPlugins 'KesasInstall.dll'}
$OldEngine=[IO.Path]::GetFullPath($OldEngine)
foreach($testInput in @($testEngine,$OldEngine,"$testPlugins/ApfCore.dll","$testPlugins/JsonFx.dll","$testPlugins/LmpClient.dll")){if(!(Test-Path -LiteralPath $testInput -PathType Leaf)){throw "Missing test input: $testInput"}}
$testAttempt=Join-Path $testWork ('BuildAttempts/Casing-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($testAttempt)
$testManaged=Join-Path $testWorkspace 'Aetherfront-KSP-Smoke/KSP_x64_Data/Managed'
$testCompression=Join-Path $BaselinePath 'KSP_x64_Data/Managed'
$testOutput=Join-Path $testAttempt 'bin'
$testEscape={param($value)[Security.SecurityElement]::Escape($value)}
$testTargets=@"
<Project>
<Target Name="PinFramework" BeforeTargets="GetReferenceAssemblyPaths"><PropertyGroup><_TargetFrameworkDirectories>$(& $testEscape $testManaged)</_TargetFrameworkDirectories><_FullFrameworkReferenceAssemblyPaths>$(& $testEscape $testManaged)</_FullFrameworkReferenceAssemblyPaths></PropertyGroup></Target>
<Target Name="PinReferences" BeforeTargets="ResolveAssemblyReferences"><ItemGroup>
<Reference Remove="System.IO.Compression;System.IO.Compression.FileSystem" />
<Reference Include="$(& $testEscape "$testCompression/System.IO.Compression.dll")"><HintPath>$(& $testEscape "$testCompression/System.IO.Compression.dll")</HintPath><Private>true</Private></Reference>
<Reference Include="$(& $testEscape "$testCompression/System.IO.Compression.FileSystem.dll")"><HintPath>$(& $testEscape "$testCompression/System.IO.Compression.FileSystem.dll")</HintPath><Private>true</Private></Reference>
</ItemGroup></Target>
</Project>
"@
[IO.File]::WriteAllText("$testAttempt/References.targets",$testTargets)
[IO.File]::WriteAllText("$testAttempt/NuGet.Config",'<configuration><packageSources><clear /></packageSources></configuration>')
$testPrevious=@{}
$testEnvironment=@{DOTNET_CLI_HOME="$testAttempt/cli";DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1';DOTNET_CLI_TELEMETRY_OPTOUT='1';DOTNET_NOLOGO='1';MSBUILDDISABLENODEREUSE='1';NUGET_PACKAGES="$testAttempt/packages";NUGET_HTTP_CACHE_PATH="$testAttempt/nuget-http";NUGET_PLUGINS_CACHE_PATH="$testAttempt/nuget-plugins";APF_CASING_ARGUMENTS=$null}
foreach($testName in $testEnvironment.Keys){$testPrevious[$testName]=[Environment]::GetEnvironmentVariable($testName,'Process');[Environment]::SetEnvironmentVariable($testName,$testEnvironment[$testName],'Process')}
try {
    & "$testWorkspace/tools/dotnet-sdk8/dotnet.exe" build "$testWork/CasingTests/CasingTests.csproj" -c Release --nologo -v:minimal -nodeReuse:false -p:UseSharedCompilation=false -p:MSBuildEnableWorkloadResolver=false -p:AutomaticallyUseReferenceAssemblyPackages=false "-p:RestoreConfigFile=$testAttempt/NuGet.Config" "-p:OutputPath=$testOutput/" "-p:BaseIntermediateOutputPath=$testAttempt/obj/" "-p:FrameworkPathOverride=$testManaged" "-p:TargetFrameworkRootPath=$testAttempt/frameworks" "-p:CustomAfterMicrosoftCommonTargets=$testAttempt/References.targets" "-p:TestPlugins=$testPlugins" "-p:TestEngine=$testEngine" 2>&1 | Tee-Object -FilePath "$testAttempt/build.log"
    if($LASTEXITCODE -ne 0){throw "Casing test compilation failed; retained $testAttempt"}
    $testRuns=[Collections.Generic.List[object]]::new()
    $testModes=if($Cases){@('new')}elseif($MigrationOnly){@('migration')}else{@('new','old')}
    if(!$Cases -and !$MigrationOnly -and !$SkipMigration){$testModes+=@('migration')}
    $testRuntimes=@('CLR');if(!$SkipMono){$testRuntimes+=@('KSP-Mono')}
    foreach($testRuntime in $testRuntimes){foreach($testMode in $testModes){
        $testSelectedEngine=if($testMode -eq 'new'){$testEngine}else{$OldEngine}
        $testRunOutput=Join-Path $testAttempt "$testRuntime-$testMode"
        $testArguments=@('--mode',$testMode,'--engine',$testSelectedEngine,'--new-engine',$testEngine,'--baseline',$BaselinePath,'--output',$testRunOutput,'--expected-runtime',$testRuntime)
        if($Cases){$testArguments+=@('--case',$Cases)}
        if($testRuntime -eq 'CLR'){
            & "$testOutput/CasingTests.exe" @testArguments 2>&1 | Tee-Object -FilePath "$testAttempt/$testRuntime-$testMode.log"
            $testExit=$LASTEXITCODE
        }else{
            [Environment]::SetEnvironmentVariable('APF_CASING_ARGUMENTS',($testArguments -join "`n"),'Process')
            Push-Location $testWorkspace
            try{& "$testWorkspace/Aetherfront-RC22-Work/MonoHost/bin/Release/net472/MonoHost.exe" "$testOutput/CasingTests.exe" 2>&1 | Tee-Object -FilePath "$testAttempt/$testRuntime-$testMode.log";$testExit=$LASTEXITCODE}
            finally{Pop-Location;[Environment]::SetEnvironmentVariable('APF_CASING_ARGUMENTS',$null,'Process')}
        }
        $testRuns.Add([ordered]@{runtime=$testRuntime;mode=$testMode;exitCode=$testExit;summary="$testRunOutput/summary-$testMode.json"})
    }}
    $testPassed=@($testRuns | Where-Object {$_.exitCode -ne 0}).Count -eq 0
    $testSources=@('CasingTests/Program.cs','CasingTests/CasingTests.csproj','Test-Casing.ps1') | ForEach-Object {$testSource=Join-Path $testWork $_;[ordered]@{path=$testSource;sha256=(Get-FileHash -LiteralPath $testSource -Algorithm SHA256).Hash.ToLowerInvariant()}}
    $testReport=[ordered]@{passed=$testPassed;engineResultPath=[IO.Path]::GetFullPath($EngineResultPath);engineResultSha256=(Get-FileHash -LiteralPath $EngineResultPath -Algorithm SHA256).Hash.ToLowerInvariant();enginePath=$testEngine;engineSha256=$testResult.engineSha256;oldEnginePath=$OldEngine;oldEngineSha256=(Get-FileHash -LiteralPath $OldEngine -Algorithm SHA256).Hash.ToLowerInvariant();executable="$testOutput/CasingTests.exe";runs=$testRuns;sources=$testSources;unityGameplayTested=$false;fixturesRetained=$true}
    [IO.File]::WriteAllText("$testAttempt/CASING-RESULT.json",($testReport | ConvertTo-Json -Depth 8))
    "CASING-RESULT: $testAttempt/CASING-RESULT.json"
    if(!$testPassed){throw "Casing regression failures retained at $testAttempt"}
}finally{foreach($testName in $testPrevious.Keys){[Environment]::SetEnvironmentVariable($testName,$testPrevious[$testName],'Process')}}
