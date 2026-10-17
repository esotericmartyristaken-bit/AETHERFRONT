$ErrorActionPreference='Stop'
# A small public checksum file tests real GitHub TLS and asset redirects.
# No account, token, private file, upload or bulk download is involved.
Add-Type -AssemblyName System.IO.Compression.FileSystem
[Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12
$metadata=Invoke-RestMethod -Uri 'https://api.github.com/repos/cli/cli/releases/latest' -Headers @{'User-Agent'='Aetherfront-Transport-Test';Accept='application/vnd.github+json'}
$asset=@($metadata.assets|Where-Object name -match '^gh_[0-9.]+_checksums\.txt$')
if($asset.Count-ne 1 -or $asset[0].size-gt 262144){throw 'Small public GitHub checksum asset not found.'}
$fixture=Join-Path $PSScriptRoot ('GitHubPublicFixture-'+[Guid]::NewGuid().ToString('N'))
[IO.Compression.ZipFile]::ExtractToDirectory([IO.Path]::GetFullPath("$PSScriptRoot/../client-artifacts/Aetherfront-RC21/Aetherfront-Starter-rc21.zip"),$fixture)
$null=[IO.Directory]::CreateDirectory("$fixture/GameData/Squad")
Copy-Item -LiteralPath "$PSScriptRoot/GameStub/bin/Release/net472/GameStub.exe" -Destination "$fixture/KSP_x64.exe"
foreach($name in @('JsonFx.dll','ApfCore.dll')){[Reflection.Assembly]::LoadFrom("$fixture/GameData/LunaMultiplayer/Plugins/$name")|Out-Null}
[Reflection.Assembly]::LoadFrom("$PSScriptRoot/Engine/bin/Release/net472/KesasInstall.dll")|Out-Null
$settings=[KesasInstall.GitHubSettings]::new();$settings.Enabled=$true;$settings.Repository='cli/cli'
$installer=[KesasInstall.Installer]::new($fixture,[Net.IPEndPoint]::new([Net.IPAddress]::Loopback,18865),'kesas-rc22')
[KesasInstall.Installer].GetField('github',[Reflection.BindingFlags]'Instance,NonPublic').SetValue($installer,$settings)
$destination=Join-Path $fixture 'APF/github-checksums.txt';$null=[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
$download=[KesasInstall.Installer].GetMethod('Download',[Reflection.BindingFlags]'Instance,NonPublic')
$null=$download.Invoke($installer,[object[]]@([Uri]$asset[0].browser_download_url,[string]$destination,[long]262144,$null))
if((Get-Item -LiteralPath $destination).Length-ne $asset[0].size){throw 'Public GitHub asset size mismatch.'}
$sha=(Get-FileHash -LiteralPath $destination).Hash.ToLowerInvariant()
if($asset[0].digest -and $asset[0].digest-ne ('sha256:'+$sha)){throw 'Public GitHub asset digest mismatch.'}
Write-Output "PASS Windows PowerShell5.1: real GitHub TLS, bounded approved redirects, $($asset[0].size)-byte public asset and SHA256 $sha. No Unity game launched."
