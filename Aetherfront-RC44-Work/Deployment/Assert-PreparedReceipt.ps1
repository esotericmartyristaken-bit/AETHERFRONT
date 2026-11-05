#requires -Version 7.4
function Assert-LocalPreparedReceipt([string]$ReceiptPath,[string]$ReceiptSha256,[string]$DeploymentRoot,[string]$ReleaseDirectory,[string]$PublishedDirectory,[string]$Version,[string]$GatePath) {
    if((Get-PCHash $ReceiptPath) -cne $ReceiptSha256){throw 'Candidate receipt changed; no shutdown authorized'}
    $receiptText=Read-UpdateText $ReceiptPath 16MB
    $receipt=$receiptText|ConvertFrom-Json
    # ConvertFrom-Json turns ISO process-start strings into DateTime values.
    # Re-serializing those trims fractional trailing zeros, falsely reporting a
    # changed live process. Preserve the exact hash-bound original strings;
    # do not round timestamps or loosen any PID/port/path/hash comparison.
    $document=[Text.Json.JsonDocument]::Parse($receiptText)
    try{
        $rawProcesses=@($document.RootElement.GetProperty('production').GetProperty('processes').EnumerateArray())
        if($rawProcesses.Count -ne @($receipt.production.processes).Count){throw 'Candidate process identity count differs'}
        for($i=0;$i -lt $rawProcesses.Count;$i++){
            $startedAt=$rawProcesses[$i].GetProperty('startedAt')
            if($startedAt.ValueKind -ne 'String'){throw 'Candidate process start identity is not a string'}
            $receipt.production.processes[$i].startedAt=$startedAt.GetString()
        }
    }finally{$document.Dispose()}
    # The candidate canonicalizes paths, while its publisher builds some with
    # forward slashes. Compare validated directory identities, not spellings.
    foreach($pair in @(@{actual=$receipt.deploymentRoot;expected=$DeploymentRoot},@{actual=$receipt.releaseDirectory;expected=$ReleaseDirectory},@{actual=$receipt.publishedDirectory;expected=$PublishedDirectory})){
        $actual=[IO.Path]::TrimEndingDirectorySeparator((Assert-UpdatePath $pair.actual))
        $expected=[IO.Path]::TrimEndingDirectorySeparator((Assert-UpdatePath $pair.expected))
        if($actual -ine $expected){throw 'Candidate readiness proof does not match this deployment'}
    }
    if($receipt.schema -cne 'apf-prepared-candidate/v1' -or $receipt.version -cne $Version -or
       $receipt.success -ne $true -or $receipt.startPassed -ne $true -or $receipt.shutdownPassed -ne $true -or
       $receipt.productionUnchanged -ne $true -or $receipt.productionPidUnchanged -ne $true -or
       $receipt.gateSha256 -cne (Get-PCHash $GatePath)){throw 'Candidate readiness proof does not match this deployment'}
    $age=([DateTimeOffset]::UtcNow-([DateTimeOffset]$receipt.createdAt).ToUniversalTime()).TotalSeconds
    if($age -lt -2 -or $age -gt 3600){throw 'Candidate readiness proof expired; live service remains running'}
    if((Get-PCHash "$ReleaseDirectory/RELEASE.json") -cne $receipt.releaseManifestSha256){throw 'Release manifest changed after candidate boot'}
    $release=Get-PCRelease $ReleaseDirectory $Version
    Assert-PCSame $receipt.releaseFiles @($release.files|Sort-Object path) 'Release bytes changed after candidate boot'
    $configuration=Get-UpdateDeployment $DeploymentRoot
    Assert-PCSame $receipt.publishedFiles @(Get-PCPublished $PublishedDirectory $Version $configuration) 'Prepared signed client bytes changed'
    $context=@{Root=$DeploymentRoot;Config=$configuration;Baseline=@($receipt.liveBaselineHashes);Production=$receipt.production;Receipt=$receipt}
    Assert-PCProduction $context -Full
}
