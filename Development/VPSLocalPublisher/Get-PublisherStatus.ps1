#requires -Version 7.5
param([ValidatePattern('^[a-f0-9]{32}$')][string]$RequestId)
$ErrorActionPreference='Stop'
$config=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'operator.json') -Raw|ConvertFrom-Json
$name=if($RequestId){$RequestId+'.json'}else{'latest.json'}
$path=Join-Path $config.statusRoot $name
$stream=[IO.FileStream]::new($path,'Open','Read',([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
try{$reader=[IO.StreamReader]::new($stream);try{$reader.ReadToEnd()}finally{$reader.Dispose()}}finally{$stream.Dispose()}
if($RequestId){
    $evidence=Join-Path $config.statusRoot ($RequestId+'.evidence.json')
    if([IO.File]::Exists($evidence)){Get-Content -LiteralPath $evidence -Raw}
}
