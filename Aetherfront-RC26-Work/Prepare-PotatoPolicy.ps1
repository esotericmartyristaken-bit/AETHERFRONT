#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ProbeDirectory)
$ErrorActionPreference='Stop'
. "$PSScriptRoot/VersionPatcher/Review.Common.ps1"
$probe=Assert-UpdatePath $ProbeDirectory
if(!$probe.StartsWith((Get-RC26Output 'ModFixes/Tests')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Evidence must stay in ModFixes/Tests.'}
$summaryPath=Join-Path $probe 'Summary.json'
$summary=Get-Content -LiteralPath $summaryPath -Raw|ConvertFrom-Json
$source=Join-Path $PSScriptRoot 'ModFixes/Source/ZZZ_KESASPotato/DisableExhaust.cfg'
if($summary.policyHash -cne (Get-RC26Hash $source) -or !$summary.focused -or @($summary.reports).Count -ne 3){throw 'Complete exact-policy three-profile replay evidence required.'}
foreach($name in @('full','potato','legacy-potato')){
    $report=@($summary.reports|Where-Object profile -CEQ $name)
    if($report.Count-ne 1 -or $report[0].observedWarningsCovered-ne 112 -or $report[0].exceptions-ne 0 -or $report[0].errors-ne $(if($name-eq 'legacy-potato'){2}else{0})){throw "Incomplete MM/B9 proof: $name"}
}
$legacy=@($summary.reports|Where-Object profile -CEQ 'legacy-potato')[0]
if($legacy.missingWaterfallSelectors-ne 28 -or $legacy.unmatchedOtherSelectors-ne 84){throw 'Legacy control must reproduce all 112 actual failures.'}
$auditExe=Join-Path $probe 'PostPatchAudit.exe'
if(Test-Path -LiteralPath $auditExe){throw 'Audit output already exists; preserve it and inspect.'}
$managed=Join-Path $RC26Workspace 'Aetherfront-KSP-Smoke\KSP_x64_Data\Managed'
$auditSource=[IO.Path]::GetFullPath((Get-RC26Output 'ModFixes/Tests/PostPatchAudit.cs'))
$compilerArguments=@('/nologo','/target:exe',('/out:'+$auditExe),('/reference:'+(Join-Path $managed 'Assembly-CSharp.dll')),('/reference:'+(Join-Path $managed 'UnityEngine.dll')),('/reference:'+(Join-Path $managed 'UnityEngine.CoreModule.dll')),$auditSource)
& 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' @compilerArguments
if($LASTEXITCODE-ne 0){throw 'Postpatch audit compilation failed.'}
$auditOutput=& $auditExe $RC26Workspace $probe
if($LASTEXITCODE-ne 0){throw 'Postpatch physics/effect identity audit failed.'}
Write-Output $auditOutput
$baseline=Get-RC26Baseline
if($summary.catalogHash-cne (Get-RC26Hash "$RC26Workspace/Aetherfront-RC25-Work/ModFixes/Staged-RC25/install-catalog.json")){throw 'Unexpected replay baseline catalog.'}
$stage=Get-RC26Output 'ModFixes/Staged-RC26'
if(Test-Path -LiteralPath $stage){throw 'Immutable policy stage already exists.'}
New-UpdateDirectory "$stage/Media"; New-UpdateDirectory "$stage/Source"
[IO.File]::Copy($source,"$stage/Source/DisableExhaust.cfg",$false)
$old=@($baseline.catalog.potato.managedPackages|Where-Object id -CEQ 'kesas-potato-policy')[0]
$oldArchive=Join-Path "$RC26Root/Server/Config" ([IO.Path]::GetFileName(([uri]$old.archiveUrl).AbsolutePath))
Assert-RC26Hash $oldArchive $old.sha256
$archive="$stage/Media/policy.pending.zip"
$zip=[IO.Compression.ZipFile]::Open($archive,[IO.Compression.ZipArchiveMode]::Create)
try{
    $entry=$zip.CreateEntry('ZZZ_KESASPotato/DisableExhaust.cfg',[IO.Compression.CompressionLevel]::Optimal)
    $entry.LastWriteTime=[DateTimeOffset]::new(2020,1,1,0,0,0,[TimeSpan]::Zero)
    $stream=$entry.Open();try{$bytes=[IO.File]::ReadAllBytes($source);$stream.Write($bytes)}finally{$stream.Dispose()}
}finally{$zip.Dispose()}
$hash=Get-RC26Hash $archive;$filename="kesas-rc26-potato-policy-$hash.zip"
[IO.File]::Move($archive,"$stage/Media/$filename")
$inventory=Get-RC26ZipInventory $oldArchive
$change=[ordered]@{path='ZZZ_KESASPotato/DisableExhaust.cfg';oldSha256=$inventory['ZZZ_KESASPotato/DisableExhaust.cfg'].sha256;oldSizeBytes=$inventory['ZZZ_KESASPotato/DisableExhaust.cfg'].sizeBytes;sha256=(Get-RC26Hash $source);sizeBytes=[IO.FileInfo]::new($source).Length}
Assert-RC26PotatoPolicyZipDelta $oldArchive "$stage/Media/$filename" $change
$declaration=ConvertTo-RC26Json $old|ConvertFrom-Json
$declaration.version='sha-'+$hash.Substring(0,16);$declaration.sha256=$hash;$declaration.sizeBytes=[IO.FileInfo]::new("$stage/Media/$filename").Length
$declaration.archiveUrl=(Get-UpdateDeployment $RC26Root).packOrigin+'/'+$filename
$catalogRecord=@($baseline.feed.files|Where-Object path -CEQ $RC26CatalogRelative)[0]
Write-RC26Json "$stage/POLICY-TEST-RESULT.json" ([ordered]@{schemaVersion=1;version='kesas-rc26';profile='potato';passed=$true;sourceCatalogSha256=$catalogRecord.sha256;oldArchiveSha256=$old.sha256;newArchiveSha256=$hash;sourceSha256=$change.sha256;unchangedOtherEntries=$true;fullProfileUnchanged=$true;realModuleManagerSummarySha256=(Get-RC26Hash $summaryPath);observedWarningsCovered=112;legacyWaterfallMismatches=28;legacyRcsMismatches=84;postPatchAudit=@($auditOutput);limits='Headless configuration/transaction validation, not Unity prefab or gameplay validation.'})
Write-RC26Json "$stage/POLICY-PLAN.json" ([ordered]@{schemaVersion=1;version='kesas-rc26';profile='potato';sourceCatalogSha256=$catalogRecord.sha256;oldDeclaration=$old;declaration=$declaration;archive=[ordered]@{path="Media/$filename";sha256=$hash;sizeBytes=$declaration.sizeBytes};source=[ordered]@{path='Source/DisableExhaust.cfg';sha256=$change.sha256;sizeBytes=$change.sizeBytes};changedEntry=$change;testEvidence=[ordered]@{path='POLICY-TEST-RESULT.json';sha256=(Get-RC26Hash "$stage/POLICY-TEST-RESULT.json")}})
$null=Get-RC26PotatoPolicyPlan "$stage/POLICY-PLAN.json" (Get-RC26Hash "$stage/POLICY-PLAN.json") $baseline.catalog
Write-Output "POLICY-PLAN: $stage/POLICY-PLAN.json"
