param(
 [Parameter(Mandatory)][string]$ClientPlugins,
 [Parameter(Mandatory)][string]$KspManaged
)
$ErrorActionPreference='Stop'
$controller=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$repo=Join-Path $controller 'source'
$build=Join-Path $controller 'build'
$runtime=Join-Path $controller 'staging/Server'
function Run-Dotnet([string[]]$Arguments){
 & dotnet @Arguments
 if($LASTEXITCODE -ne 0){throw "Build/test command failed: $($Arguments[0])"}
}
Run-Dotnet @('build',"$repo/Development/ProgressionPatch/ProgressionPatch.csproj",'-c','Release',"-p:CecilPath=$build/rc30-version-patcher/Mono.Cecil.dll",'-o',"$build/progression-patcher",'-v','quiet')
$patcher="$build/progression-patcher/ProgressionPatch.dll"
foreach($target in @(@{framework='net8.0';folder='clr';extension='dll'},@{framework='net472';folder='mono';extension='exe'})){
 Run-Dotnet @('build',"$repo/Development/PersonalProgressionTests/PersonalProgressionTests.csproj",'-c','Release','-f',$target.framework,'-o',"$build/personal-tests-$($target.folder)",'-v','quiet')
}
Run-Dotnet @($patcher,"$runtime/AetherfrontCore.dll","$build/personal-tests-clr/PersonalProgressionTests.dll","$build/rc33-server/AetherfrontCore.dll",'8CA5FB2D2FC40659B2AEF0F49C414EC444AFE3154536FE5FBC67D3251CE5CF4C')
Run-Dotnet @($patcher,"$ClientPlugins/AetherfrontCore.dll","$build/personal-tests-mono/PersonalProgressionTests.exe","$build/rc33-client/AetherfrontCore.dll",'1CBF59CEEA11D7A32C6907AA85E4F4DDE1B231C4230E6F630D2B47DFC337B5B5')
Run-Dotnet @($patcher,'server-reference',"$runtime/Server.dll","$build/rc33-server-reference/Server.dll")
Run-Dotnet @('build',"$PSScriptRoot/PersonalServer/AetherfrontPersonalServer.csproj",'-c','Release',"-p:ServerReference=$build/rc33-server-reference/Server.dll","-p:PersonalCore=$build/rc33-server/AetherfrontCore.dll","-p:ServerRuntime=$runtime",'-o',"$build/rc33-server",'-v','quiet')
Run-Dotnet @($patcher,'server-hooks',"$runtime/Server.dll","$build/rc33-server/AetherfrontPersonalServer.dll","$build/rc33-server/Server.dll")
$deps=Get-Content -Raw -LiteralPath "$runtime/Server.deps.json" | ConvertFrom-Json -AsHashtable
$dependencyTarget=$deps.targets[$deps.runtimeTarget.name]
if($dependencyTarget.ContainsKey('AetherfrontPersonalServer/1.0.0')){throw 'Server baseline is already patched'}
$newtonsoft=@($dependencyTarget.Keys | Where-Object {$_ -clike 'Newtonsoft.Json/*'})
if($newtonsoft.Count -ne 1){throw 'Ambiguous server Newtonsoft dependency'}
$dependencyTarget['Server/1.0.0'].dependencies['AetherfrontPersonalServer']='1.0.0'
$dependencyTarget['AetherfrontPersonalServer/1.0.0']=@{dependencies=@{AetherfrontCore='1.0.0';'Newtonsoft.Json'=$newtonsoft[0].Split('/')[1];'Lidgren.Core'='1.0.0'};runtime=@{'AetherfrontPersonalServer.dll'=@{assemblyVersion='1.0.0.0';fileVersion='1.0.0.0'}}}
$deps.libraries['AetherfrontPersonalServer/1.0.0']=@{type='project';serviceable=$false;sha512=''}
$deps | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath "$build/rc33-server/Server.deps.json" -Encoding utf8
Run-Dotnet @('build',"$PSScriptRoot/PersonalClient/AetherfrontPersonalClient.csproj",'-c','Release',"-p:PersonalCore=$build/rc33-client/AetherfrontCore.dll","-p:ClientPlugins=$ClientPlugins","-p:KspManaged=$KspManaged",'-o',"$build/rc33-client",'-v','quiet')
Run-Dotnet @($patcher,'client-hooks',"$ClientPlugins/LmpClient.dll","$build/rc33-client/AetherfrontPersonalClient.dll","$build/rc33-client/LmpClient.dll",$KspManaged)
foreach($target in @(@{framework='net8.0';folder='clr';runtime='server'},@{framework='net472';folder='mono';runtime='client'})){
 Run-Dotnet @('build',"$repo/Development/PersonalProgressionTests/PersonalProgressionTests.csproj",'-c','Release','-f',$target.framework,"-p:RuntimeCore=$build/rc33-$($target.runtime)/AetherfrontCore.dll",'-o',"$build/personal-patched-$($target.folder)",'-v','quiet')
}
Run-Dotnet @("$build/personal-patched-clr/PersonalProgressionTests.dll")
Write-Output 'Candidate built. KSP Mono, real server, UDP and Unity tests remain separate gates. No game or production files were modified.'
