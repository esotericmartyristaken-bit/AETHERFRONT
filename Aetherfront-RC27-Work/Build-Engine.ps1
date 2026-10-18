#requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$work=$PSScriptRoot
$workspace=Split-Path $work
$server=Join-Path $workspace 'KESAS-VPS-Server'
$feedPath=Join-Path $server 'Updates/APFClientUpdate.json'
$archive=Join-Path $server 'Updates/kesas-rc26/APFClient-kesas-rc26.zip'
$feed=Get-Content -LiteralPath $feedPath -Raw|ConvertFrom-Json
if($feed.version -cne 'kesas-rc26' -or @($feed.files).Count -ne 464){throw 'Expected authenticated RC26 baseline.'}
& "$server/UpdateTool/ApfUpdateTool.exe" --verify --manifest $feedPath --archive $archive --public-key-xml "$server/secrets/update-signing-public.xml"
if($LASTEXITCODE -ne 0){throw 'RC26 signature verification failed.'}
$attempt=Join-Path $work ('BuildAttempts/Engine-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $attempt|Out-Null
Expand-Archive -LiteralPath $archive -DestinationPath "$attempt/Baseline"
$plugins="$attempt/Baseline/GameData/LunaMultiplayer/Plugins"
$managed="$workspace/Aetherfront-KSP-Smoke/KSP_x64_Data/Managed"
$compression="$attempt/Baseline/KSP_x64_Data/Managed"
$dotnet="$workspace/tools/dotnet-sdk8/dotnet.exe"
function Frozen-Sources {
    $paths=@(Get-ChildItem -LiteralPath "$work/Engine" -File|Select-Object -ExpandProperty FullName)+@("$workspace/Aetherfront-RC22-Work/Engine/Transfer.cs","$workspace/Aetherfront-RC22-Work/Engine/DownloadFiles.cs","$workspace/Aetherfront-RC22-Work/Engine/GitHub.cs")
    foreach($sourcePath in $paths){[ordered]@{path=[IO.Path]::GetFullPath($sourcePath);sha256=(Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash.ToLowerInvariant()}}
}
$sources=@(Frozen-Sources)
$escape={param($value)[Security.SecurityElement]::Escape($value)}
$targets=@"
<Project>
<Target Name="PinFramework" BeforeTargets="GetReferenceAssemblyPaths"><PropertyGroup><_TargetFrameworkDirectories>$(& $escape $managed)</_TargetFrameworkDirectories><_FullFrameworkReferenceAssemblyPaths>$(& $escape $managed)</_FullFrameworkReferenceAssemblyPaths></PropertyGroup></Target>
<Target Name="PinReferences" BeforeTargets="ResolveAssemblyReferences"><ItemGroup>
<Reference Condition="'%(Reference.Identity)' == 'ApfCore'"><HintPath>$(& $escape "$plugins/ApfCore.dll")</HintPath><Private>false</Private></Reference>
<Reference Remove="System.IO.Compression;System.IO.Compression.FileSystem" />
<Reference Include="$(& $escape "$compression/System.IO.Compression.dll")"><HintPath>$(& $escape "$compression/System.IO.Compression.dll")</HintPath><SpecificVersion>false</SpecificVersion><Private>false</Private></Reference>
<Reference Include="$(& $escape "$compression/System.IO.Compression.FileSystem.dll")"><HintPath>$(& $escape "$compression/System.IO.Compression.FileSystem.dll")</HintPath><SpecificVersion>false</SpecificVersion><Private>false</Private></Reference>
</ItemGroup></Target>
</Project>
"@
[IO.File]::WriteAllText("$attempt/References.targets",$targets)
[IO.File]::WriteAllText("$attempt/NuGet.Config",'<configuration><packageSources><clear /></packageSources></configuration>')
$previous=@{}
$environment=@{DOTNET_CLI_HOME="$attempt/cli";DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1';DOTNET_CLI_TELEMETRY_OPTOUT='1';DOTNET_NOLOGO='1';MSBUILDDISABLENODEREUSE='1';NUGET_PACKAGES="$attempt/packages";NUGET_HTTP_CACHE_PATH="$attempt/nuget-http";NUGET_PLUGINS_CACHE_PATH="$attempt/nuget-plugins"}
foreach($name in $environment.Keys){$previous[$name]=[Environment]::GetEnvironmentVariable($name,'Process');[Environment]::SetEnvironmentVariable($name,$environment[$name],'Process')}
try{
    & $dotnet build "$work/Engine/Engine.csproj" -c Release --nologo -v:minimal -nodeReuse:false -p:UseSharedCompilation=false -p:MSBuildEnableWorkloadResolver=false -p:AutomaticallyUseReferenceAssemblyPackages=false "-p:RestoreConfigFile=$attempt/NuGet.Config" "-p:OutputPath=$attempt/Engine/" "-p:BaseIntermediateOutputPath=$attempt/obj/" "-p:FrameworkPathOverride=$managed" "-p:TargetFrameworkRootPath=$attempt/frameworks" "-p:CustomAfterMicrosoftCommonTargets=$attempt/References.targets"
    if($LASTEXITCODE -ne 0){throw 'RC27 Engine build failed.'}
    $after=@(Frozen-Sources)
    if(($sources|ConvertTo-Json -Compress) -cne ($after|ConvertTo-Json -Compress)){throw 'Engine sources changed during build.'}
    $result=[ordered]@{schemaVersion=1;version='kesas-rc27';completed=$true;enginePath="$attempt/Engine/KesasInstall.dll";engineSha256=(Get-FileHash -LiteralPath "$attempt/Engine/KesasInstall.dll" -Algorithm SHA256).Hash.ToLowerInvariant();baselinePath="$attempt/Baseline";baselineFeedSha256=(Get-FileHash -LiteralPath $feedPath -Algorithm SHA256).Hash.ToLowerInvariant();baselineArchiveSha256=(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant();sources=$sources}
    [IO.File]::WriteAllText("$attempt/ENGINE-RESULT.json",($result|ConvertTo-Json -Depth 10))
    "ENGINE-RESULT: $attempt/ENGINE-RESULT.json"
}finally{foreach($name in $previous.Keys){[Environment]::SetEnvironmentVariable($name,$previous[$name],'Process')}}
