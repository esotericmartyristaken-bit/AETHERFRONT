#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('VersionOnly','All')][string]$Mode = 'VersionOnly',
    [switch]$EngineFrozen,
    [string]$UnityManaged = (Join-Path $PSScriptRoot '../Aetherfront-KSP-Smoke/KSP_x64_Data/Managed')
)
. "$PSScriptRoot/VersionPatcher/Review.Common.ps1"
if ($Mode -ceq 'All' -and !$EngineFrozen) { throw 'Main must freeze Engine edits before using -Mode All -EngineFrozen.' }
$baseline = Get-RC26Baseline
$dotnet = Assert-UpdatePath "$RC26Workspace/tools/dotnet-sdk8/dotnet.exe"
$cecil = Assert-UpdatePath "$RC26Workspace/tools/dotnet-sdk6/sdk/6.0.428/Sdks/Microsoft.NET.ILLink.Tasks/tools/net6.0/Mono.Cecil.dll"
foreach ($path in @($dotnet,$cecil)) { if (![IO.File]::Exists($path)) { throw "Missing tooling: $path" } }
$UnityManaged = Assert-UpdatePath $UnityManaged
if ($Mode -ceq 'All') {
    foreach ($name in @('mscorlib.dll','System.dll','System.Core.dll','Assembly-CSharp.dll','UnityEngine.CoreModule.dll','UnityEngine.IMGUIModule.dll')) {
        if (![IO.File]::Exists((Get-UpdateChildPath $UnityManaged $name))) { throw "Missing game Mono reference: $name" }
    }
}
$sourceFiles = @(if ($Mode -ceq 'All') { Get-RC26EngineSources })
$patcherSources = @(foreach ($path in @('VersionPatcher/Program.cs','VersionPatcher/VersionPatcher.csproj')) { [ordered]@{ path = Get-RC26Output $path; sha256 = Get-RC26Hash (Get-RC26Output $path) } })
$attempt = New-RC26Attempt 'Code'
$stage = $attempt.pending
$environment = @{
    DOTNET_CLI_HOME = "$stage/cli"; DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'; DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'; DOTNET_NOLOGO = '1'; MSBUILDDISABLENODEREUSE = '1'
    NUGET_PACKAGES = "$stage/packages"; NUGET_HTTP_CACHE_PATH = "$stage/nuget-http"; NUGET_PLUGINS_CACHE_PATH = "$stage/nuget-plugins"
    TEMP = "$stage/temp"; TMP = "$stage/temp"
    APF_RC26_UNITY_MANAGED = $UnityManaged
}
$previous = @{}
foreach ($name in $environment.Keys) { $previous[$name] = [Environment]::GetEnvironmentVariable($name,'Process'); [Environment]::SetEnvironmentVariable($name,$environment[$name],'Process') }
try {
    New-UpdateDirectory "$stage/temp"
    Expand-UpdateArchive $baseline.archive "$stage/Baseline"
    Assert-RC26TreeInventory "$stage/Baseline" $baseline.feed.files
    $plugins = Get-UpdateChildPath $stage 'Baseline/GameData/LunaMultiplayer/Plugins'
    $compression = Get-UpdateChildPath $stage 'Baseline/KSP_x64_Data/Managed'
    foreach ($name in @('System.IO.Compression','System.IO.Compression.FileSystem')) {
        $identity = [Reflection.AssemblyName]::GetAssemblyName("$compression/$name.dll")
        if ($identity.Name -cne $name -or $identity.Version.ToString() -cne '4.0.0.0') { throw 'Shipped compression identity mismatch.' }
    }
    $nuget = "$stage/NuGet.Config"
    [IO.File]::WriteAllText($nuget,'<configuration><packageSources><clear /></packageSources></configuration>',$RC26Utf8)
    $common = @('-c','Release','--nologo','-v:minimal','-nodeReuse:false','-p:UseSharedCompilation=false',
        '-p:MSBuildEnableWorkloadResolver=false',"-p:RestoreConfigFile=$nuget")
    & $dotnet build (Get-RC26Output 'VersionPatcher/VersionPatcher.csproj') @common "-p:OutputPath=$stage/Tool/" "-p:BaseIntermediateOutputPath=$stage/obj-tool/"
    if ($LASTEXITCODE -ne 0) { throw 'VersionPatcher compilation failed.' }
    $tool = "$stage/Tool/VersionPatcher.dll"
    $patchArguments = @($baseline.feedPath,$baseline.archive,"$RC26Root/secrets/update-signing-public.xml","$RC26Root/UpdateTool/ApfUpdateTool.exe","$plugins/ApfCore.dll","$plugins/LmpClient.dll","$stage/Patched")
    $engineRelative = $null; $engineHash = $null
    if ($Mode -ceq 'All') {
        $targets = "$stage/RC26.References.targets"
        $escapedManaged = [Security.SecurityElement]::Escape($UnityManaged)
        $escapedPlugins = [Security.SecurityElement]::Escape($plugins)
        $escapedCompression = [Security.SecurityElement]::Escape($compression)
        # Override the inherited RC23 HintPath without changing main-owned csproj.
        [IO.File]::WriteAllText($targets,@"
<Project>
  <Target Name="PinRC26FrameworkReferences" BeforeTargets="GetReferenceAssemblyPaths">
    <PropertyGroup><_TargetFrameworkDirectories>$escapedManaged</_TargetFrameworkDirectories><_FullFrameworkReferenceAssemblyPaths>$escapedManaged</_FullFrameworkReferenceAssemblyPaths></PropertyGroup>
  </Target>
  <Target Name="PinRC26BaselineReferences" BeforeTargets="ResolveAssemblyReferences">
    <ItemGroup>
      <Reference Condition="'%(Reference.Identity)' == 'ApfCore'"><HintPath>$escapedPlugins/ApfCore.dll</HintPath><Private>false</Private></Reference>
      <Reference Remove="System.IO.Compression;System.IO.Compression.FileSystem" />
      <Reference Include="$escapedCompression/System.IO.Compression.dll"><HintPath>$escapedCompression/System.IO.Compression.dll</HintPath><SpecificVersion>false</SpecificVersion><Private>false</Private></Reference>
      <Reference Include="$escapedCompression/System.IO.Compression.FileSystem.dll"><HintPath>$escapedCompression/System.IO.Compression.FileSystem.dll</HintPath><SpecificVersion>false</SpecificVersion><Private>false</Private></Reference>
    </ItemGroup>
  </Target>
</Project>
"@,$RC26Utf8)
        & $dotnet build (Get-RC26Output 'Engine/Engine.csproj') @common '-p:AutomaticallyUseReferenceAssemblyPackages=false' `
            "-p:OutputPath=$stage/Engine/" "-p:BaseIntermediateOutputPath=$stage/obj-engine/" "-p:FrameworkPathOverride=$UnityManaged" `
            "-p:TargetFrameworkRootPath=$stage/frameworks" "-p:CustomAfterMicrosoftCommonTargets=$targets"
        if ($LASTEXITCODE -ne 0) { throw 'Frozen Engine compilation failed.' }
        Add-Type -Path $cecil
        $oldEngine = [Mono.Cecil.AssemblyDefinition]::ReadAssembly("$plugins/KesasInstall.dll")
        $newEngine = [Mono.Cecil.AssemblyDefinition]::ReadAssembly("$stage/Engine/KesasInstall.dll")
        try {
            if ($newEngine.Name.FullName -cne $oldEngine.Name.FullName) { throw 'Engine assembly identity/API compatibility changed.' }
            foreach ($reference in $newEngine.MainModule.AssemblyReferences | Where-Object Name -In @('System.IO.Compression','System.IO.Compression.FileSystem')) {
                if ($reference.Version.ToString() -cne '4.0.0.0') { throw 'Engine compression references must remain 4.0.0.0.' }
            }
            $newMethods = @($newEngine.MainModule.Types | ForEach-Object { $_.Methods } | ForEach-Object FullName)
            foreach ($method in $oldEngine.MainModule.Types | ForEach-Object { $_.Methods } | Where-Object IsPublic) {
                if ($method.FullName -cnotin $newMethods) { throw "Engine removed a baseline public method: $($method.FullName)" }
            }
        } finally { $oldEngine.Dispose(); $newEngine.Dispose() }
        $engineRelative = 'Engine/KesasInstall.dll'; $engineHash = Get-RC26Hash "$stage/$engineRelative"
    }
    $patchMode = if ($Mode -ceq 'All') { 'patch' } else { 'patch-client' }
    $reviewMode = if ($Mode -ceq 'All') { 'review' } else { 'review-client' }
    if ($Mode -ceq 'All') { $patchArguments += "$stage/$engineRelative" }
    & $dotnet $tool $patchMode @patchArguments
    if ($LASTEXITCODE -ne 0) { throw 'Authenticated targeted graft failed.' }
    & $dotnet $tool $reviewMode @patchArguments
    if ($LASTEXITCODE -ne 0) { throw 'Independent graft review failed.' }
    $null = Get-RC26Baseline
    if ($Mode -ceq 'All' -and (ConvertTo-RC26Json @(Get-RC26EngineSources)) -cne (ConvertTo-RC26Json $sourceFiles)) { throw 'Engine/linked sources changed during build; preserve this incomplete attempt.' }
    foreach ($inputFile in $patcherSources) { Assert-RC26Hash $inputFile.path $inputFile.sha256 }
    $outputs = @(foreach ($relative in @('Patched/ApfCore.dll','Patched/LmpClient.dll','Patched/audit.json') + @($engineRelative | Where-Object { $_ })) {
        [ordered]@{ path = $relative; sha256 = Get-RC26Hash "$stage/$relative" }
    })
    $toolFiles = @(foreach ($file in Get-ChildItem -LiteralPath "$stage/Tool" -File | Sort-Object Name) { [ordered]@{ path = "Tool/$($file.Name)"; sha256 = Get-RC26Hash $file.FullName } })
    $report = [ordered]@{ schemaVersion = 1; version = 'kesas-rc26'; baseVersion = 'kesas-rc25'; mode = $Mode; completed = $true
        baselineFeedSha256 = Get-RC26Hash $baseline.feedPath; signedBaselineFiles = 464; outputs = $outputs; toolFiles = $toolFiles
        engineBuilt = ($Mode -ceq 'All'); enginePath = $engineRelative; engineSha256 = $engineHash; engineSources = $sourceFiles
        patcherSources = $patcherSources; unityManaged = $UnityManaged; compressionAssemblyVersion = '4.0.0.0'; coreModified = ($Mode -ceq 'All')
        notActivated = $true; liveWrites = $false; historicalCoreGraftsReapplied = $false; coreScanGrafted = ($Mode -ceq 'All') }
    Write-RC26Json "$stage/CODE-RESULT.json" $report
    Complete-RC26Attempt $attempt
    Write-Output "CODE-RESULT: $($attempt.complete)/CODE-RESULT.json"
} finally {
    foreach ($name in $environment.Keys) { [Environment]::SetEnvironmentVariable($name,$previous[$name],'Process') }
}
