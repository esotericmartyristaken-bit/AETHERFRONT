"""Assemble the keyless native bundle from a verified signed RC29 baseline.

Run VersionPatcher patch and review first. Signing is performed by the guarded
production host, never by this builder. No game installation is copied.
"""
import argparse, hashlib, json, pathlib, stat, subprocess, zipfile, re

p = argparse.ArgumentParser()
for name in ('baseline', 'feed', 'archive', 'public-key', 'verifier', 'code', 'engine', 'templates', 'output'):
    p.add_argument('--' + name, required=True, type=pathlib.Path)
a = p.parse_args()
def sha(data): return hashlib.sha256(data).hexdigest()
def load(path): return json.loads(path.read_text(encoding='utf-8-sig'))
def js(value): return (json.dumps(value, separators=(',', ':'), ensure_ascii=False) + '\n').encode()
for path in vars(a).values():
    for ancestor in [path.absolute(), *path.absolute().parents]:
        assert not ancestor.is_symlink() and not (ancestor.exists() and getattr(ancestor.lstat(), 'st_file_attributes', 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT), 'Reparse build path refused'
assert not a.output.exists(), 'Use a new immutable release output'
subprocess.run([str(a.verifier), '--verify', '--manifest', str(a.feed), '--archive', str(a.archive), '--public-key-xml', str(a.public_key)], check=True)
feed = load(a.feed)
assert feed['version'] == 'kesas-rc29' and len(feed['files']) == 464
assert sha(a.archive.read_bytes()) == feed['sha256']
records = {f['path']: f for f in feed['files']}
assert len(records) == 464
files = {}
with zipfile.ZipFile(a.archive) as z:
    assert set(z.namelist()) == set(records)
    for name, f in records.items():
        assert (a.baseline/name).resolve().is_relative_to(a.baseline.resolve())
        data = z.read(name)
        assert len(data) == f['sizeBytes'] and sha(data) == f['sha256']
        assert (a.baseline/name).read_bytes() == data
        files[name] = data
audit = load(a.code/'audit.json')
assert audit['version'] == 'kesas-rc30' and audit['reversedClientImagesMatchBaseline']
assert audit['allOtherMethodsUnchanged'] and audit['allOtherFieldsUnchanged'] and audit['allowAwakeUnchanged']
assert audit['engineSha256'] == sha(a.engine.read_bytes())
assert audit['clientSha256'] == sha((a.code/'LmpClient.dll').read_bytes())
assert audit['baselineFeedSha256'] == sha(a.feed.read_bytes()) and audit['baselineArchiveSha256'] == sha(a.archive.read_bytes())
core = 'GameData/LunaMultiplayer/Plugins/ApfCore.dll'
assert (a.code/'ApfCore.dll').read_bytes() == files[core]
for name, path in [('LmpClient.dll', a.code/'LmpClient.dll'), ('KesasInstall.dll', a.engine)]:
    files['GameData/LunaMultiplayer/Plugins/' + name] = path.read_bytes()
catalog_path = 'GameData/LunaMultiplayer/Launcher/install-catalog.json'
old_catalog = json.loads(files[catalog_path]); catalog = json.loads(files[catalog_path])
assert catalog['version'] == 'kesas-rc29'
catalog['version'] = 'kesas-rc30'
settings_roots = {x.lower() for x in ['BonVoyage','ConnectedLivingSpace','ExtraplanetaryLaunchpads','KerbalKonstructs','kOS','ShipManifest','TriggerTech','WaypointManager','Astrogator','EditorExtensionsRedux','Hangar','TweakScale','REPOSoftTech','[x]_Science!','PlanetShine']}
def package_root(package):
    destination=package['destination'].replace('\\','/').strip('/')
    tail=destination[9:] if destination.lower().startswith('gamedata/') else ''
    return (tail.split('/')[0] if tail else package['stripPrefix'].replace('\\','/').strip('/').split('/')[0]).lower()
version_changes=[]
for profile in ('full', 'potato'):
    assert catalog[profile]['packVersion'] == catalog[profile]['requiredClientVersion'] == 'kesas-rc29'
    catalog[profile]['packVersion'] = catalog[profile]['requiredClientVersion'] = 'kesas-rc30'
    selected=set()
    for package,previous in zip(catalog[profile]['managedPackages'],old_catalog[profile]['managedPackages']):
        root=package_root(package)
        if root in settings_roots:
            selected.add(root)
            package['version']=re.sub(r'-settings[0-9]+$','',previous['version'])+'-settings30'
            assert package['version']!=previous['version']
            version_changes.append({'profile':profile,'id':package['id'],'root':root,'before':previous['version'],'after':package['version']})
        assert {k:v for k,v in package.items() if k!='version'}=={k:v for k,v in previous.items() if k!='version'}, 'Only settings package versions may change'
    assert selected == (settings_roots if profile=='full' else settings_roots-{'planetshine'}), ('Unexpected settings profile roots',profile,selected)

files[catalog_path] = js(catalog)
settings_path = 'GameData/LunaMultiplayer/Data/APFClient.json'
settings = json.loads(files[settings_path]); assert settings['clientVersion'] == 'kesas-rc29'
settings['clientVersion'] = 'kesas-rc30'; files[settings_path] = js(settings)
inventory_path = 'KESAS/PATCH-MANIFEST.json'
inventory = {'version': 'kesas-rc30', 'files': [{'path': n, 'bytes': len(d), 'sha256': sha(d)} for n,d in sorted(files.items()) if n != inventory_path]}
files[inventory_path] = js(inventory)
allowed = {catalog_path, settings_path, inventory_path, 'GameData/LunaMultiplayer/Plugins/LmpClient.dll', 'GameData/LunaMultiplayer/Plugins/KesasInstall.dll'}
changed = {n for n,d in files.items() if sha(d) != records[n]['sha256']}
assert changed == allowed and len(files) == 464
pack = load(a.templates/'APFPack-template.json')
assert pack['packVersion'] == pack['requiredClientVersion'] == 'kesas-rc29' and pack['managedPackages'] == []
pack['packVersion'] = pack['requiredClientVersion'] = 'kesas-rc30'
a.output.mkdir(parents=True)
with zipfile.ZipFile(a.output/'ClientTemplate.zip', 'w', compression=zipfile.ZIP_DEFLATED, compresslevel=9) as z:
    for name,data in sorted(files.items()):
        info=zipfile.ZipInfo(name, (2026,10,18,0,0,0)); info.compress_type=zipfile.ZIP_DEFLATED; z.writestr(info,data)
(a.output/'APFPack-template.json').write_bytes(js(pack))
(a.output/'AetherfrontTechnology.json').write_bytes((a.templates/'AetherfrontTechnology.json').read_bytes())
names = ['ClientTemplate.zip','APFPack-template.json','AetherfrontTechnology.json']
(a.output/'RELEASE.json').write_bytes(js({'version':'kesas-rc30','files':[{'path':n,'sha256':sha((a.output/n).read_bytes())} for n in names]}))
archive = a.output.parent/'Aetherfront-Release-kesas-rc30.zip'
assert not archive.exists()
with zipfile.ZipFile(archive,'w',compression=zipfile.ZIP_DEFLATED,compresslevel=9) as z:
    for name in names+['RELEASE.json']: z.write(a.output/name,name)
report={'version':'kesas-rc30','archive':archive.name,'sha256':sha(archive.read_bytes()),'bytes':archive.stat().st_size,'changedClientFiles':sorted(changed),'immutablePackageBytesAndUrlsUnchanged':True,'settingsVersionChanges':version_changes,'exactPreservedPreferencePaths':16,'preparedEnvelopeProtocol':'prepared28','completionPreservationProtocol':'prepared30','serverPayloadChanged':False,'productionSigningKeysUsed':False}
(a.output.parent/'BUILD-RESULT.json').write_bytes(js(report))
print(json.dumps(report,indent=2))
