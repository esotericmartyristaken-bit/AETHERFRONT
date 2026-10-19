#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Baseline,
    [Parameter(Mandatory)][string]$KspManaged,
    [Parameter(Mandatory)][string]$Compiler,
    [Parameter(Mandatory)][string]$Output
)
$ErrorActionPreference = 'Stop'
# Baseline must first pass the native signature/inventory verifier. The code
# graft independently verifies it again before accepting this engine.
$Baseline = (Resolve-Path -LiteralPath $Baseline).Path
$KspManaged = (Resolve-Path -LiteralPath $KspManaged).Path
$Compiler = (Resolve-Path -LiteralPath $Compiler).Path
$Output = [IO.Path]::GetFullPath($Output)
foreach ($root in @($Baseline,$KspManaged,$Compiler,$Output)) {
    for ($cursor=$root; $cursor; $cursor=[IO.Path]::GetDirectoryName($cursor)) {
        if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Reparse build input/output refused.' }
    }
}
$plugins=Join-Path $Baseline 'GameData/LunaMultiplayer/Plugins'
$compression=Join-Path $Baseline 'KSP_x64_Data/Managed'
$references=@(foreach($name in @('mscorlib','System','System.Core','System.Xml','System.Security')) { Join-Path $KspManaged "$name.dll" })
$references+=@(foreach($name in @('System.IO.Compression','System.IO.Compression.FileSystem')) { Join-Path $compression "$name.dll" })
$references+=Join-Path $plugins 'ApfCore.dll'
$sources=@(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'Engine') -File -Filter '*.cs' | Sort-Object Name | ForEach-Object FullName)
$sources+=@(foreach($name in @('Transfer.cs','DownloadFiles.cs','GitHub.cs')) { Join-Path $PSScriptRoot "../Aetherfront-RC22-Work/Engine/$name" })
foreach($path in $references+$sources) { if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing explicit build input: $path" } }
function Hashes { @($references+$sources | ForEach-Object { [ordered]@{path=[IO.Path]::GetFullPath($_);sha256=(Get-FileHash -LiteralPath $_).Hash.ToLowerInvariant()} }) }
$before=Hashes
[IO.Directory]::CreateDirectory($Output) | Out-Null
$arguments=@('/nostdlib+','/target:library','/langversion:10','/optimize+','/debug-','/deterministic+',('/out:"'+(Join-Path $Output 'KesasInstall.dll')+'"'))
$arguments+=@($references | ForEach-Object {'/reference:"'+$_+'"'})
$arguments+=@($sources | ForEach-Object {'"'+$_+'"'})
$response=Join-Path $Output 'build.rsp'
[IO.File]::WriteAllLines($response,$arguments)
& dotnet $Compiler /noconfig "@$response"
if($LASTEXITCODE -ne 0) { throw 'Pinned KSP engine compilation failed.' }
if (($before | ConvertTo-Json -Depth 5 -Compress) -cne ((Hashes) | ConvertTo-Json -Depth 5 -Compress)) { throw 'Build inputs changed during compilation.' }
$result=[ordered]@{version='kesas-rc31';engineSha256=(Get-FileHash -LiteralPath (Join-Path $Output 'KesasInstall.dll')).Hash.ToLowerInvariant();compilerSha256=(Get-FileHash -LiteralPath $Compiler).Hash.ToLowerInvariant();inputs=$before;serverAssembliesUsed=$false}
[IO.File]::WriteAllText((Join-Path $Output 'ENGINE-RESULT.json'),($result | ConvertTo-Json -Depth 5))
