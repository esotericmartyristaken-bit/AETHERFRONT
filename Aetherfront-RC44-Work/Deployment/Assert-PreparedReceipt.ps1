#requires -Version 7.4
function Assert-LocalPreparedReceipt([string]$ReceiptPath,[string]$ReceiptSha256,[string]$DeploymentRoot,[string]$ReleaseDirectory,[string]$PublishedDirectory,[string]$Version,[string]$GatePath) {
    if((Get-PCHash $ReceiptPath) -cne $ReceiptSha256){throw 'Candidate receipt changed; no shutdown authorized'}
    $receipt=Read-UpdateText $ReceiptPath 16MB|ConvertFrom-Json
    if($receipt.schema -cne 'apf-prepared-candidate/v1' -or $receipt.version -cne $Version -or
       $receipt.deploymentRoot -ine $DeploymentRoot -or $receipt.releaseDirectory -ine $ReleaseDirectory -or $receipt.publishedDirectory -ine $PublishedDirectory -or
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
