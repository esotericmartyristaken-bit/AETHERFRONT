#requires -Version 7.0
param([Parameter(Mandatory)][string]$DlcArchives,[Parameter(Mandatory)][string]$KspManaged,[Parameter(Mandatory)][string]$ApfCore,[Parameter(Mandatory)][string]$Compiler,[Parameter(Mandatory)][string]$Output)
$ErrorActionPreference='Stop'
$hashes=Get-Content -LiteralPath (Join-Path $DlcArchives 'DLC-INPUTS.json') -Raw|ConvertFrom-Json
$null=[IO.Directory]::CreateDirectory($Output)
$generated='internal static class ExpansionPayloadHashes {'
foreach($name in @('MakingHistory','Serenity')){
 $record=@($hashes|Where-Object name -ceq $name)
 if($record.Count -ne 1 -or $record[0].sha256 -cnotmatch '^[a-f0-9]{64}$' -or (Get-FileHash -LiteralPath (Join-Path $DlcArchives ($name+'.zip'))).Hash -ine $record[0].sha256){throw 'DLC input fingerprint mismatch'}
 $generated+=' public const string '+$name+' = "'+$record[0].sha256+'";'
}
$generated+=' }'
$generatedPath=Join-Path $Output 'ExpansionPayloadHashes.cs'
[IO.File]::WriteAllText($generatedPath,$generated,[Text.UTF8Encoding]::new($false))
$references=@('mscorlib','System','System.Core','UnityEngine','UnityEngine.CoreModule','UnityEngine.IMGUIModule','Assembly-CSharp')|ForEach-Object{Join-Path $KspManaged ($_+'.dll')}
$references+=@($ApfCore,(Join-Path $KspManaged 'System.IO.Compression.dll'),(Join-Path $KspManaged 'System.IO.Compression.FileSystem.dll'))
$arguments=@('/nostdlib+','/target:library','/langversion:10','/optimize+','/debug-','/deterministic+',('/out:"'+(Join-Path $Output 'AetherfrontExpansionBootstrap.dll')+'"'))
$arguments+=@($references|ForEach-Object{'/reference:"'+$_+'"'})
$arguments+=@(('"'+(Join-Path $PSScriptRoot 'ExpansionBootstrap.cs')+'"'),('"'+$generatedPath+'"'))
$response=Join-Path $Output 'build.rsp'
[IO.File]::WriteAllLines($response,$arguments)
& dotnet $Compiler /noconfig "@$response"
if($LASTEXITCODE -ne 0){throw 'DLC bootstrap compilation failed'}
@{version='kesas-rc34';assemblySha256=(Get-FileHash -LiteralPath (Join-Path $Output 'AetherfrontExpansionBootstrap.dll')).Hash;payloads=$hashes;stockProtectionUnchanged=$true}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $Output 'DLC-BUILD.json')
