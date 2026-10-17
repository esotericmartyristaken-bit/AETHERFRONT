[CmdletBinding()]
param([string]$WorkspaceRoot = '', [switch]$CheckOriginalSources)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
[Diagnostics.Process]::GetCurrentProcess().PriorityClass='BelowNormal'
function Get-SourceHash([string]$Path){$stream=[IO.File]::OpenRead($Path);$sha=[Security.Cryptography.SHA256]::Create();try{[BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','').ToLowerInvariant()}finally{$sha.Dispose();$stream.Dispose()}}
$root=Split-Path $PSScriptRoot
$manifestPath=Join-Path $root 'SOURCE-MANIFEST.json'
if(-not(Test-Path -LiteralPath $manifestPath)){throw 'Incomplete export: no finalized manifest.'}
$manifest=Get-Content -LiteralPath $manifestPath -Raw|ConvertFrom-Json
$scan=Get-Content -LiteralPath (Join-Path $root 'PRIVACY-SCAN.json') -Raw|ConvertFrom-Json
if($scan.findingCount -ne 0){throw 'Privacy review is not clean.'}
$errorsFound=[Collections.Generic.List[string]]::new()
$files=@(Get-ChildItem -LiteralPath $root -File -Recurse -Force)
$badExtensions=@('.dll','.exe','.pdb','.zip','.7z','.nupkg','.pem','.key','.pfx','.p12','.crt','.cer','.snk','.png','.jpg','.ico','.resources','.log','.cache')
foreach($file in $files){
    $relative=$file.FullName.Substring($root.Length+1).Replace('\','/')
    if($file.Extension.ToLowerInvariant() -in $badExtensions){$errorsFound.Add('Forbidden file type: '+$relative)}
    if($relative -match '(?i)(^|/)(?:\.git|bin|obj|node_modules|Universe|saves|secrets|diagnostics|state|Backups)(/|$)'){$errorsFound.Add('Forbidden runtime path: '+$relative)}
    if($file.Extension -eq '.resx'){
        [xml]$resource=[IO.File]::ReadAllText($file.FullName)
        if($resource.SelectNodes('//data[@mimetype or contains(@type,"System.Byte[]")]').Count){$errorsFound.Add('Embedded binary resource: '+$relative)}
    }
    if($file.Extension -eq '.ps1'){
        $parseErrors=$null
        $null=[Management.Automation.Language.Parser]::ParseFile($file.FullName,[ref]$null,[ref]$parseErrors)
        if($parseErrors.Count){$errorsFound.Add('PowerShell parse failure: '+$relative)}
    }
}
foreach($required in @('LICENSE','licenses/LGPL-2.1.txt','Aetherfront-Source/Server/Server.csproj','Aetherfront-Source/LmpClient/LmpClient.csproj','Aetherfront-Source/LmpCommon/Message/Data','Aetherfront-RC22-Work/Engine/GitHub.cs','Aetherfront-RC22-Work/GitHubTests/Program.cs','Aetherfront-RC22-Work/Test-KspRuntime.ps1')){
    if(-not(Test-Path -LiteralPath (Join-Path $root $required))){$errorsFound.Add('Required source missing: '+$required)}
}
foreach($omitted in @('Aetherfront-Source/LmpMasterServer/app.config','Aetherfront-Source/MasterServer/app.config','Aetherfront-Source/Scripts/CopyToKSPDirectory.bat')){if(Test-Path -LiteralPath (Join-Path $root $omitted)){$errorsFound.Add('Intentionally excluded file present: '+$omitted)}}
$sourceDrift=@()
if($CheckOriginalSources){
    if(-not $WorkspaceRoot){throw '-WorkspaceRoot is required for original-source comparison.'}
    $workspace=[IO.Path]::GetFullPath($WorkspaceRoot)
    $sourceDrift=@(foreach($entry in $manifest.files){
        if(-not $entry.source){continue}
        $path=Join-Path $workspace $entry.source
        if(-not [IO.File]::Exists($path) -or (Get-SourceHash $path) -ne $entry.sourceSha256){$entry.source}
    })
}
if($errorsFound.Count){$errorsFound;throw "Source staging validation failed: $($errorsFound.Count) structural/parse findings."}
Write-Output "PASS $($files.Count) staged files; forbidden files 0; required source areas present; PowerShell scripts parse."
if($CheckOriginalSources){Write-Output "Original-source drift since snapshot: $($sourceDrift.Count) files."; $sourceDrift}
Write-Output 'No build, network, publication, authentication, service or game operation performed.'
