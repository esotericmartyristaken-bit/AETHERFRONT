# Shared read-only baseline and audit checks. No service, publisher, or key setup
# script is sourced here. Every write helper is restricted to this RC26 worktree.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$RC26Work = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$RC26Workspace = [IO.Path]::GetDirectoryName($RC26Work)
$RC26Root = Join-Path $RC26Workspace 'KESAS-VPS-Server'
if ((Get-FileHash -LiteralPath "$RC26Root/Update.Common.ps1" -Algorithm SHA256).Hash -ine '52444126b6c4e0b845e6d4f895010ede22d727745f8cbaa495a41c21eaac79ba') {
    throw 'Native common helpers changed; review before using this pipeline.'
}
. "$RC26Root/Update.Common.ps1"
$RC26Utf8 = [Text.UTF8Encoding]::new($false)
$RC26CatalogRelative = 'GameData/LunaMultiplayer/Launcher/install-catalog.json'
function Get-RC26Hash([string]$Path) { (Get-FileHash -LiteralPath (Assert-UpdatePath $Path) -Algorithm SHA256).Hash.ToLowerInvariant() }
function ConvertTo-RC26Json($Value) { ConvertTo-Json -InputObject $Value -Depth 80 -Compress }
function Assert-RC26Hash([string]$Path, [string]$Expected) {
    if ($Expected -cnotmatch '^[a-f0-9]{64}$' -or (Get-RC26Hash $Path) -cne $Expected) { throw "Input hash mismatch: $Path" }
}
function Get-RC26Output([string]$Relative) { Get-UpdateChildPath $RC26Work $Relative }
function Write-RC26Json([string]$Path, $Value) {
    $target = Assert-UpdatePath $Path
    if (!$target.StartsWith($RC26Work + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Write escapes RC26 workspace.' }
    $stream = [IO.FileStream]::new($target, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $bytes = $RC26Utf8.GetBytes((ConvertTo-Json -InputObject $Value -Depth 80) + "`n"); $stream.Write($bytes); $stream.Flush($true) }
    finally { $stream.Dispose() }
}
function New-RC26Attempt([string]$Kind) {
    if ($Kind -cnotin @('Code','Release','Public','Live','Test')) { throw 'Invalid attempt kind.' }
    $name = "$Kind-$([Guid]::NewGuid().ToString('N'))"
    $pending = Get-RC26Output "BuildAttempts/.incomplete-$name"
    $complete = Get-RC26Output "BuildAttempts/$name"
    if ((Test-Path -LiteralPath $pending) -or (Test-Path -LiteralPath $complete)) { throw 'Attempt exists.' }
    New-UpdateDirectory $pending
    [pscustomobject]@{ pending = $pending; complete = $complete }
}
function Complete-RC26Attempt($Attempt) {
    $pending = Assert-UpdatePath $Attempt.pending; $complete = Assert-UpdatePath $Attempt.complete
    $attemptRoot = Get-RC26Output 'BuildAttempts'
    if ([IO.Path]::GetDirectoryName($pending) -ine $attemptRoot -or [IO.Path]::GetDirectoryName($complete) -ine $attemptRoot -or
        ![IO.Path]::GetFileName($pending).StartsWith('.incomplete-', [StringComparison]::Ordinal) -or (Test-Path -LiteralPath $complete)) { throw 'Invalid completion paths.' }
    Assert-UpdateTree $pending
    [IO.Directory]::Move($pending, $complete)
}
function Get-RC26SignedFeed([string]$FeedPath, [string]$ArchivePath, [string]$Version) {
    Assert-RC26Hash "$RC26Root/UpdateTool/ApfUpdateTool.exe" '215768322a2ff19b5388395531abd90e428bb6ac5b2ac932e9146411c76fad30'
    Invoke-UpdateTool $RC26Root @('--verify','--manifest',$FeedPath,'--archive',$ArchivePath,'--public-key-xml',"$RC26Root/secrets/update-signing-public.xml")
    $feed = Read-UpdateText $FeedPath | ConvertFrom-Json
    if ($feed.version -cne $Version) { throw "Unexpected signed feed version: $Version" }
    return $feed
}
function Get-RC26ZipJson([string]$ArchivePath, $Record) {
    $zip = [IO.Compression.ZipFile]::OpenRead((Assert-UpdatePath $ArchivePath))
    try {
        $entries = @($zip.Entries | Where-Object FullName -CEQ $Record.path)
        if ($entries.Count -ne 1 -or $entries[0].Length -ne $Record.sizeBytes -or $entries[0].Length -gt 16MB) { throw 'Signed JSON entry missing, duplicated or oversized.' }
        $inputStream = $entries[0].Open(); $buffer = [IO.MemoryStream]::new()
        try { $inputStream.CopyTo($buffer); $bytes = $buffer.ToArray() } finally { $inputStream.Dispose(); $buffer.Dispose() }
        if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant() -cne $Record.sha256) { throw 'Signed JSON entry hash mismatch.' }
        return ($RC26Utf8.GetString($bytes) | ConvertFrom-Json)
    } finally { $zip.Dispose() }
}
function Get-RC26Baseline {
    $feedPath = "$RC26Root/Updates/APFClientUpdate.json"
    $archive = "$RC26Root/Updates/kesas-rc25/APFClient-kesas-rc25.zip"
    Assert-RC26Hash $feedPath 'f249dc0712ad4599ea74d9b213283321024808ead3884f98ec1c9e41e714d22f'
    Assert-RC26Hash "$RC26Root/ClientTemplate.zip" '765d250c0beea6a530c96426de8aaa457eb0a3591419e9ca7389be3e46fcd465'
    Assert-RC26Hash "$RC26Root/Server/Config/APFPack.json" '5f2b3c55cf15b0a65ed570604ebbaa9ac28bc028acf40c153f44e71fe9d4b299'
    $feed = Get-RC26SignedFeed $feedPath $archive 'kesas-rc25'
    if (@($feed.files).Count -ne 464) { throw 'Reviewed RC25 signed inventory must contain exactly 464 files.' }
    $record = @($feed.files | Where-Object path -CEQ $RC26CatalogRelative)
    if ($record.Count -ne 1) { throw 'Signed RC25 catalog missing.' }
    $catalog = Get-RC26ZipJson $archive $record[0]
    if ($catalog.version -cne 'kesas-rc25' -or @($catalog.full.managedPackages).Count -ne 211 -or
        @($catalog.potato.managedPackages).Count -ne 177 -or !$catalog.gitHub.enabled) { throw 'Unexpected RC25 profiles/catalog.' }
    $pack = Read-UpdateText "$RC26Root/Server/Config/APFPack.json" | ConvertFrom-Json
    if ($pack.packVersion -cne 'kesas-rc25' -or $pack.requiredClientVersion -cne 'kesas-rc25' -or @($pack.managedPackages).Count -ne 0) { throw 'Unexpected server baseline; never force Full packages on Potato.' }
    [pscustomobject]@{ feedPath = $feedPath; archive = $archive; feed = $feed; catalog = $catalog; pack = $pack }
}
function Get-RC26RepairIds {
    @('kesas-contractconfigurator-3179517d','kesas-kerbalengineer-f43a5d95','kesas-kspcommunityfixes-06405f0a','kesas-zzz-kesasperformancefixes-c43001de')
}
function Get-RC26VersionCatalog($Catalog, $AssetDeclarations = @()) {
    $next = ConvertTo-RC26Json $Catalog | ConvertFrom-Json
    $next.version = 'kesas-rc26'
    foreach ($name in @('full','potato')) {
        if ($next.$name.packVersion -cne 'kesas-rc25' -or $next.$name.requiredClientVersion -cne 'kesas-rc25') { throw 'Invalid baseline profile version.' }
        $next.$name.packVersion = 'kesas-rc26'; $next.$name.requiredClientVersion = 'kesas-rc26'
        # RC25's already-loaded post-exit helper verifies before Engine26 can
        # take over. Force these four clean cached archives through replacement
        # recovery; changing only the client DLLs would repeat the rollback.
        foreach ($id in Get-RC26RepairIds) {
            $package = @($next.$name.managedPackages | Where-Object id -CEQ $id)
            if ($package.Count -ne 1 -or $package[0].version -cnotmatch '^[A-Za-z0-9._-]{1,55}$') { throw "Missing/invalid repair package: $name / $id" }
            $package[0].version = $package[0].version + '-repair26'
        }
        foreach ($declaration in $AssetDeclarations) {
            $package = @($next.$name.managedPackages | Where-Object id -CEQ $declaration.id)
            if ($package.Count -ne 1) { throw "Asset package missing/duplicated: $name / $($declaration.id)" }
            foreach ($property in @('version','archiveUrl','sha256','sizeBytes')) { $package[0].$property = $declaration.$property }
            if ((ConvertTo-RC26Json $package[0]) -cne (ConvertTo-RC26Json $declaration)) { throw 'Asset declaration changed a package identity, install root or policy field.' }
        }
    }
    return $next
}
function Assert-RC26TreeInventory([string]$Directory, $Files) {
    Assert-UpdateTree $Directory
    $expected = @{}
    foreach ($record in $Files) {
        $null = Get-UpdateRelativePath $record.path
        if ($expected.ContainsKey($record.path)) { throw 'Duplicate signed path.' }
        $expected[$record.path] = $record
        $path = Get-UpdateChildPath $Directory $record.path
        Assert-RC26Hash $path $record.sha256
        if (([IO.FileInfo]$path).Length -ne $record.sizeBytes) { throw 'Signed file size mismatch.' }
    }
    if (@(Get-ChildItem -LiteralPath $Directory -Force -Recurse -File).Count -ne $expected.Count) { throw 'Candidate has unexpected or missing files.' }
}
function Get-RC26EngineSources {
    Assert-UpdateTree "$RC26Work/Engine"
    $files = @(Get-ChildItem -LiteralPath "$RC26Work/Engine" -File -Recurse -Force | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' -and $_.Extension -in @('.cs','.csproj','.props','.targets') })
    $files += @(foreach ($name in @('Transfer.cs','DownloadFiles.cs','GitHub.cs')) { Get-Item -LiteralPath "$RC26Workspace/Aetherfront-RC22-Work/Engine/$name" })
    @(foreach ($file in $files | Sort-Object FullName) { [ordered]@{ path = $file.FullName; sha256 = Get-RC26Hash $file.FullName } })
}
function Get-RC26Media($Catalog) {
    foreach ($profile in @('full','potato')) {
        $ids = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($package in $Catalog.$profile.managedPackages) {
            if (!$ids.Add($package.id) -or $package.sha256 -cnotmatch '^[a-f0-9]{64}$' -or $package.sizeBytes -le 0) { throw 'Invalid/duplicate package declaration.' }
            $uri = [uri]$package.archiveUrl
            $name = [uri]::UnescapeDataString(($uri.AbsolutePath -split '/')[-1])
            $null = Get-UpdateRelativePath $name
            if (!$uri.IsAbsoluteUri -or $uri.Scheme -cne 'https' -or $uri.UserInfo -or $uri.Query -or $uri.Fragment -or $name.Contains('/') -or !$name.EndsWith('.zip', [StringComparison]::Ordinal)) { throw 'Unsafe media URL.' }
            [pscustomobject]@{ name = $name; sha256 = $package.sha256; sizeBytes = $package.sizeBytes }
        }
    }
}
function Get-RC26ZipInventory([string]$ArchivePath) {
    $zip = [IO.Compression.ZipFile]::OpenRead((Assert-UpdatePath $ArchivePath))
    $files = @{}; $seen = @{}; [long]$expanded = 0
    try {
        if ($zip.Entries.Count -gt 20000 -or ([IO.FileInfo]$ArchivePath).Length -gt 2GB) { throw 'Asset ZIP exceeds the reviewed archive bounds.' }
        foreach ($entry in $zip.Entries) {
            $directory = $entry.FullName.EndsWith('/',[StringComparison]::Ordinal)
            $name = if ($directory) { $entry.FullName.Substring(0,$entry.FullName.Length - 1) } else { $entry.FullName }
            $null = Get-UpdateRelativePath $name
            if ($seen.ContainsKey($name) -or ($entry.ExternalAttributes -shr 16 -band 0xF000) -eq 0xA000 -or ($entry.ExternalAttributes -band 0x400)) { throw 'Duplicate/linked asset ZIP entry.' }
            $seen[$name] = $directory; $expanded += $entry.Length
            if ($expanded -gt 2GB -or ($directory -and $entry.Length -ne 0)) { throw 'Invalid expanded asset ZIP size.' }
            if ($directory) { continue }
            $inputStream = $entry.Open()
            try { $sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($inputStream)).ToLowerInvariant() }
            finally { $inputStream.Dispose() }
            $files[$name] = [pscustomobject]@{ path = $name; sha256 = $sha256; sizeBytes = $entry.Length
                lastWriteTime = $entry.LastWriteTime.ToString('o'); externalAttributes = $entry.ExternalAttributes }
        }
        foreach ($name in $seen.Keys) {
            $parent = $name
            while ($parent.Contains('/')) {
                $parent = $parent.Substring(0,$parent.LastIndexOf('/'))
                if ($seen.ContainsKey($parent) -and !$seen[$parent]) { throw 'Asset ZIP file/directory collision.' }
            }
        }
    } finally { $zip.Dispose() }
    return $files
}
function Assert-RC26AssetZipDelta([string]$OldArchive,[string]$NewArchive,[string]$PackageId,$Aliases,[string]$AddedConfigPath,[string]$AddedConfigSha256) {
    $old = Get-RC26ZipInventory $OldArchive
    $next = Get-RC26ZipInventory $NewArchive
    foreach ($name in $old.Keys) {
        if (!$next.ContainsKey($name) -or (ConvertTo-RC26Json $old[$name]) -cne (ConvertTo-RC26Json $next[$name])) { throw "Existing archive entry changed/removed: $PackageId / $name" }
    }
    $added = @($next.Keys | Where-Object { !$old.ContainsKey($_) } | Sort-Object)
    $expectedCounts = @{ 'kesas-modularlaunchpads-8feabdb7' = 2; 'kesas-umbraspaceindustries-2ed9115b' = 2
        'kesas-tundraspacecenter-7018b381' = 1; 'kesas-interstellartechnologies-173d8c64' = 2 }
    if ($PackageId -ceq 'kesas-rc25-modfixes') {
        $null = Get-UpdateRelativePath $AddedConfigPath
        if ($added.Count -ne 1 -or $added[0] -cne $AddedConfigPath -or $AddedConfigPath -cnotmatch '^ZZZ_KESASModFixes/[^/]+\.cfg$' -or
            $AddedConfigSha256 -cnotmatch '^[a-f0-9]{64}$' -or $next[$AddedConfigPath].sha256 -cne $AddedConfigSha256 -or @($Aliases).Count -ne 0) { throw 'Common asset archive may add only the reviewed KK binding config.' }
        if (@($old.Keys | Where-Object { $_ -cmatch '(^|/)OptionalVisualModules\.cfg$' }).Count -ne 1) { throw 'Existing OptionalVisualModules.cfg must remain byte-identical.' }
        return
    }
    if (!$expectedCounts.ContainsKey($PackageId) -or $added.Count -ne $expectedCounts[$PackageId] -or @($Aliases).Count -ne $added.Count -or $AddedConfigPath) { throw 'Unexpected asset package or alias count.' }
    $approved = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($alias in $Aliases) {
        $null = Get-UpdateRelativePath $alias.path; $null = Get-UpdateRelativePath $alias.sourcePath
        if (!$approved.Add($alias.path) -or $alias.path -cnotin $added -or !$old.ContainsKey($alias.sourcePath) -or
            $alias.path -cnotmatch '\.(dds|png|tga|mbm)$' -or $alias.sourcePath -cnotmatch '\.(dds|png|tga|mbm)$' -or $next[$alias.path].sha256 -cne $old[$alias.sourcePath].sha256 -or
            $next[$alias.path].sizeBytes -ne $old[$alias.sourcePath].sizeBytes) { throw 'Texture alias is not a byte-identical approved existing asset.' }
    }
}
function Get-RC26AssetStage([string]$Stage,[string]$PlanName,[string]$PlanSha256,[string]$TestResultPath,[string]$TestResultSha256,$BaselineCatalog) {
    $Stage = (Assert-UpdatePath $Stage).TrimEnd('\')
    if (!$Stage.StartsWith((Get-RC26Output 'Assets') + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Asset stage must be inside the owned RC26 Assets directory.' }
    Assert-UpdateTree $Stage
    $planPath = Get-UpdateChildPath $Stage $PlanName
    Assert-RC26Hash $planPath $PlanSha256
    Assert-RC26Hash $TestResultPath $TestResultSha256
    $test = Read-UpdateText $TestResultPath | ConvertFrom-Json
    if (!$test.passed) { throw 'Passing asset-test evidence required.' }
    $plan = Read-UpdateText $planPath | ConvertFrom-Json
    if ($plan.schemaVersion -ne 1 -or $plan.version -cne 'kesas-rc26' -or @($plan.media).Count -ne 5 -or $plan.signed -or
        !$plan.requiresReleaseOwnerSignature -or $plan.published -or $plan.installed -or $test.planSha256 -cne $PlanSha256 -or
        $test.sourceCatalogSha256 -cne $plan.sourceCatalogSha256 -or $test.packagesVerified -ne 5 -or $test.aliasesVerified -ne 7 -or
        $test.preciseBindingsVerified -ne 8 -or !$test.originalZipPrefixesPreserved -or !$test.originalCentralDirectoryRecordsPreserved -or
        !$test.bothProfilesUseIdenticalSharedVariants -or !$test.untouchedPackagesPreserved) { throw 'Exactly five reviewed asset archives and their complete passing evidence required.' }
    $catalogRecord = @((Read-UpdateText "$RC26Root/Updates/APFClientUpdate.json" | ConvertFrom-Json).files | Where-Object path -CEQ $RC26CatalogRelative)
    if ($catalogRecord.Count -ne 1 -or $plan.sourceCatalogSha256 -cne $catalogRecord[0].sha256) { throw 'Asset plan was not prepared from the signed active RC25 catalog.' }
    $allowed = @('kesas-modularlaunchpads-8feabdb7','kesas-umbraspaceindustries-2ed9115b','kesas-tundraspacecenter-7018b381','kesas-interstellartechnologies-173d8c64','kesas-rc25-modfixes')
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $declarations = @(); $files = @()
    $deployment = Get-UpdateDeployment $RC26Root
    foreach ($item in $plan.media) {
        $package = $item.declaration
        if ($package.id -cnotin $allowed -or !$seen.Add($package.id) -or $package.sha256 -cnotmatch '^[a-f0-9]{64}$' -or
            $package.version -cnotmatch '^[A-Za-z0-9._-]{1,64}$' -or $package.sizeBytes -le 0) { throw 'Unexpected/duplicate asset package or invalid version/hash/size.' }
        $old = @($BaselineCatalog.full.managedPackages | Where-Object id -CEQ $package.id)
        $potato = @($BaselineCatalog.potato.managedPackages | Where-Object id -CEQ $package.id)
        if ($old.Count -ne 1 -or $potato.Count -ne 1 -or (ConvertTo-RC26Json $old[0]) -cne (ConvertTo-RC26Json $potato[0]) -or $package.sha256 -ceq $old[0].sha256) { throw 'Asset baseline differs across profiles or archive was not replaced.' }
        if ((ConvertTo-RC26Json $item.oldDeclaration) -cne (ConvertTo-RC26Json $old[0]) -or $item.oldSha256 -cne $old[0].sha256 -or
            (ConvertTo-RC26Json @($item.profiles | Sort-Object)) -cne (ConvertTo-RC26Json @('full','potato'))) { throw 'Asset plan baseline/profile mapping mismatch.' }
        $expected = ConvertTo-RC26Json $old[0] | ConvertFrom-Json
        foreach ($property in @('version','archiveUrl','sha256','sizeBytes')) { $expected.$property = $package.$property }
        if ((ConvertTo-RC26Json $expected) -cne (ConvertTo-RC26Json $package)) { throw 'Asset package changed install root, identity or policy.' }
        $uri = [uri]$package.archiveUrl
        $name = [uri]::UnescapeDataString(($uri.AbsolutePath -split '/')[-1])
        $null = Get-UpdateRelativePath $name
        if ($name -cnotmatch ('^kesas-rc26-[a-z0-9-]+-' + [regex]::Escape($package.sha256) + '\.zip$') -or
            $package.archiveUrl -cne "$($deployment.packOrigin)/$name") { throw 'New media requires the unchanged pinned origin and a full-SHA256 content-addressed RC26 filename.' }
        $source = Get-UpdateChildPath $Stage "Media/$name"
        Assert-RC26Hash $source $package.sha256
        if (([IO.FileInfo]$source).Length -ne $package.sizeBytes) { throw 'Staged asset size mismatch.' }
        $oldName = [uri]::UnescapeDataString((([uri]$old[0].archiveUrl).AbsolutePath -split '/')[-1])
        $oldArchive = Get-UpdateChildPath $RC26Root "Server/Config/$oldName"
        Assert-RC26Hash $oldArchive $old[0].sha256
        $oldInventory = Get-RC26ZipInventory $oldArchive
        $nextInventory = Get-RC26ZipInventory $source
        $added = @($nextInventory.Keys | Where-Object { !$oldInventory.ContainsKey($_) })
        if ($package.id -ceq 'kesas-rc25-modfixes') {
            $configPath = 'ZZZ_KESASModFixes/StaticTextureBindings.cfg'
            if ($added.Count -ne 1 -or $added[0] -cne $configPath) { throw 'Only the reviewed KK texture binding config may be added to the common archive.' }
            Assert-RC26AssetZipDelta $oldArchive $source $package.id @() $configPath $nextInventory[$configPath].sha256
            $record = [pscustomobject]@{ path = $configPath; sizeBytes = $nextInventory[$configPath].sizeBytes; sha256 = $nextInventory[$configPath].sha256 }
            $zip = [IO.Compression.ZipFile]::OpenRead($source)
            try { $reader = [IO.StreamReader]::new($zip.GetEntry($configPath).Open()); try { $text = $reader.ReadToEnd() } finally { $reader.Dispose() } }
            finally { $zip.Dispose() }
            $bindingRecord = @($plan.sourceFiles | Where-Object path -CEQ 'Assets/Source/ZZZ_KESASModFixes/StaticTextureBindings.cfg')
            if ($bindingRecord.Count -ne 1 -or $record.sha256 -cne $bindingRecord[0].sha256) { throw 'Binding config differs from the reviewed plan source bytes.' }
            $targets = [regex]::Matches($text,'(?m)^\s*@STATIC\[([A-Za-z0-9_-]+)\]:HAS\[#(OSSMAT|TSCMAT)\[1\]\]:NEEDS\[KerbalKonstructs\]:FINAL\s*$')
            $transforms = [regex]::Matches($text,'(?m)^\s*transforms\s*=\s*([A-Za-z0-9_-]+)\s*$')
            $textures = [regex]::Matches($text,'(?m)^\s*_MainTex\s*=\s*(BUILTIN:/[A-Za-z0-9_-]+)\s*$')
            $advanced = [regex]::Matches($text,'(?m)^\s*name\s*=\s*AdvancedTextures\s*$')
            if ($record.sizeBytes -gt 64KB -or $text -match ':FOR\s*\[' -or $targets.Count -ne 8 -or $transforms.Count -ne 8 -or
                $textures.Count -ne 8 -or $advanced.Count -ne 8 -or @($plan.bindings).Count -ne 8) { throw 'Common config must contain exactly eight precise KK bindings and no fake FOR markers.' }
            $bindingKeys = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            for ($index = 0; $index -lt 8; $index++) {
                $binding = $plan.bindings[$index]
                if ($targets[$index].Groups[1].Value -cne $binding.staticName -or $targets[$index].Groups[2].Value -cne $binding.marker -or
                    $transforms[$index].Groups[1].Value -cne $binding.transform -or $textures[$index].Groups[1].Value -cne $binding.mainTexture -or
                    !$bindingKeys.Add("$($binding.staticName)/$($binding.transform)")) { throw 'KK binding target/material differs from the exact reviewed plan.' }
            }
        } else {
            $aliases = @(foreach ($entry in $item.addedEntries) {
                if ($entry.kind -cne 'byte-identical-alias' -or $entry.addedEntry -cnotin $added -or
                    !$nextInventory.ContainsKey($entry.addedEntry) -or $entry.sha256 -cne $nextInventory[$entry.addedEntry].sha256 -or
                    $entry.sizeBytes -ne $nextInventory[$entry.addedEntry].sizeBytes) { throw 'Texture alias differs from the exact approved plan.' }
                [pscustomobject]@{ path = $entry.addedEntry; sourcePath = $entry.sourceEntry }
            })
            Assert-RC26AssetZipDelta $oldArchive $source $package.id $aliases '' ''
        }
        $declarations += $package
        $files += [pscustomobject]@{ path = "Media/$name"; source = $source; destination = "Server/Config/$name"; sha256 = $package.sha256; sizeBytes = $package.sizeBytes }
    }
    if (@(Get-ChildItem -LiteralPath (Get-UpdateChildPath $Stage 'Media') -Force -Recurse -File).Count -ne 5) { throw 'Unexpected staged media file.' }
    $inventory = @(foreach ($file in Get-ChildItem -LiteralPath $Stage -Force -Recurse -File | Sort-Object FullName) {
        [ordered]@{ path = $file.FullName.Substring($Stage.Length + 1).Replace('\','/'); sha256 = Get-RC26Hash $file.FullName }
    })
    [pscustomobject]@{ directory = $Stage; planPath = $planPath; planSha256 = $PlanSha256; testResultPath = Assert-UpdatePath $TestResultPath
        testResultSha256 = $TestResultSha256; declarations = $declarations; files = $files; inventory = $inventory }
}
function Get-RC26HistoricalMedia {
    $expected = @{}; $versions = @()
    foreach ($directory in Get-ChildItem -LiteralPath "$RC26Root/Updates" -Directory | Where-Object Name -CMatch '^kesas-rc[1-9][0-9]*$' | Sort-Object Name) {
        Assert-UpdateTree $directory.FullName
        $name = $directory.Name
        $feed = Get-RC26SignedFeed "$($directory.FullName)/APFClientUpdate.json" "$($directory.FullName)/APFClient-$name.zip" $name
        $versions += $name
        $record = @($feed.files | Where-Object path -CEQ $RC26CatalogRelative)
        if (!$record.Count) { continue } # Early releases predate the signed catalog.
        if ($record.Count -ne 1) { throw 'Duplicate historical signed catalog.' }
        $catalog = Get-RC26ZipJson "$($directory.FullName)/APFClient-$name.zip" $record[0]
        foreach ($asset in Get-RC26Media $catalog) {
            if ($expected.ContainsKey($asset.name) -and (ConvertTo-RC26Json $expected[$asset.name]) -cne (ConvertTo-RC26Json $asset)) { throw 'Historical content-addressed media conflict.' }
            $expected[$asset.name] = $asset
        }
    }
    [pscustomobject]@{ versions = $versions; media = @($expected.Values | Sort-Object name) }
}
function Get-RC26ReviewedCode([string]$ResultPath) {
    $ResultPath = Assert-UpdatePath $ResultPath
    $directory = [IO.Path]::GetDirectoryName($ResultPath)
    if ([IO.Path]::GetDirectoryName($directory) -ine (Get-RC26Output 'BuildAttempts') -or
        [IO.Path]::GetFileName($directory) -cnotmatch '^Code-[a-f0-9]{32}$' -or [IO.Path]::GetFileName($ResultPath) -cne 'CODE-RESULT.json') { throw 'Use a completed RC26 Code attempt.' }
    $code = Read-UpdateText $ResultPath | ConvertFrom-Json
    if ($code.schemaVersion -ne 1 -or !$code.completed -or !$code.engineBuilt -or !$code.coreScanGrafted -or $code.version -cne 'kesas-rc26' -or
        $code.baselineFeedSha256 -cne 'f249dc0712ad4599ea74d9b213283321024808ead3884f98ec1c9e41e714d22f' -or $code.signedBaselineFiles -ne 464) { throw 'Code report is not a complete frozen Engine/core/client build.' }
    $expected = @('Patched/ApfCore.dll','Patched/LmpClient.dll','Patched/audit.json','Engine/KesasInstall.dll')
    if ((ConvertTo-RC26Json @($code.outputs.path | Sort-Object)) -cne (ConvertTo-RC26Json @($expected | Sort-Object))) { throw 'Unexpected code output inventory.' }
    foreach ($file in @($code.outputs) + @($code.toolFiles)) { Assert-RC26Hash (Get-UpdateChildPath $directory $file.path) $file.sha256 }
    foreach ($file in $code.patcherSources) { Assert-RC26Hash $file.path $file.sha256 }
    if ((ConvertTo-RC26Json @(Get-RC26EngineSources)) -cne (ConvertTo-RC26Json $code.engineSources)) { throw 'Engine/linked sources changed after the frozen build.' }
    $baseline = Get-RC26Baseline
    $savedManaged = [Environment]::GetEnvironmentVariable('APF_RC26_UNITY_MANAGED','Process')
    try {
        [Environment]::SetEnvironmentVariable('APF_RC26_UNITY_MANAGED',$code.unityManaged,'Process')
        & "$RC26Workspace/tools/dotnet-sdk8/dotnet.exe" "$directory/Tool/VersionPatcher.dll" review $baseline.feedPath $baseline.archive `
            "$RC26Root/secrets/update-signing-public.xml" "$RC26Root/UpdateTool/ApfUpdateTool.exe" `
            "$directory/Baseline/GameData/LunaMultiplayer/Plugins/ApfCore.dll" "$directory/Baseline/GameData/LunaMultiplayer/Plugins/LmpClient.dll" "$directory/Patched" "$directory/Engine/KesasInstall.dll" | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'Recomputed core/client graft review failed.' }
    } finally { [Environment]::SetEnvironmentVariable('APF_RC26_UNITY_MANAGED',$savedManaged,'Process') }
    $audit = Read-UpdateText "$directory/Patched/audit.json" | ConvertFrom-Json
    if (!$audit.finalReleaseEligible -or !$audit.coreScanCallsiteOnly -or !$audit.preparedBuildFileStateUnchanged -or !$audit.allOtherMethodsUnchanged -or
        $audit.engineSha256 -cne (Get-RC26Hash "$directory/Engine/KesasInstall.dll")) { throw 'Release graft evidence is incomplete.' }
    [pscustomobject]@{ directory = $directory; report = $code; audit = $audit; resultPath = $ResultPath }
}
function Get-RC26ReviewedRelease([string]$ResultPath) {
    $ResultPath = Assert-UpdatePath $ResultPath
    $directory = [IO.Path]::GetDirectoryName($ResultPath)
    if ([IO.Path]::GetDirectoryName($directory) -ine (Get-RC26Output 'BuildAttempts') -or
        [IO.Path]::GetFileName($directory) -cnotmatch '^Release-[a-f0-9]{32}$' -or [IO.Path]::GetFileName($ResultPath) -cne 'BUILD-RESULT.json') { throw 'Use a completed RC26 Release attempt.' }
    $report = Read-UpdateText $ResultPath | ConvertFrom-Json
    if ($report.schemaVersion -ne 1 -or !$report.completed -or $report.version -cne 'kesas-rc26' -or $report.signedFiles -ne 464 -or
        $report.fullPackages -ne 211 -or $report.potatoPackages -ne 177 -or $report.newMediaCount -ne 5 -or
        $report.purpose -cne 'FinalRelease' -or $report.mediaMode -cne 'ReviewedAssets' -or !$report.exactNativePublisherBytesRequired) { throw 'Unexpected final release evidence; test-only/no-assets variants cannot verify as a final release.' }
    Assert-RC26Hash "$directory/Aetherfront-Release-kesas-rc26.zip" $report.releaseSha256
    Assert-RC26Hash "$directory/Candidate/Client/$RC26CatalogRelative" $report.catalogSha256
    Assert-RC26Hash "$directory/Release/APFPack-template.json" $report.packTemplateSha256
    Assert-RC26Hash $report.codeResultPath $report.codeResultSha256
    $feed = Get-RC26SignedFeed "$directory/Candidate/Published/APFClientUpdate.json" "$directory/Candidate/Published/APFClient-kesas-rc26.zip" 'kesas-rc26'
    if (@($feed.files).Count -ne 464) { throw 'Reviewed candidate signed inventory must contain 464 files.' }
    Assert-RC26TreeInventory "$directory/Candidate/Client" $feed.files
    [pscustomobject]@{ directory = $directory; report = $report; feed = $feed }
}
