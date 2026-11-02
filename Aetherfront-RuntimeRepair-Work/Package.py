"""Unpublished overlay only. Never edits a game or signs/deploys a release."""
from pathlib import Path
import hashlib,json,sys,zipfile
controller,game=map(lambda x:Path(x).resolve(),sys.argv[1:])
out=controller/'build/runtime-repair-candidate'
sha=lambda data:hashlib.sha256(data).hexdigest()
target='KSP_x64_Data/Managed/Assembly-CSharp.dll'
payload='GameData/LunaMultiplayer/PluginData/Runtime/Assembly-CSharp.apfbin'
data=(game/target).read_bytes()
assert len(data)==10790912 and sha(data)=='d9e42483f25ee80a9c11d6c1c0a0d29b4ec78c1e08d76c971b71580c9cce51e4'
with zipfile.ZipFile(controller/'build/rc38-public-readback/APFClient-kesas-rc38.zip') as shipping:
    for name in ['KesasInstall.dll','AetherfrontContinuityClient.dll']:
        assert shipping.read('GameData/LunaMultiplayer/Plugins/'+name)==(game/'GameData/LunaMultiplayer/Plugins'/name).read_bytes(), 'Unreviewed code baseline'
files={payload:data}
for name in ['KesasInstall.dll','AetherfrontContinuityClient.dll']:
    files['GameData/LunaMultiplayer/Plugins/'+name]=(out/'code'/name).read_bytes()
report={'status':'Unpublished runtime repair overlay; merge into next signed release/starter',
        'referencePlatform':'Windows KSP 1.12.5 build 3190','target':target,'targetSha256':sha(data),
        'payload':payload,'changesInstalledGame':False,'pushed':False,'signed':False,
        'bootstrapLimitation':'An old already-loaded helper cannot execute new repair hooks. RC31-RC38 clients first install the updater feature; the next Connect prepares the repair. No seamless single-restart bootstrap is claimed.',
        'files':[{'path':n,'bytes':len(v),'sha256':sha(v)} for n,v in sorted(files.items())]}
(out/'PACKAGE.json').write_text(json.dumps(report,indent=2)+'\n')
archive=out/'Aetherfront-RuntimeRepair-UNPUBLISHED.zip'
with zipfile.ZipFile(archive,'w',zipfile.ZIP_DEFLATED,compresslevel=9) as z:
    for name,content in sorted(files.items()):z.writestr(name,content)
    z.writestr('RUNTIME-REPAIR-REVIEW.json',json.dumps(report,indent=2))
with zipfile.ZipFile(archive) as z:
    assert z.testzip() is None and all(z.read(n)==v for n,v in files.items())
print(json.dumps({'archive':str(archive),'bytes':archive.stat().st_size,'published':False}))
