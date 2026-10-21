"""Prepare a locally signed code-test overlay. NEVER publish this fixture.

Uses disposable local trust, immutable production RC31 inputs and the authored
candidate. Version migration and production release tests are separate gates.
Both local manifests are identical code-test subsets. They deliberately do not
prove graphics/profile switching: mode inference treats equal manifests as
Regular. Use distinct real profiles for that gate, never claim it passed here.
"""
import hashlib,json,pathlib,subprocess,zipfile

controller=pathlib.Path(__file__).resolve().parents[3]
baseline=controller/'build/production31-audit'
overlay=controller/'build/rc33-local-overlay'
assert not overlay.exists(), 'Preserve existing signed fixture'
key=controller/'private-staging-signing-bootstrap/GameData/LunaMultiplayer/Data/APFClientUpdatePublicKey.xml'
verifier=controller/'staging/UpdateTool/ApfUpdateTool.exe'
subprocess.run([str(verifier),'--verify','--manifest',str(baseline/'APFClientUpdate.json'),'--archive',str(baseline/'APFClient-kesas-rc31.zip'),'--public-key-xml',str(key)],check=True)
feed=json.loads((baseline/'APFClientUpdate.json').read_text(encoding='utf-8-sig'))
assert feed['version']=='kesas-rc31' and len(feed['files'])==461
files={}
with zipfile.ZipFile(baseline/'APFClient-kesas-rc31.zip') as archive:
    assert set(archive.namelist())=={r['path'] for r in feed['files']}
    for record in feed['files']:
        data=archive.read(record['path'])
        assert len(data)==record['sizeBytes'] and hashlib.sha256(data).hexdigest()==record['sha256']
        files[record['path']]=data
def encode(value):return (json.dumps(value,separators=(',',':'),ensure_ascii=False)+'\n').encode()
plugins='GameData/LunaMultiplayer/Plugins/'
for name in ('LmpClient.dll','AetherfrontCore.dll','AetherfrontPersonalClient.dll'):
    files[plugins+name]=(controller/'build/rc33-client'/name).read_bytes()
files['GameData/LunaMultiplayer/Compatibility/KopernicusShadows.cfg']=(controller/'source/Aetherfront-RC33-Work/Compatibility/KopernicusShadows.cfg').read_bytes()
data='GameData/LunaMultiplayer/Data/'
files[data+'APFClientUpdatePublicKey.xml']=(controller/'staging/secrets/test-update-public.xml').read_bytes()
files[data+'APFClientUpdateManifestUrl.txt']=b'https://127.0.0.1:28866/updates/APFClientUpdate.json\n'
files[data+'APFTlsCertificatePins.txt']=(controller/'staging/secrets/local-pin.txt').read_bytes()
files[data+'APFServer.txt']=b'127.0.0.1:28865\n'
for name in ('APFDiagnosticsEndpoint.txt','APFDiagnosticsUploadToken.txt'):
    if data+name in files:files[data+name]=b''
catalog_path='GameData/LunaMultiplayer/Launcher/install-catalog.json'
catalog=json.loads(files[catalog_path])
catalog['gitHub']={'enabled':False} # Disposable local signing realm; never query the production channel.
pack=json.loads((controller/'staging/PersonalServer/Config/APFPack.json').read_text(encoding='utf-8-sig'))
for profile in ('full','potato'):
    catalog[profile]=json.loads(json.dumps(pack))
files[catalog_path]=encode(catalog)
files.pop('KESAS/PLAYER-INSTALL.txt',None)
# Trust and connection preferences are bootstrap inputs, not updater-managed
# files. The native builder must continue to reject updates to these paths.
bootstrap={n:files.pop(n) for n in list(files) if n.startswith(data) and n!=data+'APFClient.json'}
manifest='KESAS/PATCH-MANIFEST.json'
files[manifest]=encode({'version':'kesas-rc31','files':[{'path':n,'bytes':len(d),'sha256':hashlib.sha256(d).hexdigest()} for n,d in sorted(files.items()) if n!=manifest]})
overlay.mkdir()
for name,data in dict(files,**bootstrap).items():
    target=overlay/name
    assert target.resolve().is_relative_to(overlay.resolve())
    target.parent.mkdir(parents=True,exist_ok=True);target.write_bytes(data)
print(json.dumps({'localOnly':True,'versionLabel':'kesas-rc31','codeCandidate':'RC33 work','files':len(files),'productionKeysUsed':False}))
