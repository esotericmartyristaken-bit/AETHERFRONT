$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
  & npm test
  if ($LASTEXITCODE -ne 0) { throw "npm test failed with exit code $LASTEXITCODE" }
  Get-ChildItem src, test -Filter *.js -Recurse | ForEach-Object {
    & node --check $_.FullName
    if ($LASTEXITCODE -ne 0) { throw "Syntax check failed: $($_.FullName)" }
  }
  Get-ChildItem (Join-Path $PSScriptRoot '') -Filter *.ps1 | ForEach-Object {
    $tokens = $null; $parseErrors = $null
    [System.Management.Automation.Language.Parser]::ParseFile($_.FullName, [ref]$tokens, [ref]$parseErrors) > $null
    if ($parseErrors.Count) { throw "PowerShell parse failed: $($_.FullName)`n$($parseErrors | Out-String)" }
  }
  & npm audit --omit=dev
  if ($LASTEXITCODE -ne 0) { throw "npm audit failed with exit code $LASTEXITCODE" }
  & (Join-Path $PSScriptRoot 'Test-APFDiscordBridge.ps1')
  if ($LASTEXITCODE -ne 0) { throw "Bridge smoke failed with exit code $LASTEXITCODE" }
  & (Join-Path $PSScriptRoot 'Test-APFLifecycle.ps1')
  if ($LASTEXITCODE -ne 0) { throw "Lifecycle smoke failed with exit code $LASTEXITCODE" }
  Write-Output 'APF safe validation suite passed. Live LMP was not targeted.'
} finally { Pop-Location }
