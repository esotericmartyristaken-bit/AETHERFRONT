#requires -Version 7.4
param([Parameter(Mandatory)][string]$Archive,[Parameter(Mandatory)][string]$Feed)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
foreach($path in @($Archive,$Feed)){
    for($cursor=[IO.Path]::GetFullPath($path);$cursor;$cursor=[IO.Path]::GetDirectoryName($cursor)){
        if([IO.File]::GetAttributes($cursor) -band [IO.FileAttributes]::ReparsePoint){throw 'Unsafe client inventory path'}
    }
}
$manifest=Get-Content -LiteralPath $Feed -Raw|ConvertFrom-Json
if((Get-FileHash -LiteralPath $Archive).Hash -ine $manifest.sha256){throw 'Client archive changed after publication'}
$zip=[IO.Compression.ZipFile]::OpenRead($Archive)
try {
    $groups=@{clientUpdater=[Collections.Generic.List[object]]::new();clientHelper=[Collections.Generic.List[object]]::new()}
    $catalog=$null
    foreach($file in $manifest.files){
        $name=[string]$file.path
        $family=if($name -imatch '^GameData/LunaMultiplayer/Plugins/(LmpClient|ApfCore|AetherfrontCore)\.dll$'){'clientUpdater'}elseif($name -imatch '^GameData/LunaMultiplayer/Plugins/KesasInstall\.dll$|^KESAS/.*\.(exe|dll|bin)$'){'clientHelper'}else{$null}
        $isCatalog=$name -ieq 'GameData/LunaMultiplayer/Launcher/install-catalog.json'
        if(-not $family -and -not $isCatalog){continue}
        $entry=$zip.GetEntry($name)
        if(-not $entry -or $entry.Length -gt 512MB -or $entry.Length -ne $file.sizeBytes){throw 'Client component inventory mismatch'}
        $stream=$entry.Open();$sha=[Security.Cryptography.SHA256]::Create()
        try{$actual=[Convert]::ToHexString($sha.ComputeHash($stream)).ToLowerInvariant()}finally{$sha.Dispose();$stream.Dispose()}
        if($actual -cne $file.sha256){throw 'Client component hash mismatch'}
        if($isCatalog){$catalog=$actual}else{$groups[$family].Add([ordered]@{path=$name;sha256=$actual;sizeBytes=$entry.Length})}
    }
    if($groups.clientUpdater.Count -ne 3 -or $groups.clientHelper.Count -lt 1 -or -not $catalog){throw 'Required client components are absent'}
    $result=[ordered]@{catalog=$catalog}
    foreach($family in @('clientUpdater','clientHelper')){
        $bytes=[Text.Encoding]::UTF8.GetBytes(($groups[$family]|Sort-Object path|ConvertTo-Json -Compress -Depth 4))
        $result[$family]=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
    }
    $result|ConvertTo-Json -Compress
}finally{$zip.Dispose()}
