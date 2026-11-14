#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ReleaseRoot)
$ErrorActionPreference='Stop'
$ReleaseRoot=[IO.Path]::GetFullPath($ReleaseRoot)
$client=Join-Path $ReleaseRoot 'Client'
$prior='C:\Users\Administrator\Desktop\APF\UpdateBuilds\RC64-Freeze-Abort-20261113'
$output=Join-Path $ReleaseRoot 'Tests'
$fixture=Join-Path 'C:\Users\Administrator\Desktop\APF\UpdateBuilds\RC56-Sync-Fixture' ('RC65-DeepFreeze-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'))
if(!(Test-Path -LiteralPath $client) -or (Test-Path -LiteralPath $output) -or (Test-Path -LiteralPath $fixture)){throw 'Candidate and fresh test destinations are required'}
$null=New-Item -ItemType Directory -Path $output,$fixture
$continuity=Join-Path $fixture 'Continuity'
$null=New-Item -ItemType Directory -Path $continuity
Copy-Item -Path "$prior\Event-Tests\*" -Destination $fixture
Copy-Item -Path "$prior\Continuity-Tests\*" -Destination $continuity -Recurse
Get-ChildItem -LiteralPath $client -Filter '*.dll' -File | Copy-Item -Destination $fixture
Get-ChildItem -LiteralPath $client -Filter '*.dll' -File | Copy-Item -Destination $continuity
$game=Join-Path $fixture 'ReadOnly-Game-Reference'
$null=New-Item -ItemType Directory -Path $game
$null=New-Item -ItemType Junction -Path (Join-Path $game 'GameData') -Target 'C:\Users\Administrator\Desktop\APF\KESAS-RC16-Work\ModpackSource\GameData'
$null=New-Item -ItemType Junction -Path (Join-Path $game 'KSP_x64_Data') -Target 'C:\Users\Administrator\Desktop\APF\Aetherfront-KSP-Smoke\KSP_x64_Data'
$env:RC56_KSP_REFERENCE='C:\Users\Administrator\Desktop\APF\Aetherfront-KSP-Smoke'
$env:RC56_MONO_ROOT=$fixture
$env:APF_DF_EVENT_CANDIDATE=$client
$env:APF_DF_EVENT_GAMEDATA=Join-Path $game 'GameData'
$env:APF_DF_EVENT_MANAGED=Join-Path $game 'KSP_x64_Data\Managed'
$env:AETHERFRONT_KSP_TEST_ROOT=$game
$env:APF_CONTINUITY_CANDIDATE=$client
$started=[DateTime]::UtcNow.ToString('o')
$hostPath='C:\Users\Administrator\Desktop\APF\UpdateBuilds\RC56-Sync-Fixture\MonoHost\MonoHost.exe'
foreach($test in @(@{exe=(Join-Path $fixture 'DeepFreezeEventTests.exe');log='Event.txt';count=23},
                  @{exe=(Join-Path $continuity 'ContinuityMonoTests.exe');log='Continuity.txt';count=423})){
    & $hostPath $test.exe 2>&1 | Tee-Object -FilePath (Join-Path $output $test.log)
    if($LASTEXITCODE){throw "KSP Mono regression failed: $($test.log)"}
    if([IO.File]::ReadAllText((Join-Path $output $test.log)) -notmatch "PASS $($test.count) "){throw 'Expected complete regression result is missing'}
}
$files=@(Get-ChildItem -LiteralPath $client -Filter '*.dll' -File | ForEach-Object {@{path=$_.FullName;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()}})
$files+=@(Get-ChildItem -LiteralPath $output -Filter '*.txt' -File | ForEach-Object {@{path=$_.FullName;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()}})
$result=[ordered]@{version='kesas-rc65';passed=$true;actualKspMono=$true;startedAtUtc=$started;finishedAtUtc=[DateTime]::UtcNow.ToString('o');targetedAssertions=23;continuityAssertions=423;countsOverlap=$true;renderedGameplay=$false;files=$files;fixture=$fixture}
[IO.File]::WriteAllText((Join-Path $ReleaseRoot 'Test-Proof.json'),($result|ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))
[pscustomobject]$result|Select-Object version,passed,targetedAssertions,continuityAssertions,renderedGameplay,finishedAtUtc|ConvertTo-Json
