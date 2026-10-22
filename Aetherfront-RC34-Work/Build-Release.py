"""Assemble RC34 from the signed RC33 baseline and explicit licensed inputs.

Large immutable media is uploaded separately through media-controller. Finalize
only after its exact MediaReferences.json exists; private keys stay on the host.
No KSP installation is created or modified by this builder.
"""
import argparse
import hashlib
import json
import pathlib
import shutil
import subprocess
import zipfile

p = argparse.ArgumentParser()
p.add_argument('phase', choices=('prepare', 'finalize'))
for name in ('baseline', 'public-key', 'verifier', 'dlc', 'bootstrap', 'client', 'engine', 'wasd', 'templates', 'output'):
    p.add_argument('--' + name, required=True, type=pathlib.Path)
a = p.parse_args()
VERSION = 'kesas-rc34'
DATE = (2026, 10, 21, 0, 0, 0)

def sha(path):
    value = hashlib.sha256()
    with path.open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''): value.update(block)
    return value.hexdigest()

def load(path): return json.loads(path.read_text(encoding='utf-8-sig'))
def js(value): return (json.dumps(value, separators=(',', ':'), ensure_ascii=False) + '\n').encode()
def digest(data): return hashlib.sha256(data).hexdigest()

if a.phase == 'finalize':
    references = load(a.output / 'Native/MediaReferences.json')
    catalog = load(a.output / 'install-catalog.json')
    expected = {p['archiveUrl'].rsplit('/', 1)[-1]: (p['sha256'], p['sizeBytes'])
                for mode in ('full', 'potato') for p in catalog[mode]['managedPackages']}
    assert references['schemaVersion'] == 1 and references['applicationVersion'] == VERSION
    assert references['repository'] == 'esotericmartyristaken-bit/AETHERFRONT' and references['tag'] == 'pack-media'
    assert len(references['assets']) == 3
    assert {x['name'] for x in references['assets']} == set(load(a.output / 'PREPARED.json')['mediaAssets'])
    assert all(expected[x['name']] == (x['sha256'], x['sizeBytes']) for x in references['assets'])
    native = a.output / 'Native'
    names = sorted(x.relative_to(native).as_posix() for x in native.rglob('*') if x.is_file() and x.name != 'RELEASE.json')
    (native / 'RELEASE.json').write_bytes(js({'version': VERSION, 'files': [{'path': n, 'sha256': sha(native / n)} for n in names]}))
    archive = a.output / ('Aetherfront-Release-' + VERSION + '.zip')
    assert not archive.exists(), 'Never replace an immutable native bundle'
    with zipfile.ZipFile(archive, 'w', compression=zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for name in names + ['RELEASE.json']: z.write(native / name, name)
    assert archive.stat().st_size < 512 * 1024 * 1024
    report = {'version': VERSION, 'nativeBytes': archive.stat().st_size, 'nativeSha256': sha(archive),
              'separateMediaBytes': sum(x['sizeBytes'] for x in references['assets']),
              'baseRelease': 'kesas-rc33', 'privateKeysIncluded': False, 'serverBinariesChanged': False,
              'ordinaryStockFolderProtectionChanged': False, 'gameplayTestsPerformed': False}
    (a.output / 'BUILD-RESULT.json').write_bytes(js(report))
    print(json.dumps(report, indent=2))
    raise SystemExit(0)

assert not a.output.exists(), 'Preserve existing release output'
feed_path = a.baseline / 'APFClientUpdate.json'
archive_path = a.baseline / 'APFClient-kesas-rc33.zip'
subprocess.run([str(a.verifier), '--verify', '--manifest', str(feed_path), '--archive', str(archive_path), '--public-key-xml', str(a.public_key)], check=True)
feed = load(feed_path)
assert feed['version'] == 'kesas-rc33' and sha(archive_path) == feed['sha256']
records = {x['path']: x for x in feed['files']}
assert len(records) == len(feed['files']) == 462
files = {}
with zipfile.ZipFile(archive_path) as z:
    assert len(z.namelist()) == len(records) and set(z.namelist()) == set(records)
    for name, record in records.items():
        data = z.read(name)
        assert len(data) == record['sizeBytes'] and digest(data) == record['sha256']
        files[name] = data

plugins = 'GameData/LunaMultiplayer/Plugins/'
version_audit = load(a.client / 'version-audit.json')
engine_audit = load(a.engine / 'method-audit.json')
assert version_audit['version'] == engine_audit['version'] == VERSION
assert version_audit['inputSha256'].lower() == records[plugins + 'LmpClient.dll']['sha256']
assert version_audit['outputSha256'].lower() == sha(a.client / 'LmpClient.dll')
assert version_audit['changed'] == ['System.String LmpClient.Apf.ApfClientSettings::get_ClientVersion()']
assert engine_audit['methodCount'] == 535 and engine_audit['allOtherMethodsUnchanged']
assert len(engine_audit['engineChanges']) == 2 and all('LegacySettingsRecovery::' in x for x in engine_audit['engineChanges'])
assert engine_audit['newSha256'].lower() == sha(a.engine / 'KesasInstall.dll')
assert load(a.bootstrap / 'DLC-BUILD.json')['assemblySha256'].lower() == sha(a.bootstrap / 'AetherfrontExpansionBootstrap.dll')
for name, folder in [('LmpClient.dll', a.client), ('KesasInstall.dll', a.engine), ('AetherfrontExpansionBootstrap.dll', a.bootstrap)]:
    files[plugins + name] = (folder / name).read_bytes()

a.output.mkdir(parents=True)
media = a.output / 'Media'; media.mkdir()
new_packages, sizes = [], []
inputs = load(a.dlc / 'DLC-INPUTS.json')
assert {x['name'] for x in inputs} == {'MakingHistory', 'Serenity'}
for record in inputs:
    name = record['name']
    source = a.dlc / (name + '.zip')
    assert sha(source) == record['sha256'] and source.stat().st_size == record['bytes']
    root = 'AetherfrontMakingHistory' if name == 'MakingHistory' else 'AetherfrontBreakingGround'
    temporary = media / (root + '.zip')
    # The inner archive already compresses stock assets. STORE the container to
    # avoid another large CPU/memory pass and keep installed contents immutable.
    with zipfile.ZipFile(temporary, 'w', compression=zipfile.ZIP_STORED) as z:
        info = zipfile.ZipInfo(root + '/PluginData/' + name + '.zip', DATE)
        info.compress_type = zipfile.ZIP_STORED
        with source.open('rb') as src, z.open(info, 'w') as dst: shutil.copyfileobj(src, dst, 1024 * 1024)
    fingerprint = sha(temporary)
    package_id = 'kesas-expansion-' + name.lower()
    basename = package_id + '-' + fingerprint + '.zip'
    target = media / basename; temporary.rename(target)
    new_packages.append({'id': package_id, 'version': 'sha-' + fingerprint,
        'archiveUrl': 'https://217.216.49.250:18866/' + basename, 'sha256': fingerprint,
        'sizeBytes': target.stat().st_size, 'destination': 'GameData', 'stripPrefix': root})
    sizes.append({'id': package_id, 'expandedBytes': record['bytes']})
wasd = load(a.wasd / 'PACK-CHANGE.json')
camera = wasd['managedPackage']
asset = a.wasd / 'MediaPayload' / camera['archiveUrl'].rsplit('/', 1)[-1]
assert sha(asset) == camera['sha256'] and asset.stat().st_size == camera['sizeBytes']
with zipfile.ZipFile(asset) as z:
    assert digest(z.read('WasdEditorCamera/Plugins/WasdEditorCameraContinued.dll')) == wasd['serverClientOnly']['dlls'][0]['sha256']
shutil.copyfile(asset, media / asset.name)
new_packages.append(camera)
sizes.append({'id': camera['id'], 'expandedBytes': wasd['expandedBytes']})
catalog_path = 'GameData/LunaMultiplayer/Launcher/install-catalog.json'
catalog = json.loads(files[catalog_path])
assert catalog['version'] == 'kesas-rc33'
catalog['version'] = VERSION
for mode in ('full', 'potato'):
    profile = catalog[mode]
    assert profile['packVersion'] == profile['requiredClientVersion'] == 'kesas-rc33'
    assert 'SquadExpansion' in profile['protectedRoots']
    assert not any(p['id'] in {x['id'] for x in new_packages} for p in profile['managedPackages'])
    profile['packVersion'] = profile['requiredClientVersion'] = VERSION
    profile['managedPackages'] += new_packages
catalog['sizes'] += sizes
files[catalog_path] = js(catalog)
(a.output / 'install-catalog.json').write_bytes(files[catalog_path])
settings_path = 'GameData/LunaMultiplayer/Data/APFClient.json'
settings = json.loads(files[settings_path]); assert settings['clientVersion'] == 'kesas-rc33'
settings['clientVersion'] = VERSION; files[settings_path] = js(settings)
inventory_path = 'KESAS/PATCH-MANIFEST.json'
files[inventory_path] = js({'version': VERSION, 'files': [{'path': n, 'bytes': len(d), 'sha256': digest(d)} for n, d in sorted(files.items()) if n != inventory_path]})
changed = sorted(n for n, d in files.items() if n not in records or digest(d) != records[n]['sha256'])
assert set(changed) == {plugins+'LmpClient.dll', plugins+'KesasInstall.dll', plugins+'AetherfrontExpansionBootstrap.dll', catalog_path, settings_path, inventory_path}
assert not any('ControllerTestDriver' in n or 'PLAYER-INSTALL' in n or n.endswith('.key') for n in files)
native = a.output / 'Native'; native.mkdir()
with zipfile.ZipFile(native / 'ClientTemplate.zip', 'w', compression=zipfile.ZIP_DEFLATED, compresslevel=9) as z:
    for name, data in sorted(files.items()):
        info = zipfile.ZipInfo(name, DATE); info.compress_type = zipfile.ZIP_DEFLATED; z.writestr(info, data)
pack = load(a.templates / 'APFPack-template.json')
assert pack['packVersion'] == pack['requiredClientVersion'] == 'kesas-rc33' and pack['managedPackages'] == []
pack['packVersion'] = pack['requiredClientVersion'] = VERSION
pack['clientOnly'] += [{'id': 'expansion-' + p['stripPrefix'].lower(), 'pathRoots': ['GameData/' + p['stripPrefix']], 'dlls': []}
                       for p in new_packages if p['id'] != camera['id']]
pack['clientOnly'].append(wasd['serverClientOnly'])
(native / 'APFPack-template.json').write_bytes(js(pack))
shutil.copyfile(a.templates / 'AetherfrontTechnology.json', native / 'AetherfrontTechnology.json')
(a.output / 'PREPARED.json').write_bytes(js({'version': VERSION, 'changedClientFiles': changed,
    'mediaAssets': [p['archiveUrl'].rsplit('/', 1)[-1] for p in new_packages], 'stockProtectionChanged': False,
    'sourceDlcBytes': sum(x['expandedBytes'] for x in inputs), 'serverModeChanged': False}))
print(json.dumps({'prepared': VERSION, 'mediaBytes': sum(p['sizeBytes'] for p in new_packages), 'clientFiles': len(files)}, indent=2))
