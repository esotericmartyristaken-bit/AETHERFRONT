#requires -Version 7.4
param([Parameter(Mandatory)][string]$Candidate,[Parameter(Mandatory)][string]$ClientPlugins,[Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$managed=Join-Path $workspace 'Aetherfront-KSP-Smoke/KSP_x64_Data/Managed'
$stage=[IO.Path]::GetFullPath($OutputDirectory)
if(-not $stage.StartsWith((Join-Path $workspace 'UpdateBuilds/RC46-') ,[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $stage)){throw 'Use a fresh RC46 evidence directory'}
$null=New-Item -ItemType Directory -Path $stage
foreach($file in Get-ChildItem -LiteralPath $ClientPlugins -Filter '*.dll'){Copy-Item -LiteralPath $file.FullName -Destination $stage}
Copy-Item -LiteralPath $Candidate -Destination (Join-Path $stage 'AetherfrontPersonalClient.dll') -Force
$references=@('mscorlib.dll','System.dll','System.Core.dll')|ForEach-Object {'-r:'+(Join-Path $managed $_)}
$references+=@(('-r:'+(Join-Path $stage 'AetherfrontCore.dll')),('-r:'+(Join-Path $stage 'AetherfrontPersonalClient.dll')))
$exe=Join-Path $stage 'research-mono.exe'
& "$workspace/tools/dotnet-sdk8/dotnet.exe" "$workspace/tools/dotnet-sdk8/sdk/8.0.424/Roslyn/bincore/csc.dll" -nologo -noconfig -nostdlib -langversion:latest -target:exe -platform:x64 -optimize+ "-out:$exe" @references (Join-Path $PSScriptRoot 'Program.cs')
if($LASTEXITCODE -ne 0){throw 'Mono fixture compile failed'}
Push-Location $workspace
try{
 $output=@(& "$workspace/Aetherfront-RC20-Work/MonoHost/bin/Release/net472/MonoHost.exe" $exe 2>&1|ForEach-Object ToString)
 $exitCode=$LASTEXITCODE
 [IO.File]::WriteAllLines((Join-Path $stage 'results.txt'),[string[]]$output)
 $output
 if($exitCode -ne 0){throw 'Actual grafted helper Mono gate failed'}
}finally{Pop-Location}
Get-FileHash -LiteralPath (Join-Path $stage 'AetherfrontPersonalClient.dll'),$exe -Algorithm SHA256|Select-Object Path,Hash
