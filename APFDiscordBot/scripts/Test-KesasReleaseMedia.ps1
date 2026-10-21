param([Parameter(Mandatory)][string]$TestRoot)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$native=Join-Path $PSScriptRoot '../../KESAS-VPS-Server'
. "$native/Update.Common.ps1"
. "$native/GitHub.Publish.ps1"
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Resolve-Path "$native/Publish-Release.ps1"),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Native publisher does not parse.'}
$definition=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq 'Add-ReleaseMedia'},$true))
if($definition.Count -ne 1){throw 'Expected one actual media installer function.'}
Invoke-Expression $definition[0].Extent.Text
$root=Assert-UpdatePath $TestRoot
if(Test-Path -LiteralPath $root){throw 'Preserve existing fixture and use a new test root.'}
New-Item -ItemType Directory -Path "$root/Release/MediaPayload","$root/Config" -Force | Out-Null
$script:checks=0
function Check([bool]$Value,[string]$Label){if(!$Value){throw $Label};$script:checks++;Write-Output "PASS $Label"}
function Refuse([scriptblock]$Action,[string]$Label){$denied=$false;try{& $Action}catch{$denied=$true};Check $denied $Label}
function Fixture([string]$Text='KESAS_POTATO_COMPAT { enabled = true }',[string]$Entry='ZZZ_KESASPotato/Profile.cfg'){
    $tmp=Join-Path $root ([Guid]::NewGuid().ToString('N')+'.zip')
    $zip=[IO.Compression.ZipFile]::Open($tmp,[IO.Compression.ZipArchiveMode]::Create)
    try{$zipEntry=$zip.CreateEntry($Entry);$writer=[IO.StreamWriter]::new($zipEntry.Open());try{$writer.Write($Text)}finally{$writer.Dispose()}}finally{$zip.Dispose()}
    $hash=(Get-FileHash -LiteralPath $tmp).Hash.ToLowerInvariant();$name="kesas-potato-marker-$hash.zip"
    $source=Join-Path $root "Release/MediaPayload/$name";[IO.File]::Move($tmp,$source)
    $file=[pscustomobject]@{path="MediaPayload/$name";sha256=$hash}
    $package=[pscustomobject]@{archiveUrl="https://example.invalid/$name";sha256=$hash;sizeBytes=([IO.FileInfo]$source).Length}
    [pscustomobject]@{file=$file;catalog=[pscustomobject]@{full=@{managedPackages=@()};potato=@{managedPackages=@($package)}};target=(Join-Path $root "Config/$name");source=$source}
}
$good=Fixture
$release=[pscustomobject]@{files=@($good.file)}
Add-ReleaseMedia "$root/Release" "$root/Config" $release $good.catalog
Check ((Get-FileHash -LiteralPath $good.target).Hash -ieq $good.file.sha256) 'Valid public archive appended with exact bytes'
$mtime=(Get-Item -LiteralPath $good.target).LastWriteTimeUtc.Ticks
Add-ReleaseMedia "$root/Release" "$root/Config" $release $good.catalog
Check ((Get-Item -LiteralPath $good.target).LastWriteTimeUtc.Ticks -eq $mtime) 'Identical repeat is a no-op'
[IO.File]::WriteAllText($good.target,'preexisting conflicting bytes')
Refuse {Add-ReleaseMedia "$root/Release" "$root/Config" $release $good.catalog} 'Immutable destination conflict rejected'
Check ([IO.File]::ReadAllText($good.target) -ceq 'preexisting conflicting bytes') 'Conflict never overwrites an existing file'
$absent=Fixture 'second public marker'
$other=[pscustomobject]@{files=@($absent.file)}
$absent.catalog.potato.managedPackages[0].sha256='0'*64
Refuse {Add-ReleaseMedia "$root/Release" "$root/Config" $other $absent.catalog} 'Catalog/archive hash disagreement rejected'
Check (-not [IO.File]::Exists($absent.target)) 'Failed validation does not create destination'
$absent.catalog.potato.managedPackages=@()
Refuse {Add-ReleaseMedia "$root/Release" "$root/Config" $other $absent.catalog} 'Unreferenced media refused'
$absent.file.path='MediaPayload/Config/settings.zip'
Refuse {Add-ReleaseMedia "$root/Release" "$root/Config" $other $absent.catalog} 'Nested and non-content-addressed destinations refused'
$private=Fixture 'synthetic fixture data' 'secrets/private.key'
Refuse {Add-ReleaseMedia "$root/Release" "$root/Config" ([pscustomobject]@{files=@($private.file)}) $private.catalog} 'Private-file archive rejected before public installation'
Check (-not [IO.File]::Exists($private.target)) 'Rejected private archive is absent from public root'
Check (@(Get-ChildItem -LiteralPath "$root/Config" -Filter '*.incoming-*').Count -eq 0) 'No partial public media files remain'
Write-Output "PASS: $script:checks release media checks; production not accessed."
