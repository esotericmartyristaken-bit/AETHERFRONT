function Invoke-CompletedHealthFetch([string]$Destination) {
    [PreparedCandidate.Https]::Fetch('https://127.0.0.1:18866/health',
        '34b4bf414b4923fdf8150d0c56a541d393a45bf76dd50ae10e93afc5d1358d09',$Destination,64KB)
}
function Assert-CompletedLocalHttps([string]$EvidenceRoot) {
    $directory=Join-Path $EvidenceRoot 'CompletedHttps'
    $null=[IO.Directory]::CreateDirectory($directory)
    $last=$null
    for($attempt=1;$attempt -le 3;$attempt++) {
        try {
            $destination=Join-Path $directory ([Guid]::NewGuid().ToString('N')+'.json')
            $hash=Invoke-CompletedHealthFetch $destination
            $health=[IO.File]::ReadAllText($destination)|ConvertFrom-Json
            if($health.ok -ne $true -or $health.service -cne 'apf-pack-host'){throw 'Completed pinned HTTPS returned an invalid health body'}
            return @{verified=$true;attempt=$attempt;observedAtUtc=[DateTime]::UtcNow.ToString('o');sha256=$hash;certificateSha256='34b4bf414b4923fdf8150d0c56a541d393a45bf76dd50ae10e93afc5d1358d09';evidence=$destination}
        }catch {$last=$_.Exception.Message;if($attempt -lt 3){Start-Sleep -Milliseconds 250}}
    }
    throw ('Completed pinned HTTPS health failed: '+$last)
}
