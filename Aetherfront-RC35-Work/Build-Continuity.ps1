param(
 [Parameter(Mandatory)][string]$ClientPlugins,
 [Parameter(Mandatory)][string]$KspManaged,
 [Parameter(Mandatory)][string]$HarmonyPath,
 [Parameter(Mandatory)][string]$OwnedTestGame
)
$ErrorActionPreference='Stop'
$ClientPlugins=[IO.Path]::GetFullPath($ClientPlugins)
$KspManaged=[IO.Path]::GetFullPath($KspManaged)
$HarmonyPath=[IO.Path]::GetFullPath($HarmonyPath)
$OwnedTestGame=[IO.Path]::GetFullPath($OwnedTestGame)
foreach($inputPath in @($ClientPlugins,$KspManaged,$HarmonyPath,$OwnedTestGame)){
 if(!(Test-Path -LiteralPath $inputPath)){throw "Build input missing: $inputPath"}
}
$controller=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$repo=Join-Path $controller 'source'
$build=Join-Path $controller 'build'
$runtime=Join-Path $controller 'staging/PersonalServer'
function Run([string[]]$Arguments) {
 & dotnet @Arguments
 if($LASTEXITCODE -ne 0){throw "Build/test failed: $($Arguments[0])"}
}
$cecil=Join-Path $build 'rc30-version-patcher/Mono.Cecil.dll'
$patcher=Join-Path $build 'continuity-patcher/ContinuityPatch.dll'
Run @('build',"$repo/Development/ContinuityPatch/ContinuityPatch.csproj",'-c','Release',"-p:CecilPath=$cecil",'-o',"$build/continuity-patcher",'-v','quiet')
Run @($patcher,'reference',"$build/rc33-server/Server.dll","$build/rc35-server-reference/Server.dll")
Run @('build',"$PSScriptRoot/Server/AetherfrontContinuityServer.csproj",'-c','Release',"-p:ServerReference=$build/rc35-server-reference/Server.dll","-p:CandidateCore=$build/rc33-server/AetherfrontCore.dll","-p:ServerRuntime=$runtime",'-o',"$build/rc35-server",'-v','quiet')
Run @($patcher,'server',"$build/rc33-server/Server.dll","$build/rc35-server/Server.dll","$build/rc35-server/AetherfrontContinuityServer.dll")
Run @('build',"$PSScriptRoot/Client/AetherfrontContinuityClient.csproj",'-c','Release',"-p:ClientPlugins=$ClientPlugins","-p:KspManaged=$KspManaged","-p:HarmonyPath=$HarmonyPath","-p:LifeSupportDll=$OwnedTestGame/GameData/UmbraSpaceIndustries/LifeSupport/USILifeSupport.dll",'-o',"$build/rc35-client",'-v','quiet')
Run @($patcher,'client',"$ClientPlugins/LmpClient.dll","$build/rc35-client/LmpClient.dll","$build/rc35-client/AetherfrontContinuityClient.dll",$KspManaged)
$deps=Get-Content -Raw -LiteralPath "$build/rc33-server/Server.deps.json" | ConvertFrom-Json -AsHashtable
$target=$deps.targets[$deps.runtimeTarget.name]
if($target.ContainsKey('AetherfrontContinuityServer/1.0.0')){throw 'Expected unchanged RC33 server dependency graph'}
$target['Server/1.0.0'].dependencies['AetherfrontContinuityServer']='1.0.0'
$target['AetherfrontContinuityServer/1.0.0']=@{dependencies=@{AetherfrontCore='1.0.0';'Lidgren.Core'='1.0.0';LunaConfigNode='1.0.0'};runtime=@{'AetherfrontContinuityServer.dll'=@{assemblyVersion='1.0.0.0';fileVersion='1.0.0.0'}}}
$deps.libraries['AetherfrontContinuityServer/1.0.0']=@{type='project';serviceable=$false;sha512=''}
$deps | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath "$build/rc35-server/Server.deps.json" -Encoding utf8
Run @('build',"$repo/Development/ContinuityTests/ContinuityTests.csproj",'-c','Release','-f','net8.0','-o',"$build/continuity-tests-clr",'-v','quiet')
Run @("$build/continuity-tests-clr/ContinuityTests.dll","$controller/tests/rc35-continuity")
Run @('build',"$repo/Development/ContinuityTests/ContinuityTests.csproj",'-c','Release','-f','net472','-o',"$build/continuity-tests-mono",'-v','quiet')
$env:AETHERFRONT_KSP_TEST_ROOT=[IO.Path]::GetFullPath($OwnedTestGame)
$env:APF_CONTINUITY_EVIDENCE=Join-Path (Split-Path $controller -Parent) 's35/clock-mono'
& "$build/MonoHost.exe" "$build/continuity-tests-mono/ContinuityTests.exe"
if($LASTEXITCODE -ne 0){throw 'KSP Mono clock tests failed'}
Run @('build',"$repo/Development/ContinuityServerTests/ContinuityServerTests.csproj",'-c','Release',"-p:Candidate=$build/rc35-server","-p:Core=$build/rc33-server","-p:Runtime=$runtime",'-o',"$build/continuity-server-tests",'-v','quiet')
$evidence=Join-Path $controller ('tests/rc35-server-clock-'+[Guid]::NewGuid().ToString('N').Substring(0,8))
Run @("$build/continuity-server-tests/ContinuityServerTests.dll",$evidence,$runtime)
Run @('build',"$repo/Development/ContinuityMonoTests/ContinuityMonoTests.csproj",'-c','Release',"-p:KspManaged=$KspManaged","-p:GameRoot=$OwnedTestGame","-p:HarmonyPath=$HarmonyPath","-p:Candidate=$build/rc35-client",'-o',"$build/continuity-mono-tests",'-v','quiet')
$env:APF_CONTINUITY_CANDIDATE=Join-Path $build 'rc35-client'
& "$build/MonoHost.exe" "$build/continuity-mono-tests/ContinuityMonoTests.exe"
if($LASTEXITCODE -ne 0){throw 'Actual KSP Mono timer/signature/serialization tests failed'}
Run @('build',"$repo/Development/ResourceClockWireTests/ResourceClockWireTests.csproj",'-c','Release','-f','net8.0',"-p:ServerRuntime=$runtime",'-o',"$build/resource-wire-clr",'-v','quiet')
Run @("$build/resource-wire-clr/ResourceClockWireTests.dll")
Run @('build',"$repo/Development/ResourceClockWireTests/ResourceClockWireTests.csproj",'-c','Release','-f','net472',"-p:ClientPlugins=$ClientPlugins",'-o',"$build/resource-wire-mono",'-v','quiet')
& "$build/MonoHost.exe" "$build/resource-wire-mono/ResourceClockWireTests.exe"
if($LASTEXITCODE -ne 0){throw 'KSP Mono resource clock framing tests failed'}
Write-Output 'Local continuity candidate built. No fixture or production install changed. UDP, Unity, faction handoff and pack tests remain required.'
