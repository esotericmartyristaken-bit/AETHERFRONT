# Batch Git verification; locks live bytes until the verified copy is complete.
function Copy-ReviewedLocalSource {
    param([string]$Git,[string]$SourceRoot,[string]$Destination,[string]$Commit,[string[]]$Files)
    $handles=[Collections.Generic.List[IO.FileStream]]::new()
    $priorEncoding=$OutputEncoding
    try {
        $paths=@(foreach($relative in $Files){
            $path=$relative.Replace('\','/')
            if($path -match '[\x00-\x1f"\\]' -or $path.StartsWith('/') -or $path.Split('/') -contains '..'){throw 'Unsafe reviewed source path'}
            $full=[IO.Path]::GetFullPath((Join-Path $SourceRoot $path))
            if(-not $full.StartsWith([IO.Path]::GetFullPath($SourceRoot).TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Reviewed source escapes root'}
            for($cursor=$full;$cursor;$cursor=[IO.Path]::GetDirectoryName($cursor)){
                if([IO.File]::GetAttributes($cursor) -band [IO.FileAttributes]::ReparsePoint){throw 'Reviewed source contains a reparse point'}
            }
            $handles.Add([IO.FileStream]::new($full,'Open','Read','Read',1MB,[IO.FileOptions]::SequentialScan))
            $path
        })
        if(-not $paths.Count -or @($paths|Select-Object -Unique).Count -ne $paths.Count){throw 'Empty or duplicate reviewed source selection'}
        $tree=@(& $Git -C $SourceRoot ls-tree -r --full-tree -z $Commit -- @paths)
        if($LASTEXITCODE -ne 0){throw 'Cannot read reviewed commit tree'}
        $blobs=@{}
        foreach($entry in (($tree -join "`n").Split([char]0,[StringSplitOptions]::RemoveEmptyEntries))){
            if($entry -cnotmatch '^100(?:644|755) blob ([a-f0-9]{40})\t(.+)$'){throw 'Unexpected reviewed Git tree entry'}
            $blobs[$Matches[2]]=$Matches[1]
        }
        $OutputEncoding=[Text.UTF8Encoding]::new($false)
        $working=@($paths | & $Git -C $SourceRoot hash-object --stdin-paths)
        if($LASTEXITCODE -ne 0 -or $working.Count -ne $paths.Count){throw 'Cannot hash reviewed source selection'}
        for($index=0;$index -lt $paths.Count;$index++){
            $relative=$paths[$index]
            if(-not $blobs.ContainsKey($relative) -or $working[$index] -cne $blobs[$relative]){throw 'Frozen source differs from reviewed commit blob'}
        }
        # All blobs pass before writing even the first executable source file.
        for($index=0;$index -lt $paths.Count;$index++){
            $relative=$paths[$index];$inputStream=$handles[$index]
            $sha=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($inputStream)).ToLowerInvariant();$inputStream.Position=0
            $target=Join-Path $Destination $relative
            $null=[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
            $outputStream=[IO.FileStream]::new($target,'CreateNew','Write','None',1MB,[IO.FileOptions]::SequentialScan)
            try{$inputStream.CopyTo($outputStream,1MB);$outputStream.Flush($true)}finally{$outputStream.Dispose()}
            if((Get-FileHash -LiteralPath $target).Hash.ToLowerInvariant() -cne $sha){throw 'Frozen local deployment source differs'}
            @{path=$relative;sha256=$sha}
        }
    } finally { $OutputEncoding=$priorEncoding;foreach($handle in $handles){$handle.Dispose()} }
}
