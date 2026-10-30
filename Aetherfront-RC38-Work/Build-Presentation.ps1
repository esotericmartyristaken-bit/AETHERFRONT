param(
 [string]$GameRoot = 'C:\Users\jkell\Documents\Codex\APFBeta',
 [string]$Python = 'C:\Program Files\Python310\python.exe'
)
$ErrorActionPreference='Stop'
$controller=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$build=Join-Path $controller 'build/rc38-presentation'
$managed=Join-Path $GameRoot 'KSP_x64_Data/Managed'
$plugins=Join-Path $GameRoot 'GameData/LunaMultiplayer/Plugins'
$harmony=Join-Path $GameRoot 'GameData/000_Harmony/0Harmony.dll'
$overlay=Join-Path $build 'overlay'
New-Item -ItemType Directory -Path $overlay -Force | Out-Null
& $Python "$PSScriptRoot/Build-Assets.py" "$PSScriptRoot/Assets/APF.png" $overlay
if($LASTEXITCODE -ne 0){throw 'APF asset build failed'}
& dotnet build "$PSScriptRoot/Client/AetherfrontPresentationClient.csproj" -c Release "-p:KspManaged=$managed" "-p:ClientPlugins=$plugins" "-p:HarmonyPath=$harmony" -o "$build/code" -v quiet
if($LASTEXITCODE -ne 0){throw 'Presentation client build failed'}
$target=Join-Path $overlay 'GameData/LunaMultiplayer/Plugins'
New-Item -ItemType Directory -Path $target -Force | Out-Null
Copy-Item -LiteralPath "$build/code/AetherfrontPresentationClient.dll" -Destination $target -Force
$compatibility=Join-Path $overlay 'GameData/AetherfrontCompatibility'
New-Item -ItemType Directory -Path $compatibility -Force | Out-Null
Copy-Item -LiteralPath "$PSScriptRoot/Compatibility/ParallaxNoCollisions.cfg" -Destination $compatibility -Force
Write-Output "Local unpublished overlay: $overlay. No game installation, server or release was modified."
