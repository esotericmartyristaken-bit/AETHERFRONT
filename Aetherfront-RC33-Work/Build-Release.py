"""Build a keyless RC33 native bundle from the signed production RC31 client.

Use reviewed actual-runtime grafts, not a rebuild of historical server source.
The production host retains sole release signing authority.
"""
import argparse
import hashlib
import json
import pathlib
import stat
import subprocess
import zipfile
import io
from PackPolicy import update_catalog, remove_declarations

p = argparse.ArgumentParser()
for name in ('feed', 'archive', 'public-key', 'verifier', 'client', 'server', 'versioned-client', 'engine', 'templates', 'output'):
    p.add_argument('--' + name, required=True, type=pathlib.Path)
a = p.parse_args()

def sha(data): return hashlib.sha256(data).hexdigest()
def load(path): return json.loads(path.read_text(encoding='utf-8-sig'))
def js(value): return (json.dumps(value, separators=(',', ':'), ensure_ascii=False) + '\n').encode()

for path in vars(a).values():
    for ancestor in [path.absolute(), *path.absolute().parents]:
        assert not ancestor.is_symlink() and not (ancestor.exists() and getattr(ancestor.lstat(), 'st_file_attributes', 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT), 'Reparse build path refused'
assert not a.output.exists(), 'Preserve a prior release attempt; use a new output'
subprocess.run([str(a.verifier), '--verify', '--manifest', str(a.feed), '--archive', str(a.archive), '--public-key-xml', str(a.public_key)], check=True)
feed = load(a.feed)
assert feed['version'] == 'kesas-rc31' and sha(a.archive.read_bytes()) == feed['sha256']
records = {f['path']: f for f in feed['files']}
assert len(records) == len(feed['files']) == 461
files = {}
with zipfile.ZipFile(a.archive) as z:
    assert len(z.namelist()) == len(records) and set(z.namelist()) == set(records)
    for name, entry in records.items():
        data = z.read(name)
        assert len(data) == entry['sizeBytes'] and sha(data) == entry['sha256']
        files[name] = data

plugins = 'GameData/LunaMultiplayer/Plugins/'
for folder, filename, baseline in [
    (a.client, 'AetherfrontCore.dll', '1CBF59CEEA11D7A32C6907AA85E4F4DDE1B231C4230E6F630D2B47DFC337B5B5'),
    (a.server, 'AetherfrontCore.dll', '8CA5FB2D2FC40659B2AEF0F49C414EC444AFE3154536FE5FBC67D3251CE5CF4C')]:
    audit = load(folder / (filename + '.audit.json'))
    assert audit['inputSha256'].upper() == baseline and len(audit['changed']) == 1
    assert audit['outputSha256'].lower() == sha((folder / filename).read_bytes())
    assert 'Aetherfront.Core.AetherfrontState::.ctor' in audit['changed'][0]
engine_audit = load(a.engine / 'method-audit.json')
version_audit = load(a.versioned_client / 'version-audit.json')
assert engine_audit['version'] == version_audit['version'] == 'kesas-rc33'
assert engine_audit['oldSha256'] == 'ED61774722CDD37505472568999B3E889CC25E527E2A25F8A4FFE2C2CEFD3D0A'
assert engine_audit['allOtherMethodsUnchanged'] and engine_audit['methodCount'] == 535
assert engine_audit['newSha256'].lower() == sha((a.engine / 'KesasInstall.dll').read_bytes())
assert len(engine_audit['engineChanges']) == 2 and all('LegacySettingsRecovery::' in name for name in engine_audit['engineChanges'])
client_hooks = load(a.client / 'LmpClient.dll.audit.json')
server_hooks = load(a.server / 'Server.dll.audit.json')
assert client_hooks['baselineSha256'] == '2781F634E5B17A15B9590DA2093F56297DFA12711897DD43A8275D2950CE47BE'
assert len(client_hooks['changed']) == 12 and client_hooks['outputSha256'].lower() == sha((a.client / 'LmpClient.dll').read_bytes())
assert version_audit['inputSha256'] == client_hooks['outputSha256']
assert version_audit['outputSha256'].lower() == sha((a.versioned_client / 'LmpClient.dll').read_bytes())
assert version_audit['changed'] == ['System.String LmpClient.Apf.ApfClientSettings::get_ClientVersion()']
assert server_hooks['baselineSha256'] == '32E7BEEA41224B6368127EE346DD2F3F0069D66ED15682C78526A621871E845E'
assert len(server_hooks['changed']) == 3 and server_hooks['outputSha256'].lower() == sha((a.server / 'Server.dll').read_bytes())
assert sha(files[plugins+'ApfCore.dll']).upper() == '0A4B6BD7C25EE4E18ABA88EC8D41CB66B8C0034DA3BA02D2D090DDB798BDC6C0'
for filename, source in [
    ('LmpClient.dll', a.versioned_client / 'LmpClient.dll'),
    ('AetherfrontCore.dll', a.client / 'AetherfrontCore.dll'),
    ('AetherfrontPersonalClient.dll', a.client / 'AetherfrontPersonalClient.dll'),
    ('KesasInstall.dll', a.engine / 'KesasInstall.dll')]:
    files[plugins + filename] = source.read_bytes()
files['GameData/LunaMultiplayer/Compatibility/KopernicusShadows.cfg'] = (pathlib.Path(__file__).parent / 'Compatibility/KopernicusShadows.cfg').read_bytes()
catalog_path = 'GameData/LunaMultiplayer/Launcher/install-catalog.json'
old_catalog = json.loads(files[catalog_path])
assert old_catalog['version'] == 'kesas-rc31'
marker_bytes = b'// Profile identity for legacy helpers. Exhaust remains enabled.\nKESAS_POTATO_COMPAT\n{\n    enabled = true\n}\n'
marker_buffer = io.BytesIO()
with zipfile.ZipFile(marker_buffer, 'w', compression=zipfile.ZIP_DEFLATED, compresslevel=9) as z:
    info = zipfile.ZipInfo('ZZZ_KESASPotato/Profile.cfg', (2026, 10, 20, 0, 0, 0)); info.compress_type = zipfile.ZIP_DEFLATED; z.writestr(info, marker_bytes)
marker_archive = marker_buffer.getvalue(); marker_hash = sha(marker_archive)
marker_name = 'kesas-rc33-potato-marker-' + marker_hash + '.zip'
marker_package = {'id': 'kesas-rc33-potato-marker', 'version': 'sha-' + marker_hash, 'archiveUrl': 'https://217.216.49.250:18866/' + marker_name,
                  'sha256': marker_hash, 'sizeBytes': len(marker_archive), 'destination': 'GameData', 'stripPrefix': 'ZZZ_KESASPotato'}
catalog = update_catalog(old_catalog, marker_package, len(marker_bytes))
catalog['version'] = 'kesas-rc33'
for name in ('full', 'potato'):
    assert catalog[name]['packVersion'] == catalog[name]['requiredClientVersion'] == 'kesas-rc31'
    catalog[name]['packVersion'] = catalog[name]['requiredClientVersion'] = 'kesas-rc33'
    # Every retained/reused declaration is an existing immutable archive.
    old_packages = {p['id']: p for profile in ('full', 'potato') for p in old_catalog[profile]['managedPackages']}
    assert all(package == marker_package or package == old_packages[package['id']] for package in catalog[name]['managedPackages'])
files[catalog_path] = js(catalog)
settings_path = 'GameData/LunaMultiplayer/Data/APFClient.json'
settings = json.loads(files[settings_path]); assert settings['clientVersion'] == 'kesas-rc31'
settings['clientVersion'] = 'kesas-rc33'; files[settings_path] = js(settings)
retired = ['KESAS/PLAYER-INSTALL.txt']
for name in retired: files.pop(name, None)
inventory_path = 'KESAS/PATCH-MANIFEST.json'
files[inventory_path] = js({'version': 'kesas-rc33', 'files': [{'path': n, 'bytes': len(d), 'sha256': sha(d)} for n, d in sorted(files.items()) if n != inventory_path]})
changed = {name for name, data in files.items() if name not in records or sha(data) != records[name]['sha256']}
assert changed == {catalog_path, settings_path, inventory_path, plugins+'LmpClient.dll', plugins+'AetherfrontCore.dll', plugins+'AetherfrontPersonalClient.dll', plugins+'KesasInstall.dll', 'GameData/LunaMultiplayer/Compatibility/KopernicusShadows.cfg'}
assert not any('ControllerTestDriver' in n or n.endswith('.key') or 'PLAYER-INSTALL' in n for n in files)
assert {n for n in files if n.endswith('.pdb')} == {'GameData/000_Harmony/HarmonyInstallChecker.pdb'}
pack = load(a.templates / 'APFPack-template.json')
assert pack['packVersion'] == pack['requiredClientVersion'] == 'kesas-rc31' and pack['managedPackages'] == []
remove_declarations(pack)
pack['clientOnly'].append({'id': 'kesas-potato-profile-marker', 'pathRoots': ['GameData/ZZZ_KESASPotato'], 'dlls': []})
pack['packVersion'] = pack['requiredClientVersion'] = 'kesas-rc33'
science = load(a.server / 'AetherfrontScienceSubjects.json')
assert science['Version'] == 2 and len(science['Subjects']) >= 93002 and science['Experiments']
technology = load(a.server / 'AetherfrontTechnology.json')
a.output.mkdir(parents=True)
(a.output / 'MediaPayload').mkdir()
(a.output / 'MediaPayload' / marker_name).write_bytes(marker_archive)
with zipfile.ZipFile(a.output / 'ClientTemplate.zip', 'w', compression=zipfile.ZIP_DEFLATED, compresslevel=9) as z:
    for name, data in sorted(files.items()):
        info = zipfile.ZipInfo(name, (2026, 10, 20, 0, 0, 0)); info.compress_type = zipfile.ZIP_DEFLATED; z.writestr(info, data)
(a.output / 'APFPack-template.json').write_bytes(js(pack))
(a.output / 'AetherfrontTechnology.json').write_bytes((a.server / 'AetherfrontTechnology.json').read_bytes())
payload = a.output / 'ServerPayload'; payload.mkdir()
for name in ('Server.dll', 'AetherfrontCore.dll', 'AetherfrontPersonalServer.dll', 'Server.deps.json', 'AetherfrontScienceSubjects.json'):
    (payload / name).write_bytes((a.server / name).read_bytes())
names = sorted(f.relative_to(a.output).as_posix() for f in a.output.rglob('*') if f.is_file())
(a.output / 'RELEASE.json').write_bytes(js({'version': 'kesas-rc33', 'files': [{'path': n, 'sha256': sha((a.output / n).read_bytes())} for n in names]}))
archive = a.output.parent / 'Aetherfront-Release-kesas-rc33.zip'
assert not archive.exists()
with zipfile.ZipFile(archive, 'w', compression=zipfile.ZIP_DEFLATED, compresslevel=9) as z:
    for name in names + ['RELEASE.json']: z.write(a.output / name, name)
report = {'version': 'kesas-rc33', 'bytes': archive.stat().st_size, 'sha256': sha(archive.read_bytes()), 'changedClientFiles': sorted(changed), 'retired': retired,
          'newPackArchives': 1, 'newArchiveBytes': len(marker_archive), 'legacyPotatoIdentityPreserved': True, 'reusedPlumeArchives': 7, 'removedMods': ['PAPIPlugin'], 'serverRuntime': 'existing .NET 8.0.29', 'privateKeysIncluded': False}
(a.output.parent / 'BUILD-RESULT.json').write_bytes(js(report))
print(json.dumps(report, indent=2))
