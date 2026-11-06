#requires -Version 7.4
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$workspace=Split-Path $repo
$evidence=Join-Path $workspace 'UpdateBuilds/RC49-Recovery-20261106'
$utf8=[Text.UTF8Encoding]::new($false)
Push-Location $repo
try {
    $selected=@(@(& git diff --name-only)+@(& git ls-files --others --exclude-standard)|Sort-Object -Unique|Where-Object {$_ -and $_ -notin @('SOURCE-MANIFEST.json','PRIVACY-SCAN.json')})
    foreach($relative in $selected){
        if($relative -match '(^|/)(bin|obj|secrets|Universe|state)/|\.(dll|exe|zip|pfx|pem|key|log)$'){throw 'Non-source selected'}
        if(-not [IO.File]::Exists((Join-Path $repo $relative))){throw 'Deleted source requires explicit manifest review'}
    }
    $role='Reviewed RC49 recovery capture, startup packaging, personal operator command and isolated regression source'
    $policy=Get-Content export-policy.json -Raw|ConvertFrom-Json
    $known=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach($row in $policy.explicitFiles){$null=$known.Add($row.source)}
    foreach($relative in $selected){
        if(($relative.StartsWith('Aetherfront-RC49-Work/') -or $relative -in @('Aetherfront-RC43-Work/Client/RecoverySubjects.cs','Aetherfront-RC43-Work/Server/OperatorScienceCommands.cs')) -and $known.Add($relative)){
            $policy.explicitFiles+=@{source=$relative;role=$role}
        }
    }
    [IO.File]::WriteAllText((Join-Path $repo 'export-policy.json'),(($policy|ConvertTo-Json -Depth 30)+"`n").Replace("`r`n","`n"),$utf8)
    $source=Join-Path $evidence ('PrivacySources-'+[Guid]::NewGuid().ToString('N'))
    $null=[IO.Directory]::CreateDirectory($source)
    foreach($relative in @($selected+'export-policy.json'|Sort-Object -Unique)){
        $target=Join-Path $source $relative
        $null=[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
        Copy-Item -LiteralPath (Join-Path $repo $relative) -Destination $target
    }
    $ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $repo 'scripts/Export-GitHubSource.ps1'),[ref]$null,[ref]$null)
    foreach($function in $ast.FindAll({param($node)$node -is [Management.Automation.Language.FunctionDefinitionAst]},$false)){
        if($function.Name -in @('Walk','Scan')){. ([scriptblock]::Create($function.Extent.Text))}
    }
    $report=Scan $source
    [IO.File]::WriteAllText((Join-Path $evidence 'CHANGED-SOURCE-PRIVACY.json'),($report|ConvertTo-Json -Depth 12),$utf8)
    [pscustomobject]$report|Select-Object filesScanned,findingCount,findings|ConvertTo-Json -Depth 6
    if($report.findingCount){throw 'Changed-source privacy findings require review'}
    $privacy=Get-Content PRIVACY-SCAN.json -Raw|ConvertFrom-Json
    $privacy|Add-Member -NotePropertyName incrementalRC49 -NotePropertyValue $report -Force
    [IO.File]::WriteAllText((Join-Path $repo 'PRIVACY-SCAN.json'),(($privacy|ConvertTo-Json -Depth 30)+"`n").Replace("`r`n","`n"),$utf8)
    $committed=(& git show HEAD:SOURCE-MANIFEST.json) -join "`n"
    if($LASTEXITCODE -ne 0){throw 'Cannot read committed source manifest'}
    $manifest=$committed|ConvertFrom-Json
    $originalPaths=@($manifest.files.path)
    $rows=@{};foreach($row in $manifest.files){$rows[$row.path]=$row}
    foreach($relative in @($selected+'export-policy.json'|Sort-Object -Unique)){
        if($relative -in @('SOURCE-MANIFEST.json','PRIVACY-SCAN.json') -or ($relative -in $policy.metadataFiles -and -not $rows.ContainsKey($relative))){continue}
        $full=Join-Path $repo $relative
        $sha=(Get-FileHash -LiteralPath $full).Hash.ToLowerInvariant();$bytes=(Get-Item -LiteralPath $full).Length
        if($rows.ContainsKey($relative)){$rows[$relative].sha256=$sha;$rows[$relative].bytes=$bytes}
        else{$rows[$relative]=[ordered]@{path=$relative;source=$null;role=$role;bytes=$bytes;sha256=$sha;sourceSha256=$null;transformations=@('Maintained directly in the repository; reviewed source addition only.','Production state, credentials and licensed runtime references excluded.')}}
    }
    $manifest.files=@(@(foreach($relative in $originalPaths){$rows[$relative]})+@($rows.Values|Where-Object {$_.path -notin $originalPaths}|Sort-Object path))
    $manifest.fileCount=$manifest.files.Count
    $manifest.totalFileBytes=($manifest.files|Measure-Object -Property bytes -Sum).Sum
    [IO.File]::WriteAllText((Join-Path $repo 'SOURCE-MANIFEST.json'),(($manifest|ConvertTo-Json -Depth 30)+"`n").Replace("`r`n","`n"),$utf8)
    "Updated $($selected.Count) selected source paths; manifest contains $($manifest.fileCount) records. No publication performed."
} finally {Pop-Location}
