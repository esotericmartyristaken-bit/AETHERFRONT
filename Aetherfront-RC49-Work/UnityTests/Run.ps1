#requires -Version 7.4
param([Parameter(Mandatory)][string]$Candidate,[Parameter(Mandatory)][ValidatePattern('^[a-z0-9-]+$')][string]$Tag)
$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$root=Join-Path $workspace "UpdateBuilds/RC49-Recovery-20261106/unity-$Tag"
if(Test-Path -LiteralPath $root){throw 'Use a new independent KSP fixture'}
$game=Join-Path $root 'Game';$plugins=Join-Path $game 'GameData/RecoveryProbe/Plugins'
$source=Join-Path $workspace 'Aetherfront-KSP-Smoke';$managed=Join-Path $source 'KSP_x64_Data/Managed'
$null=New-Item -ItemType Directory -Path $plugins
$null=New-Item -ItemType Directory -Path "$game/GameData/Squad"
foreach($name in @('KSP_x64.exe','UnityPlayer.dll','Physics.cfg','buildID64.txt')){Copy-Item -LiteralPath "$source/$name" -Destination "$game/$name"}
foreach($name in @('KSP_x64_Data','MonoBleedingEdge')){
 & robocopy "$source/$name" "$game/$name" /E /COPY:DAT /DCOPY:DAT /R:0 /W:0 /XJ /NFL /NDL /NJH /NJS
 if($LASTEXITCODE -gt 7){throw 'Independent KSP runtime copy failed'}
}
# No real client install, save, updater or host credentials are copied. The
# dependencies are from the already-verified signed production client archive.
$zip=[IO.Compression.ZipFile]::OpenRead("$workspace/KESAS-VPS-Server/Updates/kesas-rc48/APFClient-kesas-rc48.zip")
try{foreach($entry in $zip.Entries|Where-Object FullName -Match '^GameData/LunaMultiplayer/Plugins/[^/]+\.dll$'){
 $output=Join-Path $plugins $entry.Name;$stream=[IO.File]::Create($output)
 try{$inputStream=$entry.Open();try{$inputStream.CopyTo($stream)}finally{$inputStream.Dispose()}}finally{$stream.Dispose()}
}}finally{$zip.Dispose()}
Copy-Item -LiteralPath "$source/GameData/000_Harmony/0Harmony.dll" -Destination "$plugins/0Harmony.dll"
Add-Type -Path "$workspace/tools/ilspy/.store/ilspycmd/9.1.0.7988/ilspycmd/9.1.0.7988/tools/net8.0/any/Mono.Cecil.dll"
$resolver=[Mono.Cecil.DefaultAssemblyResolver]::new();$resolver.AddSearchDirectory($managed);$resolver.AddSearchDirectory($plugins)
# Disable only dependency addon discovery in this test fixture. Their executable
# methods remain unchanged; the candidate PersonalClient DLL is never rewritten.
foreach($file in Get-ChildItem -LiteralPath $plugins -Filter '*.dll' -File|Where-Object Name -NE 'AetherfrontPersonalClient.dll'){
 $parameters=[Mono.Cecil.ReaderParameters]::new();$parameters.InMemory=$true;$parameters.AssemblyResolver=$resolver
 $assembly=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($file.FullName,$parameters)
 try{$changed=$false;foreach($type in $assembly.MainModule.Types){foreach($attribute in @($type.CustomAttributes|Where-Object {$_.AttributeType.FullName -eq 'KSPAddon'})){$type.CustomAttributes.Remove($attribute)|Out-Null;$changed=$true}};if($changed){$assembly.Write($file.FullName)}}finally{$assembly.Dispose()}
}
$resolver.Dispose()
Copy-Item -LiteralPath ([IO.Path]::GetFullPath($Candidate)) -Destination "$plugins/AetherfrontPersonalClient.dll" -Force
$dot=Join-Path $workspace 'tools/dotnet-sdk8/dotnet.exe'
$references=@('mscorlib.dll','System.dll','System.Core.dll','Assembly-CSharp.dll','UnityEngine.dll','UnityEngine.CoreModule.dll')|ForEach-Object{'-r:'+(Join-Path $managed $_)}
& $dot "$workspace/tools/dotnet-sdk8/sdk/8.0.424/Roslyn/bincore/csc.dll" -nologo -noconfig -nostdlib -langversion:latest -target:library -optimize+ "-out:$plugins/RecoveryStartupProbe.dll" @references "$PSScriptRoot/StartupProbe.cs"
if($LASTEXITCODE -ne 0){throw 'Native startup probe compile failed'}
$env:AF_RECOVERY_STARTUP_ROOT=$root
$process=Start-Process -FilePath "$game/KSP_x64.exe" -ArgumentList @('-batchmode','-nographics','-logFile',"`"$root/unity.log`"") -WorkingDirectory $game -WindowStyle Hidden -PassThru
$process.PriorityClass='BelowNormal'
$deadline=[DateTime]::UtcNow.AddSeconds(180)
while(-not $process.HasExited -and [DateTime]::UtcNow -lt $deadline){Start-Sleep -Milliseconds 250;$process.Refresh()}
if(-not $process.HasExited){$process.Kill();throw 'Private KSP startup test timed out; only its owned test process was stopped'}
if(-not(Test-Path -LiteralPath "$root/startup-result.json")){throw 'KSP exited without startup evidence'}
$result=Get-Content -LiteralPath "$root/startup-result.json" -Raw|ConvertFrom-Json
if($result.passed -ne $true -or $result.candidateSha256 -ine (Get-FileHash -LiteralPath $Candidate).Hash){throw 'Actual candidate did not pass automatic KSP startup'}
$result|ConvertTo-Json
