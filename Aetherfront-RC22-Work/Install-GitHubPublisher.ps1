param([switch]$Apply,[string]$ExpectedCurrentSha256='')
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../KESAS-VPS-Server'))
. "$root/Update.Common.ps1"
foreach($path in @($root,"$root/Publish-Release.ps1","$root/GitHub.Publish.ps1")){Assert-UpdatePath $path|Out-Null}
$before=Join-Path $PSScriptRoot 'NativePublisher/Publish-Release.before.ps1'
$expected=if($ExpectedCurrentSha256){
    if($ExpectedCurrentSha256 -notmatch '^[a-fA-F0-9]{64}$'){throw 'Expected current publisher SHA256 must contain 64 hexadecimal characters.'}
    $ExpectedCurrentSha256
}else{(Get-FileHash -LiteralPath $before).Hash}
if((Get-FileHash -LiteralPath "$root/Publish-Release.ps1").Hash-ne $expected){throw 'Native publisher changed since review; do not overwrite it.'}
foreach($relative in @('NativePublisher/Publish-Release.ps1','GitHub.Publish.ps1')){
    $path=Join-Path $PSScriptRoot $relative
    $tokens=$null;$errors=$null;$null=[Management.Automation.Language.Parser]::ParseFile($path,[ref]$tokens,[ref]$errors)
    if($errors.Count){throw "Publisher syntax errors: $relative"}
}
if(-not $Apply){Write-Output 'Preflight passed. No running service or file changed. Authenticate gh, then rerun with -Apply before the first GitHub-enabled release.';exit 0}
& 'C:/Program Files/GitHub CLI/gh.exe' auth status --hostname github.com
if($LASTEXITCODE-ne 0){throw 'Authenticate GitHub CLI on this server first.'}
$backup=Get-UpdateChildPath $root ('Backups/github-publisher/'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N'))
New-UpdateDirectory $backup
foreach($name in @('Publish-Release.ps1','GitHub.Publish.ps1')){if([IO.File]::Exists("$root/$name")){Copy-UpdateAtomic "$root/$name" "$backup/$name"}}
# Install the library before activating its caller. This changes only future
# release automation; it neither restarts nor reconfigures a running service.
Copy-UpdateAtomic "$PSScriptRoot/GitHub.Publish.ps1" "$root/GitHub.Publish.ps1"
Copy-UpdateAtomic "$PSScriptRoot/NativePublisher/Publish-Release.ps1" "$root/Publish-Release.ps1"
Write-Output "GitHub release hooks installed for future staged updates. Backup: $backup. No services restarted."
