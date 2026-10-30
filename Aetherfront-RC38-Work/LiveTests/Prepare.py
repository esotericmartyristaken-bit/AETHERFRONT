"""Prepare only the existing disposable apf-test, preserving previous evidence."""
from pathlib import Path
import json, os, shutil, zipfile, re
from datetime import datetime, timezone

w=Path(__file__).resolve().parents[4]
c=Path(__file__).resolve().parents[3]
game=w/'apf-test'
assert game.name=='apf-test' and game.parent==w and (game/'KSP_x64.exe').is_file(), str(game)
evidence=c/'tests/rc38-live'
evidence.mkdir(exist_ok=True)
before=evidence/'before'
assert not before.exists(), 'Preserve the first pre-test snapshot'
before.mkdir()
files=[game/'KSP.log', game/'settings.cfg',game/'GameData/LunaMultiplayer/Data/APFClient.json',
       game/'GameData/LunaMultiplayer/Plugins/ControllerTestDriver.dll']
for f in files:
    if f.exists(): shutil.copyfile(f,before/f.name)
driver=game/'GameData/LunaMultiplayer/Plugins/ControllerTestDriver.dll'
if driver.exists(): driver.unlink() # Only the isolated test driver; preserved above.
def put(relative,data):
    target=game/relative
    assert target.is_relative_to(game) and '..' not in Path(relative).parts
    for path in [target,*target.parents]:
        if path==game.parent: break
        assert not path.is_symlink() and not (path.exists() and os.lstat(path).st_file_attributes & 1024), str(path)
    target.parent.mkdir(parents=True,exist_ok=True)
    temp=target.with_name(target.name+'.rc38-temp')
    temp.write_bytes(data)
    os.replace(temp,target) # Avoid writing through an existing immutable hard link.
with zipfile.ZipFile(c/'build/rc38-release/Native/ClientTemplate.zip') as z:
    for name in z.namelist():
        if name.startswith('GameData/'): put(name,z.read(name))
for archive in (c/'build/rc38-themes/Media').glob('*.zip'):
    with zipfile.ZipFile(archive) as z:
        for name in z.namelist():put('GameData/'+name,z.read(name))
put('GameData/LunaMultiplayer/Plugins/RC38LiveProbe.dll',(c/'build/rc38-live-probe/RC38LiveProbe.dll').read_bytes())
settings=(game/'settings.cfg').read_text()
settings=re.sub(r'(?m)^(\s*[A-Z_]*VOLUME\s*=\s*)[^\r\n]+',r'\g<1>0',settings)
put('settings.cfg',settings.encode())
source=game/'saves/LunaMultiplayer-APF-aetherfront-continuity-df-test-eb809d68e09daa5cd9894ce0/persistent.sfs'
put('saves/RC38Probe/persistent.sfs',source.read_bytes())
(evidence/'PREPARATION.json').write_text(json.dumps({'utc':datetime.now(timezone.utc).isoformat(),
    'testRoot':str(game),'before':str(before),'userInstallationChanged':False,
    'networkConnectActions':False,'updaterActions':False,'testOnlySave':'RC38Probe',
    'staleDriverDisabled':True,'commands':'PID scoped; automatic quit disabled'},indent=2)+'\n')
print(str(game))
