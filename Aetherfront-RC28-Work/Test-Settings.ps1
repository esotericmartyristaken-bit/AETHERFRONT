#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$EngineResultPath='',
    [string]$EnginePath='',
    [string]$BaselinePath='',
    [string]$Old26Engine='',
    [string]$Old27Engine='',
    [string]$Modes='new,old27,migration26,migration27,fresh26,fresh27',
    [string]$Cases='',
    [ValidateSet('','plan-saved','file-written:0','file-recorded:0')][string]$LegacyFaultCheckpoint='',
    [switch]$SkipMono,
    [switch]$BuildOnly
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$settingsWork=$PSScriptRoot
$settingsWorkspace=Split-Path $settingsWork
if($EngineResultPath){
    $settingsFrozen=Get-Content -LiteralPath $EngineResultPath -Raw | ConvertFrom-Json
    if(!$settingsFrozen.completed -or $settingsFrozen.version -cne 'kesas-rc28'){throw 'Completed RC28 Engine result required.'}
    $EnginePath=$settingsFrozen.enginePath
    if((Get-FileHash -LiteralPath $EnginePath -Algorithm SHA256).Hash -ine $settingsFrozen.engineSha256){throw 'Frozen Engine hash mismatch.'}
    if(!$BaselinePath){$BaselinePath=$settingsFrozen.baselinePath}
}
if(!$EnginePath -or !$BaselinePath){throw 'Pass -EngineResultPath, or preliminary -EnginePath and -BaselinePath.'}
$EnginePath=[IO.Path]::GetFullPath($EnginePath)
$BaselinePath=[IO.Path]::GetFullPath($BaselinePath)
$settingsPlugins=Join-Path $BaselinePath 'GameData/LunaMultiplayer/Plugins'
$settingsAttempt=Join-Path $settingsWork ('SettingsTests/Attempts/'+[Guid]::NewGuid().ToString('N').Substring(0,12))
[void][IO.Directory]::CreateDirectory($settingsAttempt)
$settingsOutput=Join-Path $settingsAttempt 'bin'
$settingsModes=@($Modes.Split(',') | Where-Object {$_})
foreach($settingsMode in $settingsModes){if($settingsMode -notin @('new','old27','migration26','migration27','fresh26','fresh27')){throw "Unknown mode: $settingsMode"}}
if($LegacyFaultCheckpoint -and @($settingsModes | Where-Object {$_ -notin @('migration26','migration27')}).Count -ne 0){throw 'Checkpoint runs require -Modes migration26,migration27 (or one migration mode).'}
# Verify historical inputs read-only; extract only the helper into this owned test directory.
foreach($settingsVersion in @('26','27')){
    $settingsNeedsOld=if($settingsVersion -eq '26'){@($settingsModes | Where-Object {$_ -in @('migration26','fresh26')}).Count -gt 0}else{@($settingsModes | Where-Object {$_ -in @('old27','migration27','fresh27')}).Count -gt 0}
    $settingsOld=if($settingsVersion -eq '26'){$Old26Engine}else{$Old27Engine}
    if(!$BuildOnly -and $settingsNeedsOld -and !$settingsOld){
        $settingsServer=Join-Path $settingsWorkspace 'KESAS-VPS-Server'
        $settingsArchive=Join-Path $settingsServer "Updates/kesas-rc$settingsVersion/APFClient-kesas-rc$settingsVersion.zip"
        $settingsFeed=Join-Path $settingsServer "Updates/kesas-rc$settingsVersion/APFClientUpdate.json"
        & "$settingsServer/UpdateTool/ApfUpdateTool.exe" --verify --manifest $settingsFeed --archive $settingsArchive --public-key-xml "$settingsServer/secrets/update-signing-public.xml" *> "$settingsAttempt/authenticate-rc$settingsVersion.log"
        if($LASTEXITCODE -ne 0){throw "Signed historical RC$settingsVersion baseline verification failed."}
        $settingsOld=Join-Path $settingsAttempt "old$settingsVersion/KesasInstall.dll"
        [void][IO.Directory]::CreateDirectory((Split-Path $settingsOld))
        $settingsZip=[IO.Compression.ZipFile]::OpenRead($settingsArchive)
        try{
            $settingsEntry=$settingsZip.GetEntry('GameData/LunaMultiplayer/Plugins/KesasInstall.dll')
            if(!$settingsEntry){throw 'Historical helper archive entry missing.'}
            [IO.Compression.ZipFileExtensions]::ExtractToFile($settingsEntry,$settingsOld,$false)
        }finally{$settingsZip.Dispose()}
        if($settingsVersion -eq '26'){$Old26Engine=$settingsOld}else{$Old27Engine=$settingsOld}
    }
}
foreach($settingsInput in @($EnginePath,"$settingsPlugins/ApfCore.dll","$settingsPlugins/JsonFx.dll","$settingsPlugins/LmpClient.dll")){
    if(!(Test-Path -LiteralPath $settingsInput -PathType Leaf)){throw "Missing test input: $settingsInput"}
}
$settingsEngineHash=(Get-FileHash -LiteralPath $EnginePath -Algorithm SHA256).Hash.ToLowerInvariant()
$settingsSources=@('SettingsTests/Program.cs','SettingsTests/SettingsTests.csproj','Test-Settings.ps1') | ForEach-Object {
    $settingsSource=Join-Path $settingsWork $_
    [ordered]@{path=$settingsSource;sha256=(Get-FileHash -LiteralPath $settingsSource -Algorithm SHA256).Hash.ToLowerInvariant()}
}
$settingsManaged=Join-Path $settingsWorkspace 'Aetherfront-KSP-Smoke/KSP_x64_Data/Managed'
$settingsCompression=Join-Path $BaselinePath 'KSP_x64_Data/Managed'
$settingsEscape={param($value)[Security.SecurityElement]::Escape($value)}
$settingsTargets=@"
<Project>
<Target Name="PinFramework" BeforeTargets="GetReferenceAssemblyPaths"><PropertyGroup><_TargetFrameworkDirectories>$(& $settingsEscape $settingsManaged)</_TargetFrameworkDirectories><_FullFrameworkReferenceAssemblyPaths>$(& $settingsEscape $settingsManaged)</_FullFrameworkReferenceAssemblyPaths></PropertyGroup></Target>
<Target Name="PinReferences" BeforeTargets="ResolveAssemblyReferences"><ItemGroup>
<Reference Remove="System.IO.Compression;System.IO.Compression.FileSystem" />
<Reference Include="$(& $settingsEscape "$settingsCompression/System.IO.Compression.dll")"><HintPath>$(& $settingsEscape "$settingsCompression/System.IO.Compression.dll")</HintPath><Private>true</Private></Reference>
<Reference Include="$(& $settingsEscape "$settingsCompression/System.IO.Compression.FileSystem.dll")"><HintPath>$(& $settingsEscape "$settingsCompression/System.IO.Compression.FileSystem.dll")</HintPath><Private>true</Private></Reference>
</ItemGroup></Target>
</Project>
"@
[IO.File]::WriteAllText("$settingsAttempt/References.targets",$settingsTargets)
[IO.File]::WriteAllText("$settingsAttempt/NuGet.Config",'<configuration><packageSources><clear /></packageSources></configuration>')
$settingsPrevious=@{}
$settingsEnvironment=@{DOTNET_CLI_HOME="$settingsAttempt/cli";DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1';DOTNET_CLI_TELEMETRY_OPTOUT='1';DOTNET_NOLOGO='1';MSBUILDDISABLENODEREUSE='1';NUGET_PACKAGES="$settingsAttempt/packages";NUGET_HTTP_CACHE_PATH="$settingsAttempt/nuget-http";NUGET_PLUGINS_CACHE_PATH="$settingsAttempt/nuget-plugins";APF_SETTINGS_ARGUMENTS=$null}
foreach($settingsName in $settingsEnvironment.Keys){$settingsPrevious[$settingsName]=[Environment]::GetEnvironmentVariable($settingsName,'Process');[Environment]::SetEnvironmentVariable($settingsName,$settingsEnvironment[$settingsName],'Process')}
try{
    & "$settingsWorkspace/tools/dotnet-sdk8/dotnet.exe" build "$settingsWork/SettingsTests/SettingsTests.csproj" -c Release --nologo -v:minimal -nodeReuse:false -p:UseSharedCompilation=false -p:MSBuildEnableWorkloadResolver=false -p:AutomaticallyUseReferenceAssemblyPackages=false "-p:RestoreConfigFile=$settingsAttempt/NuGet.Config" "-p:OutputPath=$settingsOutput/" "-p:BaseIntermediateOutputPath=$settingsAttempt/obj/" "-p:FrameworkPathOverride=$settingsManaged" "-p:TargetFrameworkRootPath=$settingsAttempt/frameworks" "-p:CustomAfterMicrosoftCommonTargets=$settingsAttempt/References.targets" "-p:TestPlugins=$settingsPlugins" "-p:TestEngine=$EnginePath" *> "$settingsAttempt/build.log"
    if($LASTEXITCODE -ne 0){Get-Content -LiteralPath "$settingsAttempt/build.log" -Tail 18;throw "Settings test compilation failed: $settingsAttempt"}
    $settingsRuns=[Collections.Generic.List[object]]::new()
    if(!$BuildOnly){
        $settingsRuntimes=@('CLR');if(!$SkipMono){$settingsRuntimes+=@('KSP-Mono')}
        foreach($settingsRuntime in $settingsRuntimes){foreach($settingsMode in $settingsModes){
            if((Get-FileHash -LiteralPath $EnginePath -Algorithm SHA256).Hash -ine $settingsEngineHash){throw 'Selected Engine changed during tests.'}
            $settingsSelected=if($settingsMode -eq 'new'){$EnginePath}elseif($settingsMode -in @('migration26','fresh26')){$Old26Engine}else{$Old27Engine}
            if(!$settingsSelected -or !(Test-Path -LiteralPath $settingsSelected -PathType Leaf)){throw "Missing historical helper for $settingsMode"}
            $settingsRunOutput=Join-Path $settingsAttempt "$settingsRuntime-$settingsMode"
            $settingsArguments=@('--mode',$settingsMode,'--engine',$settingsSelected,'--new-engine',$EnginePath,'--baseline',$BaselinePath,'--output',$settingsRunOutput,'--expected-runtime',$settingsRuntime)
            if($Cases){$settingsArguments+=@('--case',$Cases)}
            if($LegacyFaultCheckpoint){$settingsArguments+=@('--legacy-fault-checkpoint',$LegacyFaultCheckpoint)}
            if($settingsRuntime -eq 'CLR'){
                & "$settingsOutput/SettingsTests.exe" @settingsArguments *> "$settingsAttempt/$settingsRuntime-$settingsMode.log"
                $settingsExit=$LASTEXITCODE
            }else{
                [Environment]::SetEnvironmentVariable('APF_SETTINGS_ARGUMENTS',($settingsArguments -join "`n"),'Process')
                Push-Location $settingsWorkspace
                try{& "$settingsWorkspace/Aetherfront-RC22-Work/MonoHost/bin/Release/net472/MonoHost.exe" "$settingsOutput/SettingsTests.exe" *> "$settingsAttempt/$settingsRuntime-$settingsMode.log";$settingsExit=$LASTEXITCODE}
                finally{Pop-Location;[Environment]::SetEnvironmentVariable('APF_SETTINGS_ARGUMENTS',$null,'Process')}
            }
            $settingsRuns.Add([ordered]@{runtime=$settingsRuntime;mode=$settingsMode;legacyFaultCheckpoint=$LegacyFaultCheckpoint;exitCode=$settingsExit;enginePath=$settingsSelected;engineSha256=(Get-FileHash -LiteralPath $settingsSelected -Algorithm SHA256).Hash.ToLowerInvariant();summary="$settingsRunOutput/summary.json"})
            "Settings $settingsRuntime $settingsMode exit=$settingsExit"
        }}
    }
    $settingsPassed=!$BuildOnly -and $settingsRuns.Count -gt 0 -and @($settingsRuns | Where-Object {$_.exitCode -ne 0}).Count -eq 0
    $settingsReport=[ordered]@{passed=$settingsPassed;compiled=$true;buildOnly=[bool]$BuildOnly;preliminary=[string]::IsNullOrEmpty($EngineResultPath);enginePath=$EnginePath;engineSha256=$settingsEngineHash;engineResultPath=$EngineResultPath;baselinePath=$BaselinePath;executable="$settingsOutput/SettingsTests.exe";runs=$settingsRuns;sources=$settingsSources;unityGameplayTested=$false;fixturesRetained=$true}
    [IO.File]::WriteAllText("$settingsAttempt/SETTINGS-RESULT.json",($settingsReport | ConvertTo-Json -Depth 9))
    "SETTINGS-RESULT: $settingsAttempt/SETTINGS-RESULT.json"
    if(!$BuildOnly -and !$settingsPassed){throw 'Settings regression failures; private logs retained locally.'}
}finally{foreach($settingsName in $settingsPrevious.Keys){[Environment]::SetEnvironmentVariable($settingsName,$settingsPrevious[$settingsName],'Process')}}
