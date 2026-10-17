#requires -Version 7.0
$ErrorActionPreference='Stop'
. "$PSScriptRoot/VersionPatcher/Review.Common.ps1"
function Canonical($Files) { ConvertTo-RC26InventoryJson $Files }
$expected=@([ordered]@{path='GameData/Example/a.cfg';sha256=('a'*64);sizeBytes=7},[ordered]@{path='GameData/Example/b.dll';sha256=('b'*64);sizeBytes=19})
$actual=@([pscustomobject][ordered]@{sizeBytes=19;sha256=('b'*64);path='GameData/Example/b.dll'},[pscustomobject][ordered]@{sizeBytes=7;sha256=('a'*64);path='GameData/Example/a.cfg'})
if((Canonical $expected)-cne(Canonical $actual)){throw 'Equivalent native/dictionary inventory was rejected'}
$checks=1
foreach($kind in @('hash','size','path','missing','extra','duplicate')) {
    $changed=ConvertFrom-Json (ConvertTo-Json -InputObject $actual -Depth 10)
    switch($kind){
        'hash' {$changed[0].sha256='c'*64}
        'size' {$changed[0].sizeBytes++}
        'path' {$changed[0].path='GameData/Example/other.dll'}
        'missing' {$changed=@($changed[0])}
        'extra' {$changed=@($changed)+@([pscustomobject]@{path='GameData/Example/extra.cfg';sha256=('d'*64);sizeBytes=1})}
        'duplicate' {$changed=@($changed[0],$changed[0])}
    }
    if((Canonical $expected)-ceq(Canonical $changed)){throw "Changed inventory accepted: $kind"}
    $checks++
}
Write-Output "PASS canonical inventory checks=$checks; property/record order normalized, content differences rejected."
