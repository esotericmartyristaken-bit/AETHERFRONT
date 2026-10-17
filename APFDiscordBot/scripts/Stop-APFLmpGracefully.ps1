param(
  [Parameter(Mandatory = $true)] [int] $ProcessId,
  [Parameter(Mandatory = $true)] [string] $ExecutablePath,
  [ValidateRange(5, 120)] [int] $TimeoutSeconds = 45
)
$ErrorActionPreference = 'Stop'
$expected = [IO.Path]::GetFullPath($ExecutablePath)
$target = Get-CimInstance Win32_Process -Filter "ProcessId = $ProcessId" -ErrorAction SilentlyContinue
if (-not $target) {
  [pscustomobject]@{ stopped = $true; pid = $ProcessId; graceful = $true; alreadyExited = $true; forced = $false } | ConvertTo-Json -Compress
  exit 0
}
$actual = [IO.Path]::GetFullPath([string]$target.ExecutablePath)
if (-not [string]::Equals($actual, $expected, [StringComparison]::OrdinalIgnoreCase)) {
  throw "PID $ProcessId executable mismatch. Expected '$expected'; found '$actual'."
}

if (-not ('ApfConsoleSignal' -as [type])) {
  Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ApfConsoleSignal {
  [DllImport("kernel32.dll", SetLastError=true)] public static extern bool FreeConsole();
  [DllImport("kernel32.dll", SetLastError=true)] public static extern bool AttachConsole(uint processId);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern bool GenerateConsoleCtrlEvent(uint signal, uint processGroupId);
}
'@
}

$signalled = $false
try {
  [ApfConsoleSignal]::FreeConsole() | Out-Null
  if ([ApfConsoleSignal]::AttachConsole([uint32]$ProcessId)) {
    [ApfConsoleSignal]::SetConsoleCtrlHandler([IntPtr]::Zero, $true) | Out-Null
    $signalled = [ApfConsoleSignal]::GenerateConsoleCtrlEvent(0, 0)
  }
} finally {
  [ApfConsoleSignal]::FreeConsole() | Out-Null
}

$deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
while ($signalled -and [DateTime]::UtcNow -lt $deadline -and (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)) {
  Start-Sleep -Milliseconds 250
}
$stillRunning = [bool](Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)
if ($stillRunning) {
  Stop-Process -Id $ProcessId -Force -ErrorAction Stop
  Wait-Process -Id $ProcessId -Timeout 10 -ErrorAction SilentlyContinue
}
[pscustomobject]@{
  stopped = -not [bool](Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)
  pid = $ProcessId
  graceful = $signalled -and -not $stillRunning
  alreadyExited = $false
  forced = $stillRunning
} | ConvertTo-Json -Compress
