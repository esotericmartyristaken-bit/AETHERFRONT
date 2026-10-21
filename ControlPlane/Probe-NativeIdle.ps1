param([Parameter(Mandatory)][string]$NativeRoot)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath($NativeRoot)
for($cursor=$root;$cursor;$cursor=[IO.Path]::GetDirectoryName($cursor)) {
    if([IO.File]::GetAttributes($cursor) -band [IO.FileAttributes]::ReparsePoint){throw 'Unsafe native root'}
}
if([IO.File]::Exists((Join-Path $root '.update-transaction.json'))){Write-Output 'busy';exit 0}
$lease=$null
try {
    $lease=[IO.FileStream]::new((Join-Path $root '.publish.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    Write-Output 'idle'
} catch [IO.IOException] {Write-Output 'busy'}
finally {if($lease){$lease.Dispose()}}
