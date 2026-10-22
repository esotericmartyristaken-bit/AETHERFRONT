#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Archive,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$HostCertificateSha256,
    [string]$HostOrigin='https://217.216.49.250:18866',
    [string]$Notes='',
    [string]$ControlArchive='',
    [string]$ControlVersion='',
    [ValidateRange(1,60)][int]$TimeoutMinutes=30
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
if([bool]$ControlArchive -ne [bool]$ControlVersion){throw 'Supply both ControlArchive and ControlVersion, or neither.'}
$controller=Join-Path $PSScriptRoot 'controller.mjs'
if($ControlArchive){
    $requiredCapabilities=@('application-v1','guard-15m-empty-v1','host-report-v1')
    $applicationZip=[IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($Archive))
    try{
        if($applicationZip.GetEntry('MediaReferences.json')){$requiredCapabilities+='media-reference-v1'}
        if(@($applicationZip.Entries|Where-Object FullName -clike 'MediaPayload/*').Count){$requiredCapabilities+='media-payload-v1'}
        if(@($applicationZip.Entries|Where-Object FullName -clike 'UpdateToolPayload/*').Count){$requiredCapabilities+='native-tools-v1'}
    }finally{$applicationZip.Dispose()}
    $expectedControlHash=(Get-FileHash -LiteralPath $ControlArchive -Algorithm SHA256).Hash.ToLowerInvariant()
    & node $controller publish-control --origin $HostOrigin --pin $HostCertificateSha256 --archive $ControlArchive --version $ControlVersion
    if($LASTEXITCODE -ne 0){throw 'Control publication was rejected; application channel unchanged.'}
    $deadline=(Get-Date).AddMinutes($TimeoutMinutes)
    do {
        $output=@(& node $controller status --origin $HostOrigin --pin $HostCertificateSha256)
        if($LASTEXITCODE -ne 0){throw 'Fresh host status unavailable; application channel unchanged.'}
        $status=($output -join "`n")|ConvertFrom-Json
        if($status.control.rollback -ceq $ControlVersion -or $status.control.failure -ceq 'control-transaction-blocked'){throw 'Control replacement failed or rolled back; application channel unchanged.'}
        $applicationReady=$status.application.status -ceq 'idle' -and $status.application.health -ceq 'healthy' -and -not $status.application.pendingVersion -and -not $status.application.failure -and $status.application.clientVersion -ceq $status.application.packVersion -and @($requiredCapabilities|Where-Object {$_ -notin $status.capabilities}).Count -eq 0
        if($status.control.version -ceq $ControlVersion -and $status.control.sha256 -ceq $expectedControlHash -and $status.control.lastSuccess -ceq $ControlVersion -and $status.control.status -ceq 'healthy' -and -not $status.control.pendingVersion -and -not $status.control.failure -and $applicationReady){break}
        if((Get-Date)-ge $deadline){throw 'Control activation is still pending; application channel unchanged. Read status before retrying the same bytes.'}
        Start-Sleep -Seconds 5
    }while($true)
}
# The application publisher rechecks fresh capabilities before upload and again
# immediately before the final channel write. Upload is never activation.
& (Join-Path $PSScriptRoot '../APFDiscordBot/scripts/Publish-GitHubDeployment.ps1') -Archive $Archive -Version $Version -Notes $Notes -HostOrigin $HostOrigin -HostCertificateSha256 $HostCertificateSha256
