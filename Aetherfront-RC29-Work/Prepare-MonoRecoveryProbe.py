"""Prepare a completed *synthetic* migration fixture for a second runtime probe.

Never accepts an actual KSP install. Existing CLR evidence is preserved, and
normal authenticated production startup recovery runs next under KSP Mono.
"""
import argparse, hashlib, json, pathlib, shutil, zipfile
p=argparse.ArgumentParser()
for n in ('fixture','fixture-parent','baseline'):p.add_argument('--'+n,required=True,type=pathlib.Path)
a=p.parse_args();r=a.fixture.resolve();parent=a.fixture_parent.resolve()
assert r.is_relative_to(parent) and r!=parent
def load(p):return json.loads(p.read_text(encoding='utf-8-sig'))
assert (r/'startup-result.json').read_text()=='passed'
assert load(r/'startup-proof.json')['runtime']=='CLR'
assert (r/'saves/Fixture/persistent.sfs').read_text()=='synthetic save sentinel'
before=r/'APF/mono-probe-before';assert not before.exists();before.mkdir()
for f in list(r.glob('startup-*'))+[r/'APF/LegacySettingsRecovery.json']:
    if f.is_file():shutil.copy2(f,before/f.name)
requests=list(r.glob('APF/.staging/connect/*/transaction.json.consumed'));assert len(requests)==1
t=load(requests[0]);assert pathlib.Path(t['gameRoot']).resolve()==r
settings=load(r/'expected-settings-hashes.json');assert len(settings)==8
for relative in settings:
    assert pathlib.PurePosixPath(relative).parts[0] not in ('Squad','LunaMultiplayer')
    package=next(x for x in t['packages'] if x['package']['stripPrefix']==relative.split('/')[0])
    archive=pathlib.Path(package['archivePath']).resolve();assert archive.is_relative_to(r)
    assert hashlib.sha256(archive.read_bytes()).hexdigest()==package['package']['sha256']
    with zipfile.ZipFile(archive) as z:
        matches=[x for x in z.namelist() if x==relative];assert len(matches)<=1
        target=r/'GameData'/relative;assert target.resolve().is_relative_to(r)
        if matches:target.write_bytes(z.read(matches[0]))
        elif target.exists():target.unlink()
(r/'APF/LegacySettingsRecovery.json').unlink()
for n in ('startup-launch.txt','startup-result.json','startup-result.tmp'):
    f=r/n
    if f.exists():f.unlink()
managed=r/'KSP_x64_Data/Managed';managed.mkdir(parents=True,exist_ok=True)
for n in ('System.IO.Compression.dll','System.IO.Compression.FileSystem.dll'):
    shutil.copy2(a.baseline/'KSP_x64_Data/Managed'/n,managed/n)
print('Prepared only synthetic defaults/marker; preserved prior CLR startup evidence at',before)
