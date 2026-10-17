$ErrorActionPreference = 'Stop'
$botRoot = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $botRoot
if (-not $env:APF_DISCORD_TOKEN) { $env:APF_DISCORD_TOKEN = [Environment]::GetEnvironmentVariable('APF_DISCORD_TOKEN', 'User') }
if (-not $env:APF_DISCORD_TOKEN) { throw 'APF_DISCORD_TOKEN is not set. Store it in the current user environment; never write it to config.json.' }
$stateRoot = Join-Path $botRoot 'state'
New-Item -ItemType Directory -Path $stateRoot -Force | Out-Null
$runtimeLog = Join-Path $stateRoot 'bot-runtime.log'
function Write-RuntimeLog {
    process {
        $line = [string]$_
        $line | Add-Content -LiteralPath $runtimeLog -Encoding utf8
        Write-Output $_
    }
}
"[$([DateTime]::UtcNow.ToString('O'))] APF Discord launcher starting." | Add-Content -LiteralPath $runtimeLog -Encoding utf8
$node = (Get-Command node.exe -ErrorAction Stop).Source
$ErrorActionPreference = 'Continue'
& $node src/validate-config.js 2>&1 | Write-RuntimeLog
$validationExit = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
if ($validationExit -ne 0) { throw 'APF Discord configuration validation failed.' }
$ErrorActionPreference = 'Continue'
& $node src/index.js 2>&1 | Write-RuntimeLog
$botExit = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
if ($botExit -ne 0) { throw "APF Discord bot exited with code $botExit. See $runtimeLog" }
