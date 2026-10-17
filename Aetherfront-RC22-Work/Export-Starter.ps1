$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$work=$PSScriptRoot
$root='.\KESAS-VPS-Server'
$output=Join-Path $work '../client-artifacts/Aetherfront-RC22'
if(Test-Path -LiteralPath $output){throw 'Starter output exists; preserve distributed archives.'}
$null=New-Item -ItemType Directory -Path $output
$export=Join-Path $root 'Aetherfront-RC22-Export.zip'
& "$root/Export-Client.ps1" -Template "$work/Release/ClientTemplate.zip" -Output $export
$destination=Join-Path $output 'Aetherfront-Starter-rc22.zip'
$source=[IO.Compression.ZipFile]::OpenRead($export)
$zip=[IO.Compression.ZipFile]::Open($destination,[IO.Compression.ZipArchiveMode]::Create)
try{
    foreach($entry in $source.Entries){
        $name=$entry.FullName
        # Ship the complete signed overlay. Filtering this inventory causes an
        # immediate same-version client repair on the first Connect.
        if($name -match '(?i)(privatekey|apfhelper.key|\.pfx$|update-signing-private|KesasInstallProfile|KesasInstallSelection|KESASGraphicsBeforePotato|(^|/)saves/)'){throw "Private/player state in starter: $name"}
        if($name -match '(?i)(\.cmd$|KesasLauncher\.bin$)'){throw "External launcher in in-game starter: $name"}
        $input=$entry.Open();$stream=$zip.CreateEntry($name,[IO.Compression.CompressionLevel]::Optimal).Open();try{$input.CopyTo($stream)}finally{$input.Dispose();$stream.Dispose()}
    }
    $null=[IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,"$work/PLAYER-INSTALL.txt",'READ ME FIRST.txt',[IO.Compression.CompressionLevel]::Optimal)
}finally{$source.Dispose();$zip.Dispose()}
$sha=(Get-FileHash -LiteralPath $destination).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$destination.sha256",$sha+'  '+[IO.Path]::GetFileName($destination)+"`n",[Text.UTF8Encoding]::new($false))
Write-Output "$destination : $((Get-Item -LiteralPath $destination).Length) bytes; SHA256 $sha"
