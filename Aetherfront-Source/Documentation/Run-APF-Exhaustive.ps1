param(
    [string]$SyncServerDirectory,
    [string]$SyncMasterDirectory
)

$ErrorActionPreference = 'Stop'
$sourceRoot = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $sourceRoot
$workspaceDotnet = Join-Path (Split-Path -Parent $sourceRoot) '.dotnet-sdk6\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $workspaceDotnet -PathType Leaf) {
    $workspaceDotnet
} elseif (Test-Path -LiteralPath 'C:\Program Files\dotnet\dotnet.exe' -PathType Leaf) {
    'C:\Program Files\dotnet\dotnet.exe'
} else { 'dotnet' }

function Invoke-Checked([string]$FilePath, [string[]]$Arguments, [string]$WorkingDirectory = $sourceRoot) {
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Command failed ($LASTEXITCODE): $FilePath $($Arguments -join ' ')" }
}

Write-Host '1/6: publishing APF artifacts'
$publishArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $sourceRoot 'Documentation\Publish-APFArtifacts.ps1'))
if ($SyncServerDirectory) { $publishArgs += @('-SyncServerDirectory', $SyncServerDirectory) }
if ($SyncMasterDirectory) { $publishArgs += @('-SyncMasterDirectory', $SyncMasterDirectory) }
Invoke-Checked 'powershell.exe' $publishArgs

Write-Host '2/6: core and helper integration tests'
Invoke-Checked $dotnet @('run', '--project', (Join-Path $sourceRoot 'ApfCore.Tests\ApfCore.Tests.csproj'), '--configuration', 'Release', '--no-restore')
Invoke-Checked $dotnet @('run', '--project', (Join-Path $sourceRoot 'ApfUpdater.Tests\ApfUpdater.Tests.csproj'), '--configuration', 'Release', '--no-restore')

Write-Host '3/6: update-tool smoke'
$temp = Join-Path ([IO.Path]::GetTempPath()) ('apf-exhaustive-' + [Guid]::NewGuid().ToString('N'))
try {
    $clientRoot = Join-Path $temp 'Client'
    $plugins = Join-Path $clientRoot 'GameData\LunaMultiPlayer\Plugins'
    New-Item -ItemType Directory -Path $plugins -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $plugins 'LmpClient.dll') -Value 'test-client' -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $plugins 'ApfCore.dll') -Value 'test-core' -Encoding UTF8
    $privateKey = Join-Path $temp 'private.xml'
    $publicKey = Join-Path $temp 'public.xml'
    Invoke-Checked $dotnet @('run', '--project', (Join-Path $sourceRoot 'ApfUpdateTool\ApfUpdateTool.csproj'), '--configuration', 'Release', '--no-restore', '--', '--generate-key', '--private-output', $privateKey, '--public-output', $publicKey)
    $updateOutput = Join-Path $temp 'Output'
    Invoke-Checked $dotnet @('run', '--project', (Join-Path $sourceRoot 'ApfUpdateTool\ApfUpdateTool.csproj'), '--configuration', 'Release', '--no-restore', '--', '--source-root', $clientRoot, '--output', $updateOutput, '--version', 'apf-client-2', '--package-base-url', 'https://updates.example.invalid/apf/', '--private-key-xml', $privateKey)
    if (-not (Test-Path -LiteralPath (Join-Path $updateOutput 'APFClientUpdate.json'))) { throw 'Update-tool manifest missing' }
}
finally {
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force }
}

Write-Host '4/6: protocol and discovery smoke'
Invoke-Checked 'powershell.exe' @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $sourceRoot 'Documentation\Run-APF-Smoke.ps1'))

Write-Host '5/6: PowerShell syntax'
$parseErrors = @()
Get-ChildItem -LiteralPath (Join-Path $sourceRoot 'Documentation') -Filter '*.ps1' | ForEach-Object {
    $tokens = $null
    $errors = $null
    [System.Management.Automation.Language.Parser]::ParseFile($_.FullName, [ref]$tokens, [ref]$errors) | Out-Null
    $parseErrors += $errors
}
if ($parseErrors.Count -gt 0) { $parseErrors | Format-List; throw 'PowerShell parser errors found' }
$versionSource = Join-Path $sourceRoot 'LmpClient\obj\Release\ApfClientVersion.g.cs'
Invoke-Checked $dotnet @('msbuild', (Join-Path $sourceRoot 'LmpClient\LmpClient.csproj'), '/t:GenerateApfClientVersion', '/p:Configuration=Release', "/p:SolutionDir=$sourceRoot\", '/p:ApfClientVersion=apf-client-1', '/v:minimal')
if (-not (Test-Path -LiteralPath $versionSource) -or (Get-Content -LiteralPath $versionSource -Raw) -notmatch 'Value = "apf-client-1"') {
    throw 'APF client build-version source generation failed'
}

Write-Host '6/6: process and port cleanup'
$leftovers = Get-CimInstance Win32_Process | Where-Object { $_.Name -in @('Server.exe', 'MasterServer.exe', 'ApfUpdater.exe') -and $_.ExecutablePath -like '*\APF\*' }
if (@($leftovers).Count -gt 0) { $leftovers | Format-Table ProcessId,Name,ExecutablePath; throw 'APF test process remained alive' }
$ports = Get-NetUDPEndpoint -ErrorAction SilentlyContinue | Where-Object { $_.LocalPort -in @(18700,18701,18702,18703,18800,18801) }
if (@($ports).Count -gt 0) { $ports | Format-Table LocalAddress,LocalPort,OwningProcess; throw 'APF smoke port remained bound' }
Write-Host 'APF exhaustive automated suite passed.'
