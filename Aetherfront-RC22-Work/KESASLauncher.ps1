param([string]$GameRoot='',[int]$WaitForPid=0,[switch]$VerifyOnly)
$ErrorActionPreference='Stop'
if(-not $GameRoot){$GameRoot=[IO.Directory]::GetParent([IO.Directory]::GetParent($PSScriptRoot).FullName).FullName}
$GameRoot=[IO.Path]::GetFullPath($GameRoot)
$game=Join-Path $GameRoot 'KSP_x64.exe'
if(-not (Test-Path -LiteralPath $game)){throw 'KSP_x64.exe is missing.'}
if($VerifyOnly){Write-Output 'KESAS installs through the in-game connection window.';exit 0}
if($WaitForPid -gt 0){
    $previous=Get-Process -Id $WaitForPid -ErrorAction SilentlyContinue
    if($previous){if($previous.Path -ine $game){throw 'The waiting process is not this KSP copy.'};if(-not $previous.WaitForExit(60000)){throw 'KSP did not close before restart.'}}
}
# Compatibility with RC16's already-running post-exit helper. New clients use
# the in-game connection flow and do not open the former external installer.
Start-Process -FilePath $game -WorkingDirectory $GameRoot
