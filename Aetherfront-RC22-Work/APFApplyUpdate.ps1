param(
    [Parameter(Mandatory = $true)][string]$TransactionPath,
    [ValidateSet('pack', 'client', 'rollback')][string]$UpdateKind = 'pack'
)
$ErrorActionPreference = 'Stop'
$helperRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$gameRoot = [IO.Directory]::GetParent([IO.Directory]::GetParent($helperRoot).FullName).FullName
$runtime = $null
function Assert-PlainPath([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Update path traverses a link: $current"
            }
        }
        $parent = [IO.Directory]::GetParent($current)
        if ($null -eq $parent) { break }
        $current = $parent.FullName
    }
}
try {
    if ($helperRoot -ine [IO.Path]::Combine($gameRoot, 'GameData', 'LunaMultiplayer')) { throw 'Invalid helper location' }
    Assert-PlainPath $helperRoot
    $runtime = [IO.Path]::Combine($gameRoot, 'APF', '.helper-runtime', [Guid]::NewGuid().ToString('N'))
    Assert-PlainPath $runtime
    [IO.Directory]::CreateDirectory($runtime) | Out-Null
    # Load a local shadow copy so the running helper never locks installed DLLs.
    foreach ($name in @('JsonFx.dll', 'ApfCore.dll')) {
        $source = [IO.Path]::Combine($helperRoot, 'Plugins', $name)
        Assert-PlainPath $source
        $destination = [IO.Path]::Combine($runtime, $name)
        [IO.File]::WriteAllBytes($destination, [IO.File]::ReadAllBytes($source))
        [Reflection.Assembly]::LoadFrom($destination) | Out-Null
    }
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    if ($UpdateKind -eq 'rollback') {
        [ApfCore.ApfPostExitRunner]::Restore($helperRoot, $TransactionPath)
    } elseif ($UpdateKind -eq 'pack' -and (Test-Path -LiteralPath ([IO.Path]::Combine($helperRoot, 'Plugins', 'KesasInstall.dll')))) {
        $source = [IO.Path]::Combine($helperRoot, 'Plugins', 'KesasInstall.dll')
        Assert-PlainPath $source
        $destination = [IO.Path]::Combine($runtime, 'KesasInstall.dll')
        [IO.File]::WriteAllBytes($destination, [IO.File]::ReadAllBytes($source))
        [Reflection.Assembly]::LoadFrom($destination) | Out-Null
        [KesasInstall.PostExit]::Apply($helperRoot, $TransactionPath)
    } else {
        [ApfCore.ApfPostExitRunner]::Apply($helperRoot, $TransactionPath, ($UpdateKind -eq 'client'))
    }
    exit 0
}
catch {
    $message = $_.Exception.ToString()
    try {
        $log = [IO.Path]::Combine($gameRoot, 'APF', 'APFUpdater.log')
        Assert-PlainPath $log
        [IO.File]::AppendAllText($log, [DateTime]::UtcNow.ToString('O') + ' ' + $message + [Environment]::NewLine)
    } catch { }
    [Console]::Error.WriteLine($message)
    exit 1
}
