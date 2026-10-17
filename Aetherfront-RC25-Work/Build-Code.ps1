#requires -Version 7.0
[CmdletBinding()]
param([switch]$EngineOnly)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$work = [IO.Path]::GetFullPath($PSScriptRoot)
$workspace = [IO.Path]::GetDirectoryName($work)
$root = Join-Path $workspace 'KESAS-VPS-Server'
. "$root/Update.Common.ps1"
$utf8 = [Text.UTF8Encoding]::new($false)
$plugins = Get-UpdateChildPath $workspace 'Aetherfront-RC24-Work/Candidate/Client/GameData/LunaMultiplayer/Plugins'
$baselineClientRoot = Get-UpdateChildPath $workspace 'Aetherfront-RC24-Work/Candidate/Client'
$compressionDirectory = Get-UpdateChildPath $baselineClientRoot 'KSP_x64_Data/Managed'
$managed = Get-UpdateChildPath $workspace 'Aetherfront-KSP-Smoke/KSP_x64_Data/Managed'
$dotnet = Get-UpdateChildPath $workspace 'tools/dotnet-sdk8/dotnet.exe'
$feedPath = Get-UpdateChildPath $root 'Updates/APFClientUpdate.json'
$feedHash = (Get-FileHash -LiteralPath $feedPath -Algorithm SHA256).Hash
$feed = Read-UpdateText $feedPath | ConvertFrom-Json
if ($feed.version -cne 'kesas-rc24') { throw 'Expected the active signed RC24 baseline.' }
Invoke-UpdateTool $root @('--verify', '--manifest', $feedPath, '--archive', "$root/Updates/kesas-rc24/APFClient-kesas-rc24.zip", '--public-key-xml', "$root/secrets/update-signing-public.xml")
$pins = @{
    'ApfCore.dll' = '3b6a0a7959d249c3228d70bcb5946fa42f38f0dc301feda3b8a64bc9bba256ef'
    'LmpClient.dll' = '8fba4ca5c78c89399bcf5e0aaba470e157873ea20d107b21e27e6fe90f4672f6'
    'System.IO.Compression.dll' = '7dc81ec9d4b64e8ca46c12fe4cb9ba6db8ffb31f48d2c52a9bd0ca308cd24cd5'
    'System.IO.Compression.FileSystem.dll' = '92f68856c67851f598949498f074e9a2086704d22e2e5029deea05eb454a63d1'
}
function Assert-CodeBaseline {
    if ((Get-FileHash -LiteralPath $feedPath -Algorithm SHA256).Hash -cne $feedHash) { throw 'Active baseline changed during compilation.' }
    foreach ($name in $pins.Keys) {
        $relative = if ($name.StartsWith('System.IO.Compression', [StringComparison]::Ordinal)) { "KSP_x64_Data/Managed/$name" } else { "GameData/LunaMultiplayer/Plugins/$name" }
        $record = @($feed.files | Where-Object path -CEQ $relative)
        $path = Get-UpdateChildPath $baselineClientRoot $relative
        if ($record.Count -ne 1 -or $record[0].sha256 -ine $pins[$name] -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine $pins[$name]) {
            throw "Unauthenticated or unreviewed RC24 reference: $name"
        }
    }
}
Assert-CodeBaseline
foreach ($name in @('System.IO.Compression', 'System.IO.Compression.FileSystem')) {
    $identity = [Reflection.AssemblyName]::GetAssemblyName((Get-UpdateChildPath $compressionDirectory "$name.dll"))
    if ($identity.Name -cne $name -or $identity.Version.ToString() -cne '4.0.0.0') { throw "Unexpected shipped compression identity: $name" }
}
foreach ($path in @($dotnet, "$managed/mscorlib.dll", "$managed/System.dll", "$managed/System.Core.dll",
    "$managed/Assembly-CSharp.dll", "$managed/UnityEngine.CoreModule.dll", "$managed/UnityEngine.IMGUIModule.dll",
    "$workspace/tools/dotnet-sdk6/sdk/6.0.428/Sdks/Microsoft.NET.ILLink.Tasks/tools/net6.0/Mono.Cecil.dll")) {
    $null = Assert-UpdatePath $path
    if (![IO.File]::Exists($path)) { throw "Missing build reference: $path" }
}
foreach ($directory in @('Engine', 'ClientUi', 'CorePatcher', 'ClientPatcher')) { Assert-UpdateTree "$work/$directory" }
$attempt = Get-UpdateChildPath $work ('BuildAttempts/Code-' + [Guid]::NewGuid().ToString('N'))
New-UpdateDirectory $attempt
$targets = "$attempt/RC25.References.targets"
$escapedPlugins = [Security.SecurityElement]::Escape($plugins)
$escapedManaged = [Security.SecurityElement]::Escape($managed)
$escapedCompressionDirectory = [Security.SecurityElement]::Escape($compressionDirectory)
# Engine.csproj still has an RC23 HintPath. Override references at target time,
# without changing agent-owned project files or using ambient assembly lookup.
[IO.File]::WriteAllText($targets, @"
<Project>
  <Target Name="PinRC25FrameworkReferences" BeforeTargets="GetReferenceAssemblyPaths" Condition="'`$(TargetFramework)' == 'net472'">
    <PropertyGroup>
      <_TargetFrameworkDirectories>$escapedManaged</_TargetFrameworkDirectories>
      <_FullFrameworkReferenceAssemblyPaths>$escapedManaged</_FullFrameworkReferenceAssemblyPaths>
    </PropertyGroup>
  </Target>
  <Target Name="PinRC25BaselineReferences" BeforeTargets="ResolveAssemblyReferences" Condition="'`$(TargetFramework)' == 'net472'">
    <ItemGroup>
      <Reference Condition="'%(Reference.Identity)' == 'ApfCore'"><HintPath>$escapedPlugins/ApfCore.dll</HintPath><Private>false</Private></Reference>
      <Reference Condition="'%(Reference.Identity)' == 'LmpClient'"><HintPath>$escapedPlugins/LmpClient.dll</HintPath><Private>false</Private></Reference>
      <Reference Remove="System.IO.Compression;System.IO.Compression.FileSystem" />
      <Reference Include="$escapedCompressionDirectory/System.IO.Compression.dll"><HintPath>$escapedCompressionDirectory/System.IO.Compression.dll</HintPath><SpecificVersion>false</SpecificVersion><Private>false</Private></Reference>
      <Reference Include="$escapedCompressionDirectory/System.IO.Compression.FileSystem.dll"><HintPath>$escapedCompressionDirectory/System.IO.Compression.FileSystem.dll</HintPath><SpecificVersion>false</SpecificVersion><Private>false</Private></Reference>
    </ItemGroup>
  </Target>
</Project>
"@, $utf8)
$nugetConfig = "$attempt/NuGet.Config"
[IO.File]::WriteAllText($nugetConfig, '<configuration><packageSources><clear /></packageSources></configuration>', $utf8)
$environment = @{
    DOTNET_CLI_HOME = "$attempt/cli"
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
    DOTNET_NOLOGO = '1'
    MSBUILDDISABLENODEREUSE = '1'
    NUGET_PACKAGES = "$attempt/packages"
    NUGET_HTTP_CACHE_PATH = "$attempt/nuget-http"
    NUGET_PLUGINS_CACHE_PATH = "$attempt/nuget-plugins"
    TEMP = "$attempt/temp"
    TMP = "$attempt/temp"
}
$previousEnvironment = @{}
foreach ($name in $environment.Keys) {
    $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
    [Environment]::SetEnvironmentVariable($name, $environment[$name], 'Process')
}
New-UpdateDirectory "$attempt/temp"
Push-Location $workspace
try {
    $common = @('-c', 'Release', '--nologo', '-v:minimal', '-nodeReuse:false', '-p:UseSharedCompilation=false',
        '-p:MSBuildEnableWorkloadResolver=false', '-p:AutomaticallyUseReferenceAssemblyPackages=false',
        "-p:RestoreConfigFile=$nugetConfig", "-p:CustomAfterMicrosoftCommonTargets=$targets")
    $projects = if ($EngineOnly) { @('Engine/Engine.csproj') } else { @('Engine/Engine.csproj', 'ClientUi/ClientUi.csproj', 'CorePatcher/Patcher.csproj', 'ClientPatcher/ClientPatcher.csproj') }
    foreach ($project in $projects) {
        $arguments = @('build', (Get-UpdateChildPath $work $project)) + $common
        if ($project -like 'Engine/*' -or $project -like 'ClientUi/*') {
            # Compile against the game's Mono framework, not an ambient NuGet or
            # machine-wide reference package. No restore download is required.
            $arguments += @("-p:FrameworkPathOverride=$managed", "-p:TargetFrameworkRootPath=$attempt/frameworks",
                "-p:BaselinePlugins=$plugins", "-p:UnityManaged=$managed", "-p:EngineAssembly=$work/Engine/bin/Release/net472/KesasInstall.dll")
        }
        & $dotnet @arguments
        if ($LASTEXITCODE -ne 0) { throw "Compilation failed: $project (exit $LASTEXITCODE)" }
    }
    Assert-CodeBaseline
    # Metadata-only check: never execute the built assemblies. Framework 4.7.2
    # reference packages expose Compression 4.2.0.0, but this client ships 4.0.0.0.
    Add-Type -Path "$workspace/tools/dotnet-sdk6/sdk/6.0.428/Sdks/Microsoft.NET.ILLink.Tasks/tools/net6.0/Mono.Cecil.dll"
    $net472Outputs = if ($EngineOnly) { @('Engine/bin/Release/net472/KesasInstall.dll') } else { @('Engine/bin/Release/net472/KesasInstall.dll', 'ClientUi/bin/Release/net472/AetherfrontConnectSource.dll') }
    foreach ($relative in $net472Outputs) {
        $assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Get-UpdateChildPath $work $relative))
        try {
            foreach ($reference in $assembly.MainModule.AssemblyReferences | Where-Object { $_.Name -in @('System.IO.Compression', 'System.IO.Compression.FileSystem') }) {
                if ($reference.Version.ToString() -cne '4.0.0.0') { throw "Incompatible compression reference in ${relative}: $($reference.FullName)" }
            }
        } finally { $assembly.Dispose() }
    }
    $outputs = if ($EngineOnly) { @('Engine/bin/Release/net472/KesasInstall.dll') } else { @('Engine/bin/Release/net472/KesasInstall.dll', 'ClientUi/bin/Release/net472/AetherfrontConnectSource.dll',
        'CorePatcher/bin/Release/net8.0/Patcher.dll', 'ClientPatcher/bin/Release/net8.0/ClientPatcher.dll') }
    $files = @(foreach ($relative in $outputs) {
        $path = Get-UpdateChildPath $work $relative
        [ordered]@{ path = $relative; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    $report = [ordered]@{ version = 'kesas-rc25'; baseVersion = 'kesas-rc24'; files = $files; engineOnly = $EngineOnly.IsPresent; patchersExecuted = $false;
        baselineHashes = $pins; frameworkReferences = $managed; compressionReferences = $compressionDirectory;
        compressionAssemblyVersion = '4.0.0.0'; notActivated = $true;
        nextStep = 'After final Engine/ClientUi changes, rebuild, then run the main-owned CorePatcher and UI-owned ClientPatcher against authenticated RC24 inputs. Build-Release consumes Patched/ApfCore.dll and ClientPatcher/Patched-Verified/LmpClient.dll; it never creates them.' }
    [IO.File]::WriteAllText("$attempt/CODE-RESULT.json", ($report | ConvertTo-Json -Depth 10) + "`n", $utf8)
    $report | ConvertTo-Json -Depth 10
} finally {
    Pop-Location
    foreach ($name in $environment.Keys) { [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], 'Process') }
}
