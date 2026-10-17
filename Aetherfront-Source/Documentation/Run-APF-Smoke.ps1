param(
    [int]$MasterPort = 18700,
    [int]$MasterHttpPort = 18701,
    [int]$ServerPort = 18800
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
$masterDirectory = Join-Path $sourceRoot 'Build\APF\MasterServer'
$serverSource = Join-Path $sourceRoot 'Build\APF\Server'
$runtimeDirectory = Join-Path $sourceRoot 'TestRuntime\APFServer'
$vanillaRuntimeDirectory = Join-Path $sourceRoot 'TestRuntime\VanillaServer'
$masterExecutable = Join-Path $masterDirectory 'MasterServer.exe'
$serverExecutable = Join-Path $runtimeDirectory 'Server.exe'
$vanillaExecutable = Join-Path $vanillaRuntimeDirectory 'Server.exe'
$masterPort = $MasterPort
$masterHttpPort = $MasterHttpPort
if (-not (Test-Path -LiteralPath $masterExecutable)) { throw "MasterServer publish missing: $masterExecutable" }
if (-not (Test-Path -LiteralPath $serverSource)) { throw "Server publish missing: $serverSource" }
if (-not (Test-Path -LiteralPath $serverExecutable)) { throw "Test runtime server missing: $serverExecutable" }
if (-not (Test-Path -LiteralPath $vanillaExecutable)) { throw "Vanilla test runtime missing: $vanillaExecutable" }

Get-ChildItem -LiteralPath $serverSource -File | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $runtimeDirectory $_.Name) -Force
}
Get-ChildItem -LiteralPath $serverSource -Directory | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $runtimeDirectory -Recurse -Force
}

$debugPath = Join-Path $runtimeDirectory 'Config\DebugSettings.xml'
[xml]$debug = Get-Content -LiteralPath $debugPath -Raw
$debug.SelectSingleNode('//CustomMasterServer').InnerText = "127.0.0.1:$masterPort"
$debug.Save($debugPath)
$connectionPath = Join-Path $runtimeDirectory 'Config\ConnectionSettings.xml'
[xml]$connection = Get-Content -LiteralPath $connectionPath -Raw
$connection.SelectSingleNode('//Port').InnerText = [string]$ServerPort
$connection.Save($connectionPath)

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$testRuntime = Join-Path $sourceRoot 'TestRuntime'
$masterOut = Join-Path $testRuntime ("smoke-$stamp.master.out.log")
$masterErr = Join-Path $testRuntime ("smoke-$stamp.master.err.log")
$serverOut = Join-Path $testRuntime ("smoke-$stamp.server.out.log")
$serverErr = Join-Path $testRuntime ("smoke-$stamp.server.err.log")
$vanillaOut = Join-Path $testRuntime ("smoke-$stamp.vanilla.out.log")
$vanillaErr = Join-Path $testRuntime ("smoke-$stamp.vanilla.err.log")
$master = $null
$server = $null
$vanilla = $null
$exitCode = 1
try {
    $master = Start-Process -FilePath $masterExecutable -WorkingDirectory $masterDirectory -WindowStyle Hidden `
        -ArgumentList "/p:$MasterPort", "/g:$MasterHttpPort", '/noupnp', '/apfonly' `
        -RedirectStandardOutput $masterOut -RedirectStandardError $masterErr -PassThru
    Start-Sleep -Seconds 2
    $server = Start-Process -FilePath $serverExecutable -WorkingDirectory $runtimeDirectory -WindowStyle Hidden `
        -RedirectStandardOutput $serverOut -RedirectStandardError $serverErr -PassThru
    $vanilla = Start-Process -FilePath $vanillaExecutable -WorkingDirectory $vanillaRuntimeDirectory -WindowStyle Hidden `
        -RedirectStandardOutput $vanillaOut -RedirectStandardError $vanillaErr -PassThru
    Start-Sleep -Seconds 6

    $directory = @()
    for ($attempt = 0; $attempt -lt 15 -and @($directory).Count -ne 1; $attempt++) {
        $directoryResponse = Invoke-RestMethod -Uri "http://127.0.0.1:$MasterHttpPort/json" -TimeoutSec 10
        $directory = if ($directoryResponse -is [System.Array]) {
            @($directoryResponse)
        } elseif ($directoryResponse.PSObject.Properties.Name -contains 'value') {
            @($directoryResponse.value)
        } else {
            @($directoryResponse)
        }
        if (@($directory).Count -ne 1) { Start-Sleep -Seconds 1 }
    }
    $apfDirectoryEntry = @($directory) | Select-Object -First 1
    if (@($directory).Count -ne 1) { throw "APF-only HTTP directory expected one entry, got $(@($directory).Count)" }
    if (-not $apfDirectoryEntry.ApfEnabled) { throw 'APF-only HTTP directory returned a non-APF entry' }
    if ($apfDirectoryEntry.ApfServerId -ne 'apf-smoke') { throw "Unexpected APF server id: $($apfDirectoryEntry.ApfServerId)" }

    & $dotnet run --project (Join-Path $sourceRoot 'ApfProtocol.Tests\ApfProtocol.Tests.csproj') --configuration Release --no-build -- --master-port $masterPort --server-port $ServerPort
    $exitCode = $LASTEXITCODE
}
finally {
    foreach ($process in @($server, $vanilla, $master)) {
        if ($process -and -not $process.HasExited) {
            Stop-Process -Id $process.Id -Force
            $process.WaitForExit()
        }
    }
}
if ($exitCode -eq 0) {
    $masterLog = Get-Content -LiteralPath $masterOut -Raw
    if ($masterLog -notmatch '\[APF\]: Private master mode; public master-list check disabled') {
        throw 'APF-only master did not enter private master mode'
    }
    if ($masterLog -match "You're not listed in the master servers list") {
        throw 'APF-only master contacted the public master-list check'
    }
}
if ($exitCode -eq 0) { Write-Host 'APF smoke passed.' }
exit $exitCode
