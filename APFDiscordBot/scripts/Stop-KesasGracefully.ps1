[CmdletBinding()]
param([Parameter(Mandatory)][string]$DeploymentRoot, [Parameter(Mandatory)][int]$ProcessId)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $DeploymentRoot).Path
$expected = Join-Path $root 'Server\Server.exe'
$game = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
if ($game -and $game.Path -ine $expected) { throw 'KESAS process path mismatch' }
& (Join-Path $root 'Stop-Server.ps1') *> $null
$deadline = [DateTime]::UtcNow.AddSeconds(45)
do {
    $released = $false
    try {
        $lock = [IO.File]::Open((Join-Path $root '.server.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
        $lock.Dispose()
        $released = $true
    } catch [IO.IOException] { }
    if ($released -and -not (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)) {
        @{ stopped = $true; graceful = $true } | ConvertTo-Json -Compress
        exit 0
    }
    Start-Sleep -Milliseconds 250
} while ([DateTime]::UtcNow -lt $deadline)
throw 'KESAS launcher did not finish its clean shutdown; no replacement process was started'
