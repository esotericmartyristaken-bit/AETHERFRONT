$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$dotnet=Join-Path $root 'tools/dotnet-sdk8/dotnet.exe'
$mono=Join-Path $root 'Aetherfront-RC22-Work/MonoHost/bin/Release/net472/MonoHost.exe'
foreach($name in @('TransferTests','GitHubTests')) {
    & $dotnet build "$PSScriptRoot/$name/$name.csproj" -c Release --nologo -v quiet
    if($LASTEXITCODE){throw "Build failed: $name"}
    $binary="$PSScriptRoot/$name/bin/Release/net472/$name.exe"
    foreach($runtime in @('CLR','KSP-Mono')) {
        $log="$PSScriptRoot/$name-$runtime.log"
        if($runtime -eq 'CLR'){& $binary *> $log}else{& $mono $binary *> $log}
        if($LASTEXITCODE){Get-Content -LiteralPath $log -Tail 25;throw "$name failed under $runtime"}
        Get-Content -LiteralPath $log -Tail 2
    }
}
