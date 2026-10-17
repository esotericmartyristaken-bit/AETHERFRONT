#requires -Version 7.0
[CmdletBinding()]
param([string]$Stage = (Join-Path $PSScriptRoot 'ModFixes/Staged-RC25'))
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$stagePath = (Resolve-Path -LiteralPath $Stage).Path
$work = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'ModFixes'))
if (!$stagePath.StartsWith($work + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Tests accept only isolated mod-fix staging.' }
$plan = Get-Content -LiteralPath (Join-Path $stagePath 'MODFIX-PLAN.json') -Raw | ConvertFrom-Json
Add-Type -Path $plan.patchTool.path
. (Join-Path $work 'Patch-Waterfall.ps1')
$base = Get-Content -LiteralPath $plan.baseline.catalog -Raw | ConvertFrom-Json
$next = Get-Content -LiteralPath (Join-Path $stagePath 'install-catalog.json') -Raw | ConvertFrom-Json
$media = [IO.Path]::GetDirectoryName($plan.baseline.policyArchive)
$script:checks = 0
function Check([bool]$Condition, [string]$Message) {
    if (!$Condition) { throw "FAIL: $Message" }
    $script:checks++
}
function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function ByteHash([byte[]]$Bytes) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { [Convert]::ToHexString($sha.ComputeHash($Bytes)).ToLowerInvariant() } finally { $sha.Dispose() }
}
function Zip-Data([string]$Path) {
    $data = @{}
    $zip = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        foreach ($entry in $zip.Entries) {
            if (!$entry.Name) { continue }
            Check (!$data.ContainsKey($entry.FullName)) "No duplicate ZIP entry: $($entry.FullName)"
            $read = $entry.Open(); $dest = [IO.MemoryStream]::new()
            try { $read.CopyTo($dest); $data[$entry.FullName] = $dest.ToArray() } finally { $dest.Dispose(); $read.Dispose() }
        }
    } finally { $zip.Dispose() }
    return ,$data
}
function Package-Data($Package) {
    Zip-Data (Join-Path $media ([IO.Path]::GetFileName(([uri]$Package.archiveUrl).AbsolutePath)))
}
function Read-Assembly([byte[]]$Bytes) {
    $stream = [IO.MemoryStream]::new($Bytes, $false)
    $parameters = [Mono.Cecil.ReaderParameters]::new()
    $parameters.InMemory = $true
    $parameters.ReadingMode = [Mono.Cecil.ReadingMode]::Immediate
    # Deferred mode avoids resolving KSP attribute enum types on the build host.
    $parameters.ReadingMode = [Mono.Cecil.ReadingMode]::Deferred
    return [Mono.Cecil.AssemblyDefinition]::ReadAssembly($stream, $parameters)
}
function Read-ConfigTree([string]$Text) {
    $root = [pscustomobject]@{ Name = 'ROOT'; Values = [Collections.Generic.List[string]]::new(); Nodes = [Collections.Generic.List[object]]::new() }
    $stack = [Collections.Generic.Stack[object]]::new()
    $stack.Push($root); $pending = ''
    foreach ($match in [regex]::Matches(([regex]::Replace($Text, '(?m)//[^\r\n]*', '')), '[{}]|[^{}\r\n]+')) {
        $token = $match.Value.Trim()
        if (!$token) { continue }
        if ($token -ceq '{') {
            if (!$pending) { throw 'Config structural parser found unnamed node.' }
            $node = [pscustomobject]@{ Name = $pending; Values = [Collections.Generic.List[string]]::new(); Nodes = [Collections.Generic.List[object]]::new() }
            $stack.Peek().Nodes.Add($node); $stack.Push($node); $pending = ''
        } elseif ($token -ceq '}') {
            if ($pending -or $stack.Count -le 1) { throw 'Config structural parser found unbalanced node.' }
            [void]$stack.Pop()
        } elseif ($token.Contains('=')) {
            $stack.Peek().Values.Add($token)
        } else { $pending = $token }
    }
    if ($stack.Count -ne 1 -or $pending) { throw 'Config structural parser found incomplete tree.' }
    return $root
}
function All-ConfigNodes($Node) {
    $Node
    foreach ($child in $Node.Nodes) { All-ConfigNodes $child }
}
function Fuel-Modifiers($Tree) {
    foreach ($node in (All-ConfigNodes $Tree)) {
        if ($node.Name -ceq 'MODULE' -and @($node.Nodes | Where-Object { $_.Name -ceq 'IDENTIFIER' -and 'name = ModuleRCSFX' -cin $_.Values }).Count) {
            $node | ConvertTo-Json -Depth 40 -Compress
        }
    }
}
function Remove-RendererReferences($Node) {
    for ($i = $Node.Nodes.Count - 1; $i -ge 0; $i--) {
        $child = $Node.Nodes[$i]
        if ($child.Name -ceq 'MODULE' -and @($child.Nodes | Where-Object { $_.Name -ceq 'IDENTIFIER' -and 'name = ModuleWaterfallFX' -cin $_.Values }).Count) {
            $Node.Nodes.RemoveAt($i)
        } else { Remove-RendererReferences $child }
    }
}
Check ($next.version -ceq 'kesas-rc25' -and $plan.notActivated) 'RC25 staging remains inactive'
Check ((Hash $plan.baseline.catalog) -ceq $plan.baseline.catalogSha256) 'RC24 catalog unchanged'
Check ((Hash $plan.baseline.policyArchive) -ceq $plan.baseline.policySha256) 'RC24 policy archive unchanged'
foreach ($profile in @('full', 'potato')) {
    Check ($next.$profile.packVersion -ceq 'kesas-rc25' -and $next.$profile.requiredClientVersion -ceq 'kesas-rc25') "RC25 versions: $profile"
    Check (($base.$profile.protectedRoots -join ',') -ceq ($next.$profile.protectedRoots -join ',')) "Protected roots preserved: $profile"
    Check (@($next.$profile.managedPackages).Count -eq @($base.$profile.managedPackages).Count + 1) "Exactly one new package: $profile"
    foreach ($old in $base.$profile.managedPackages) {
        $matching = @($next.$profile.managedPackages | Where-Object id -CEQ $old.id)
        Check ($matching.Count -eq 1) "Package identity preserved: $profile/$($old.id)"
        if ($old.stripPrefix -ceq 'Waterfall' -or ($profile -ceq 'potato' -and $old.stripPrefix -ceq 'ZZZ_KESASPotato')) { continue }
        Check (($old | ConvertTo-Json -Depth 20 -Compress) -ceq ($matching[0] | ConvertTo-Json -Depth 20 -Compress)) "Unrelated declaration unchanged: $profile/$($old.stripPrefix)"
    }
}
$policyText = Get-Content -LiteralPath (Join-Path $work 'Source/ZZZ_KESASPotato/DisableExhaust.cfg') -Raw
$optionalText = Get-Content -LiteralPath (Join-Path $work 'Source/ZZZ_KESASModFixes/OptionalVisualModules.cfg') -Raw
Check ($policyText -notmatch ':FOR\[') 'Potato never advertises another mod'
Check ($policyText.Contains('!MODULE:HAS[@IDENTIFIER[ModuleWaterfallFX]] {}')) 'Only renderer B9 modifiers are removed'
Check ($policyText.Contains('!MODEL:HAS[#model[StockWaterfallEffects/*]] {}')) 'Stripped supplementary plume MODEL references removed'
Check ($policyText -notmatch '!MODULE\[(ModuleRCS|ModuleRCSFX|ModuleB9PartSwitch|PersistentEngine)\]') 'Physics and B9 switch modules preserved'
Check ($optionalText.Contains(':NEEDS[!EngineLightRelit,!EngineLight]')) 'Engine lighting cleanup requires both providers absent'
Check ($optionalText.Contains(':NEEDS[!KSPCommunityFixes]')) 'Ladder controller cleanup requires its actual provider absent'
# Structural regression on actual archived configs, not a replacement for an
# in-game ModuleManager run. Every fuel modifier must survive unchanged.
foreach ($case in @(
    @('FarFutureTechnologies', 'FarFutureTechnologies/Parts/Control/RCS/fft-rcs-arcjet-1.cfg'),
    @('FarFutureTechnologies', 'FarFutureTechnologies/Parts/Control/RCS/fft-rcs-arcjet-large-1.cfg'),
    @('NearFutureLaunchVehicles', 'NearFutureLaunchVehicles/Patches/NFLaunchVehiclesCryoTanksBipropRCS.cfg'),
    @('NearFutureLaunchVehicles', 'NearFutureLaunchVehicles/Patches/Waterfall/NFLaunchVehiclesWaterfallBipropRCS.cfg')
)) {
    $package = $base.full.managedPackages | Where-Object stripPrefix -CEQ $case[0]
    $files = Package-Data $package
    $tree = Read-ConfigTree ([Text.Encoding]::UTF8.GetString($files[$case[1]]))
    $fuelBefore = @(Fuel-Modifiers $tree) -join "`n"
    $visualBefore = @((All-ConfigNodes $tree) | Where-Object { $_.Name -ceq 'IDENTIFIER' -and 'name = ModuleWaterfallFX' -cin $_.Values }).Count
    Remove-RendererReferences $tree
    Check ($fuelBefore -ceq (@(Fuel-Modifiers $tree) -join "`n")) "Fuel modifiers, PROPELLANT, thrust and atmosphereCurve preserved: $($case[1])"
    Check (@((All-ConfigNodes $tree) | Where-Object { $_.Name -ceq 'IDENTIFIER' -and 'name = ModuleWaterfallFX' -cin $_.Values }).Count -eq 0) "No renderer modifier references remain: $($case[1])"
    if ($case[0] -ceq 'FarFutureTechnologies') {
        Check ($visualBefore -eq 2 -and @(Fuel-Modifiers $tree).Count -eq 2) 'FFT Lithium/Hydrogen modifiers retained; both dangling visual selectors removed'
    }
}
foreach ($profile in @('full', 'potato')) {
    $roots = @($base.$profile.managedPackages.stripPrefix)
    $removeEngineLight = 'EngineLightRelit' -cnotin $roots -and 'EngineLight' -cnotin $roots
    Check ($removeEngineLight -eq ($profile -ceq 'potato')) "Engine lighting removed only from absent-provider profile: $profile"
    Check ('KSPCommunityFixes' -cin $roots) "Ladder controller retained after Harmony repair: $profile"
}
foreach ($item in $plan.media) {
    Check ((Hash $item.path) -ceq $item.declaration.sha256) "Content-addressed media hash: $($item.declaration.id)"
    Check ((Get-Item -LiteralPath $item.path).Length -eq $item.declaration.sizeBytes) "Media byte size: $($item.declaration.id)"
    Check ([IO.Path]::GetFileName($item.path).Contains($item.declaration.sha256)) 'Media filename contains complete SHA256'
    $contents = Zip-Data $item.path
    $expanded = [long]0
    foreach ($key in $contents.Keys) {
        Check ($key.StartsWith($item.declaration.stripPrefix + '/', [StringComparison]::Ordinal)) 'Exact installer stripPrefix layout'
        $expanded += $contents[$key].Length
    }
    Check ($expanded -eq $item.expandedBytes) 'Expanded size matches archive'
    $size = @($next.sizes | Where-Object id -CEQ $item.declaration.id)
    Check ($size.Count -eq 1 -and $size[0].expandedBytes -eq $expanded) 'Catalog expanded size matches archive'
    if ($item.declaration.stripPrefix -ceq 'ZZZ_KESASPotato') {
        Check ([Text.Encoding]::UTF8.GetString($contents['ZZZ_KESASPotato/DisableExhaust.cfg']) -ceq $policyText) 'Policy archive exactly matches reviewed source'
    }
    if ($item.declaration.stripPrefix -ceq 'ZZZ_KESASModFixes') {
        Check ([Text.Encoding]::UTF8.GetString($contents['ZZZ_KESASModFixes/OptionalVisualModules.cfg']) -ceq $optionalText) 'Common archive exactly matches reviewed source'
    }
}
$checker = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $stagePath 'ClientOverlay/GameData/000_Harmony/HarmonyInstallChecker.dll'))
try {
    $attribute = @($checker.CustomAttributes | Where-Object { $_.AttributeType.Name -ceq 'KSPAssembly' })
    Check ($attribute.Count -eq 1 -and $attribute[0].ConstructorArguments[0].Value -ceq 'HarmonyKSP') 'Official install checker declares HarmonyKSP'
    Check ($attribute[0].ConstructorArguments[1].Value -ge 1) 'HarmonyKSP dependency version satisfies 1.0.0'
} finally { $checker.Dispose() }
foreach ($file in $plan.dependency.files) {
    Check ((Hash (Join-Path $stagePath ('ClientOverlay/' + $file.path))) -ceq $file.sha256) "Official dependency file hash: $($file.path)"
}
$oldHarmony = Join-Path $PSScriptRoot '../Aetherfront-RC24-Work/Candidate/Client/GameData/000_Harmony/0Harmony.dll'
if (Test-Path -LiteralPath $oldHarmony) {
    Check ((Hash $oldHarmony) -ceq (Hash (Join-Path $stagePath 'ClientOverlay/GameData/000_Harmony/0Harmony.dll'))) 'Existing 0Harmony.dll remains byte-identical; only missing checker restored'
}
foreach ($audit in $plan.waterfall) {
    Check ((Hash $audit.sourceArchive) -ceq $audit.sourceSha256) 'Original Waterfall archive unchanged'
    $oldFiles = Zip-Data $audit.sourceArchive
    $newPackage = @($plan.media | Where-Object { $_.declaration.id -ceq ($base.($audit.profile).managedPackages | Where-Object stripPrefix -CEQ 'Waterfall').id })
    Check ($newPackage.Count -eq 1) 'Waterfall media maps uniquely to profile'
    $newFiles = Zip-Data $newPackage[0].path
    Check ($oldFiles.Count -eq $newFiles.Count) 'Waterfall archive file count preserved'
    foreach ($key in $oldFiles.Keys) {
        Check ($newFiles.ContainsKey($key)) "Waterfall file retained: $key"
        if ($key -cne 'Waterfall/Plugins/Waterfall.dll') { Check ((ByteHash $oldFiles[$key]) -ceq (ByteHash $newFiles[$key])) "Non-plugin Waterfall bytes unchanged: $key" }
    }
    $before = Read-Assembly $oldFiles['Waterfall/Plugins/Waterfall.dll']
    $after = Read-Assembly $newFiles['Waterfall/Plugins/Waterfall.dll']
    try {
        $oldMethods = Get-WaterfallMethodSnapshot $before
        $newMethods = Get-WaterfallMethodSnapshot $after
        $differences = @($oldMethods.Keys | Where-Object { $oldMethods[$_] -cne $newMethods[$_] })
        Check ($differences.Count -eq 2) 'Exactly two Waterfall method bodies changed'
        Check ($before.Name.FullName -ceq $after.Name.FullName) 'Waterfall assembly identity preserved'
        Check (($before.MainModule.AssemblyReferences.FullName -join ',') -ceq ($after.MainModule.AssemblyReferences.FullName -join ',')) 'No new runtime assembly dependencies'
        foreach ($typeName in @('Waterfall.WaterfallParticleLoader', 'Waterfall.ShaderLoader')) {
            $methodName = if ($typeName.EndsWith('ShaderLoader')) { 'LoadShaders' } else { 'LoadParticles' }
            $method = $after.MainModule.GetType($typeName).Methods | Where-Object Name -CEQ $methodName
            $root = @($method.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.DeclaringType.FullName -ceq 'KSPUtil' -and $_.Operand.Name -ceq 'get_ApplicationRootPath' })
            Check ($root.Count -eq 1 -and $root[0].Next.Operand -ceq 'GameData' -and $root[0].Next.Next.Operand.FullName -ceq 'System.String System.IO.Path::Combine(System.String,System.String)') 'Actual loader IL combines KSP root with GameData'
            Check (@($method.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.DeclaringType.FullName -ceq 'System.IO.Directory' -and $_.Operand.Name -ceq 'GetFiles' }).Count -eq 1) 'Original recursive scan retained'
        }
        $rebuilt = New-WaterfallGameDataBinary $oldFiles['Waterfall/Plugins/Waterfall.dll']
        Check ((ByteHash $rebuilt) -ceq (ByteHash $newFiles['Waterfall/Plugins/Waterfall.dll'])) 'Waterfall binary patch reproducible'
    } finally { $after.Dispose(); $before.Dispose() }
}
# Filesystem regression fixture: recovery, prepared staging and Resources remain
# on disk but cannot be reached from either patched GameData scan root.
$fixture = Join-Path $work ('TestRuns/' + [Guid]::NewGuid().ToString('N'))
foreach ($relative in @('GameData/Active', 'APF/.transaction-backup/undo/FX', 'APF/prepared/Waterfall', 'Resources/GameData/Waterfall')) {
    $directory = Join-Path $fixture $relative
    [void][IO.Directory]::CreateDirectory($directory)
    foreach ($name in @('particles-fft.particle', 'waterfall-windows.waterfall')) { [IO.File]::WriteAllBytes((Join-Path $directory $name), [byte[]]@(0)) }
}
foreach ($pattern in @('*.particle', '*-windows.waterfall')) {
    $oldMatches = [IO.Directory]::GetFiles($fixture, $pattern, [IO.SearchOption]::AllDirectories)
    $newMatches = [IO.Directory]::GetFiles([IO.Path]::Combine($fixture, 'GameData'), $pattern, [IO.SearchOption]::AllDirectories)
    Check ($oldMatches.Count -eq 4 -and $newMatches.Count -eq 1) 'GameData-only search excludes backups, prepared staging and Resources'
}
Check (@(Get-ChildItem -LiteralPath $fixture -File -Recurse).Count -eq 8) 'Recovery and prepared fixture files remain untouched'
$result = [ordered]@{ checks = $script:checks; passed = $true; stage = $stagePath; fixture = $fixture; kspRuntimeExecuted = $false; note = 'Archive, binary-IL, real-config structural, profile-gating and filesystem regression checks; in-game verification still required.' }
[IO.File]::WriteAllText((Join-Path $fixture 'TEST-RESULT.json'), ($result | ConvertTo-Json -Depth 5) + "`n", [Text.UTF8Encoding]::new($false))
$result | ConvertTo-Json -Depth 5
