#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$Workspace = (Join-Path $PSScriptRoot '../../..'),
    [ValidateSet('focused', 'catalog')][string]$Mode = 'focused',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot ('Results-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8)))
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$workspacePath = (Resolve-Path -LiteralPath $Workspace).Path
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$managed = Join-Path $workspacePath 'Aetherfront-KSP-Smoke/KSP_x64_Data/Managed'
$executable = Join-Path $PSScriptRoot ('ModuleManagerProbe-' + $Mode + '.exe')
$references = @(
    (Join-Path $managed 'Assembly-CSharp.dll'),
    (Join-Path $managed 'UnityEngine.dll'),
    (Join-Path $managed 'UnityEngine.CoreModule.dll'),
    (Join-Path $workspacePath 'Aetherfront-KSP-Smoke/GameData/ModuleManager.4.2.3.dll'),
    (Join-Path $workspacePath 'Aetherfront-RC25-Work/P3e9e2e69/GameData/b9partswitch/Plugins/B9PartSwitch.dll'),
    (Join-Path $workspacePath 'tools/dotnet-sdk6/sdk/6.0.428/Sdks/Microsoft.NET.ILLink.Tasks/tools/net472/Mono.Cecil.dll'),
    (Join-Path $workspacePath 'tools/dotnet-sdk6/sdk/6.0.428/Microsoft/Microsoft.NET.Build.Extensions/net461/lib/netstandard.dll'),
    'System.IO.Compression.dll',
    'System.IO.Compression.FileSystem.dll',
    'System.Web.Extensions.dll'
) | ForEach-Object { '/reference:' + $_ }
& $compiler /nologo /target:exe ('/out:' + $executable) @references (Join-Path $PSScriptRoot 'ModuleManagerProbe.cs')
if ($LASTEXITCODE -ne 0) { throw 'ModuleManager harness compilation failed.' }
& $executable $workspacePath ([IO.Path]::GetFullPath($OutputDirectory)) $Mode
if ($LASTEXITCODE -ne 0) { throw 'ModuleManager harness failed.' }
