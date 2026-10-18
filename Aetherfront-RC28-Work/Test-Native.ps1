#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$EngineResultPath,[switch]$KspMono)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$work=$PSScriptRoot
$workspace=Split-Path $work
$server=Join-Path $workspace 'KESAS-VPS-Server'
$frozen=Get-Content -LiteralPath $EngineResultPath -Raw|ConvertFrom-Json
if(!$frozen.completed -or $frozen.version -cne 'kesas-rc28' -or (Get-FileHash -LiteralPath $frozen.enginePath -Algorithm SHA256).Hash -ine $frozen.engineSha256){throw 'Frozen RC28 Engine mismatch.'}
$baseline=$frozen.baselinePath
$feed=Get-Content -LiteralPath "$server/Updates/APFClientUpdate.json" -Raw|ConvertFrom-Json
if($feed.version -cne 'kesas-rc27'){throw 'Expected authenticated RC27 active baseline.'}
& "$server/UpdateTool/ApfUpdateTool.exe" --verify --manifest "$server/Updates/APFClientUpdate.json" --archive "$server/Updates/kesas-rc27/APFClient-kesas-rc27.zip" --public-key-xml "$server/secrets/update-signing-public.xml"
if($LASTEXITCODE-ne 0){throw 'Signed RC27 baseline failed verification.'}
$plugins="$baseline/GameData/LunaMultiplayer/Plugins"
$managed=Join-Path $workspace 'Aetherfront-KSP-Smoke/KSP_x64_Data/Managed'
$attempt=Join-Path $work ('BuildAttempts/Native-'+[Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($attempt)|Out-Null
$output=Join-Path $attempt 'bin'
$references=$plugins;$engine=$frozen.enginePath
foreach($name in @('ApfCore.dll','KesasInstall.dll','JsonFx.dll')){
    $record=@($feed.files|Where-Object path -CEQ "GameData/LunaMultiplayer/Plugins/$name")
    if($record.Count-ne 1-or(Get-FileHash -LiteralPath "$plugins/$name" -Algorithm SHA256).Hash -ine $record[0].sha256){throw "Baseline input mismatch: $name"}
}
$escapedManaged=[Security.SecurityElement]::Escape($managed)
$escapedCore=[Security.SecurityElement]::Escape("$references/ApfCore.dll")
$escapedEngine=[Security.SecurityElement]::Escape($engine)
$escapedJson=[Security.SecurityElement]::Escape("$plugins/JsonFx.dll")
$escapedCompression=[Security.SecurityElement]::Escape("$baseline/KSP_x64_Data/Managed")
$targets=Join-Path $attempt 'References.targets'
[IO.File]::WriteAllText($targets,@"
<Project>
  <Target Name="PinFramework" BeforeTargets="GetReferenceAssemblyPaths"><PropertyGroup><_TargetFrameworkDirectories>$escapedManaged</_TargetFrameworkDirectories><_FullFrameworkReferenceAssemblyPaths>$escapedManaged</_FullFrameworkReferenceAssemblyPaths></PropertyGroup></Target>
  <Target Name="PinReferences" BeforeTargets="ResolveAssemblyReferences"><ItemGroup>
    <Reference Condition="'%(Reference.Identity)' == 'ApfCore'"><HintPath>$escapedCore</HintPath></Reference>
    <Reference Condition="'%(Reference.Identity)' == 'KesasInstall'"><HintPath>$escapedEngine</HintPath></Reference>
    <Reference Condition="'%(Reference.Identity)' == 'JsonFx'"><HintPath>$escapedJson</HintPath></Reference>
    <Reference Remove="System.IO.Compression;System.IO.Compression.FileSystem" />
    <Reference Include="$escapedCompression/System.IO.Compression.dll"><HintPath>$escapedCompression/System.IO.Compression.dll</HintPath><Private>true</Private></Reference>
    <Reference Include="$escapedCompression/System.IO.Compression.FileSystem.dll"><HintPath>$escapedCompression/System.IO.Compression.FileSystem.dll</HintPath><Private>true</Private></Reference>
  </ItemGroup></Target>
</Project>
"@)
$nuget=Join-Path $attempt 'NuGet.Config'
[IO.File]::WriteAllText($nuget,'<configuration><packageSources><clear /></packageSources></configuration>')
& "$workspace/tools/dotnet-sdk8/dotnet.exe" build "$work/NativeTests/NativeTests.csproj" -c Release --nologo -v:minimal -nodeReuse:false -p:UseSharedCompilation=false -p:AutomaticallyUseReferenceAssemblyPackages=false -p:MSBuildEnableWorkloadResolver=false "-p:CustomAfterMicrosoftCommonTargets=$targets" "-p:RestoreConfigFile=$nuget" "-p:FrameworkPathOverride=$managed" "-p:TargetFrameworkRootPath=$attempt/frameworks" "-p:OutputPath=$output/" "-p:BaseIntermediateOutputPath=$attempt/obj/"
if($LASTEXITCODE-ne 0){throw 'Native regression compilation failed.'}
Push-Location $workspace
try{
    if((Get-FileHash -LiteralPath "$output/KesasInstall.dll" -Algorithm SHA256).Hash -ine $frozen.engineSha256){throw 'Test runtime did not copy the frozen Engine.'}
    & "$output/NativeTests.exe"
    if($LASTEXITCODE-ne 0){throw 'Native CLR tests failed.'}
    if($KspMono){
        & "$workspace/Aetherfront-RC22-Work/MonoHost/bin/Release/net472/MonoHost.exe" "$output/NativeTests.exe"
        if($LASTEXITCODE-ne 0){throw 'Actual KSP Mono native tests failed.'}
    }
    [pscustomobject]@{passed=$true;executable="$output/NativeTests.exe";unityGameplayTested=$false}|ConvertTo-Json -Compress
}finally{Pop-Location}
