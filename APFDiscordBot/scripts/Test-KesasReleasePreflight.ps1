param([Parameter(Mandatory=$true)][string]$TestRoot)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$testPath=[IO.Path]::GetFullPath($TestRoot)
if([IO.Directory]::Exists($testPath)){throw 'Use a new test fixture directory'}
$null=[IO.Directory]::CreateDirectory($testPath)
$verifier=Join-Path $PSScriptRoot 'Verify-KesasRelease.ps1'
$utf8=[Text.UTF8Encoding]::new($false)
$cases=[Collections.Generic.List[object]]::new()
function New-Fixture([string]$Name,[string]$Extra='', [int]$Attributes=0,[string]$Mutation='',[bool]$Accept=$false) {
    $file=Join-Path $testPath ($Name+'.zip')
    $entries=[ordered]@{'ClientTemplate.zip'='fixture-client';'APFPack-template.json'='{}';'AetherfrontTechnology.json'='{}'}
    if($Extra){$entries[$Extra]='fixture-marker'}
    $rows=@(foreach($key in $entries.Keys){
        $hash=[Security.Cryptography.SHA256]::Create()
        try{$sha=[BitConverter]::ToString($hash.ComputeHash($utf8.GetBytes($entries[$key]))).Replace('-','').ToLowerInvariant()}finally{$hash.Dispose()}
        [ordered]@{path=$key;sha256=$sha}
    })
    $release=[ordered]@{version='kesas-rc29';files=$rows}
    if($Mutation -eq 'wrong-version'){$release.version='kesas-rc28'}
    if($Mutation -eq 'bad-hash'){$rows[0].sha256='0'*64}
    if($Mutation -eq 'missing-required'){$release.files=@($rows | Select-Object -Skip 1)}
    if($Mutation -eq 'duplicate-manifest'){$release.files=@($rows)+@($rows[0])}
    $zip=[IO.Compression.ZipFile]::Open($file,[IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach($key in $entries.Keys){
            $item=$zip.CreateEntry($key)
            if($key -eq $Extra -and $Attributes){$item.ExternalAttributes=$Attributes}
            $stream=$item.Open()
            try{$bytes=$utf8.GetBytes($entries[$key]);$stream.Write($bytes,0,$bytes.Length)}finally{$stream.Dispose()}
        }
        $item=$zip.CreateEntry('RELEASE.json');$stream=$item.Open()
        try{$bytes=$utf8.GetBytes(($release | ConvertTo-Json -Depth 6 -Compress));$stream.Write($bytes,0,$bytes.Length)}finally{$stream.Dispose()}
        if($Mutation -in @('duplicate-entry','undeclared-entry')){
            $name=if($Mutation -eq 'duplicate-entry'){'RELEASE.json'}else{'unlisted.txt'}
            $stream=$zip.CreateEntry($name).Open();$stream.Dispose()
        }
    }finally{$zip.Dispose()}
    if($Mutation -eq 'expansion-limit'){
        $bytes=[IO.File]::ReadAllBytes($file)
        for($i=0;$i -lt $bytes.Length-28;$i++){
            if($bytes[$i] -eq 80 -and $bytes[$i+1] -eq 75 -and $bytes[$i+2] -eq 1 -and $bytes[$i+3] -eq 2){
                [Array]::Copy([BitConverter]::GetBytes([uint32]2147483649),0,$bytes,$i+24,4);break
            }
        }
        [IO.File]::WriteAllBytes($file,$bytes)
    }
    $cases.Add([ordered]@{name=$Name;archive=$file;accept=$Accept})
}
New-Fixture 'valid-minimal' -Accept $true
New-Fixture 'valid-payload' 'ServerPayload/Plugins/Safe.dll' -Accept $true
$bad=@('ServerPayload/./Config/x','ServerPayload/Config./x','ServerPayload/Config /x',
'ServerPayload/NUL.txt','ServerPayload/COM1.dll',('ServerPayload/LPT'+[char]0x00b2+'.txt'),
'ServerPayload/x:stream','ServerPayload/../escape','/absolute','ServerPayload\bad','ServerPayload//bad',
'ServerPayload/dir./x','ServerPayload/dir /x',
'ServerPayload/Config/x','ServerPayload/config/x','ServerPayload/Universe/x','ServerPayload/secrets/x',
'ServerPayload/Logs/x','ServerPayload/Aetherfront/x','CertificateToolPayload/Config/x','PackHostPayload/secrets/x')
for($i=0;$i -lt $bad.Count;$i++){New-Fixture ('unsafe-'+$i) $bad[$i]}
New-Fixture 'unix-link' 'ServerPayload/link' -Attributes -1577123840
New-Fixture 'windows-reparse' 'ServerPayload/link' -Attributes 1024
foreach($mutation in @('wrong-version','bad-hash','missing-required','duplicate-manifest','duplicate-entry','undeclared-entry','expansion-limit')){New-Fixture $mutation -Mutation $mutation}
$results=@(foreach($case in $cases){
    $accepted=$false
    try{$null=& $verifier -Archive $case.archive -ExpectedVersion kesas-rc29;$accepted=$true}catch{}
    [ordered]@{name=$case.name;expectedAccepted=$case.accept;actualAccepted=$accepted;passed=$accepted -eq $case.accept}
})
[IO.File]::WriteAllText((Join-Path $testPath 'results.json'),($results | ConvertTo-Json),$utf8)
$failed=@($results | Where-Object {-not $_.passed})
if($failed.Count){throw ('Release preflight regression: '+(($failed | ForEach-Object name) -join ', '))}
Write-Output ("Release preflight: {0}/{0} passed; fixtures never extracted" -f $results.Count)
