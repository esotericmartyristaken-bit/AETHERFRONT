param(
    [Parameter(Mandatory = $true)][string]$TransactionPath,
    [ValidateSet('pack', 'client', 'rollback')][string]$UpdateKind = 'pack',
    [switch]$Headless
)
$ErrorActionPreference = 'Stop'
$helperRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$gameRoot = [IO.Directory]::GetParent([IO.Directory]::GetParent($helperRoot).FullName).FullName
function Assert-PlainPath([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Update path traverses a link: $current" }
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
    # Non-DLL names keep shadow dependencies out of plugin discovery.
    foreach ($name in @('JsonFx', 'ApfCore', 'KesasInstall')) {
        $source = [IO.Path]::Combine($helperRoot, 'Plugins', $name + '.dll')
        Assert-PlainPath $source
        $destination = [IO.Path]::Combine($runtime, $name + '.bin')
        [IO.File]::WriteAllBytes($destination, [IO.File]::ReadAllBytes($source))
        [Reflection.Assembly]::LoadFrom($destination) | Out-Null
    }
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $completion = [KesasInstall.NativeInstall]::Run($helperRoot, $TransactionPath, $UpdateKind)
    try {
        # Restart was explicitly requested by the in-game button. Headless tests
        # and recovery never launch a game process.
        if (!$Headless -and $UpdateKind -ne 'rollback') { $completion.Restart() }
    } finally { $completion.Dispose() }
    exit 0
} catch {
    $message = $_.Exception.ToString()
    try {
        $log = [IO.Path]::Combine($gameRoot, 'APF', 'APFUpdater.log')
        Assert-PlainPath $log
        [IO.File]::AppendAllText($log, [DateTime]::UtcNow.ToString('O') + ' ' + $message + [Environment]::NewLine)
    } catch { }
    [Console]::Error.WriteLine($message)
    exit 1
}
