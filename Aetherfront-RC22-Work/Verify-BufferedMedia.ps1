#Requires -Version 7.0
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
[Diagnostics.Process]::GetCurrentProcess().PriorityClass='BelowNormal'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../KESAS-VPS-Server'))
$catalog=Get-Content -LiteralPath "$root/.github-prepared/kesas-rc22/install-catalog.json" -Raw|ConvertFrom-Json
$packages=@($catalog.full.managedPackages)+@($catalog.potato.managedPackages)
$unique=@{}
foreach($package in $packages){
 $name=[IO.Path]::GetFileName(([uri]$package.archiveUrl).AbsolutePath)
 if($name -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*\.zip$'){throw 'Unexpected media filename.'}
 if($unique.ContainsKey($name)){
  if($unique[$name].sha256 -cne $package.sha256 -or $unique[$name].sizeBytes -ne $package.sizeBytes){throw 'Conflicting media identity.'}
 }else{$unique[$name]=$package}
}
$watch=[Diagnostics.Stopwatch]::StartNew();[long]$bytes=0;$count=0
foreach($name in @($unique.Keys|Sort-Object)){
 $package=$unique[$name];$path=Join-Path "$root/Server/Config" $name
 $item=Get-Item -LiteralPath $path
 if(($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $item.Length -ne $package.sizeBytes){throw 'Media link/size mismatch.'}
 $inputStream=[IO.FileStream]::new($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read,1048576,[IO.FileOptions]::SequentialScan)
 $sha=[Security.Cryptography.SHA256]::Create()
 try{$hash=[BitConverter]::ToString($sha.ComputeHash($inputStream)).Replace('-','').ToLowerInvariant()}finally{$sha.Dispose();$inputStream.Dispose()}
 if($hash -cne $package.sha256){throw "Media SHA256 mismatch: $name"}
 $bytes+=$item.Length;$count++
 if($count%20 -eq 0 -or $item.Length -ge 100MB){Write-Host "PASS buffered read $count/$($unique.Count); $bytes bytes; $([Math]::Round($watch.Elapsed.TotalSeconds,1)) seconds"}
}
$result=[ordered]@{verifiedAt=[DateTime]::UtcNow.ToString('o');files=$count;bytes=$bytes;seconds=$watch.Elapsed.TotalSeconds;bufferBytes=1048576;readOnly=$true;catalogHashesMatched=$true;scope='Read-only sequential local media hash verification; not a network or end-user benchmark.'}
$result|ConvertTo-Json|Set-Content -LiteralPath "$PSScriptRoot/BUFFERED-MEDIA-RESULT.json" -Encoding utf8
$result|ConvertTo-Json
