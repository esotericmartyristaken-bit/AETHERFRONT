"""Build a keyless RC35 native release from the signed RC34 baseline.

No game installs or private state are read. Immutable mod media is published
separately; the guarded production host retains signing and activation authority.
"""
import argparse, hashlib, json, pathlib, re, shutil, subprocess, zipfile

p = argparse.ArgumentParser()
p.add_argument('phase', choices=('prepare', 'finalize'))
for name in ('controller', 'inputs', 'public-key', 'output'):
    p.add_argument('--' + name, required=True, type=pathlib.Path)
a = p.parse_args()
C = a.controller.resolve()
VERSION = 'kesas-rc35'
DATE = (2026, 10, 23, 0, 0, 0)
def sha(data): return hashlib.sha256(data).hexdigest()
def digest(file):
    value = hashlib.sha256()
    with file.open('rb') as f:
        for block in iter(lambda: f.read(1024 * 1024), b''): value.update(block)
    return value.hexdigest()
def load(file): return json.loads(file.read_text(encoding='utf-8-sig'))
def js(value): return (json.dumps(value, separators=(',', ':'), ensure_ascii=False) + '\n').encode()
def write_zip(file, files):
    with zipfile.ZipFile(file, 'w', compression=zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for name, data in sorted(files.items()):
            parts = pathlib.PurePosixPath(name).parts
            assert not name.startswith('/') and '..' not in parts and '\\' not in name and ':' not in name
            info = zipfile.ZipInfo(name, DATE); info.compress_type = zipfile.ZIP_DEFLATED
            z.writestr(info, data)

native = a.output / 'Native'
if a.phase == 'finalize':
    refs = load(native / 'MediaReferences.json')
    prepared = load(a.output / 'PREPARED.json')
    assert refs['applicationVersion'] == VERSION and refs['schemaVersion'] == 1
    assert refs['repository'] == 'esotericmartyristaken-bit/AETHERFRONT' and refs['tag'] == 'pack-media'
    assert {x['name'] for x in refs['assets']} == set(prepared['mediaAssets'])
    catalog = load(a.output / 'install-catalog.json')
    declared = {x['archiveUrl'].rsplit('/', 1)[-1]: x for mode in ('full', 'potato') for x in catalog[mode]['managedPackages']}
    for x in refs['assets']:
        assert (x['sha256'], x['sizeBytes']) == (declared[x['name']]['sha256'], declared[x['name']]['sizeBytes'])
    files = {x.relative_to(native).as_posix(): x.read_bytes() for x in native.rglob('*') if x.is_file() and x.name != 'RELEASE.json'}
    files['RELEASE.json'] = js({'version': VERSION, 'files': [{'path': n, 'sha256': sha(d)} for n, d in sorted(files.items())]})
    (native / 'RELEASE.json').write_bytes(files['RELEASE.json'])
    archive = a.output / ('Aetherfront-Release-' + VERSION + '.zip')
    assert not archive.exists(), 'Never overwrite a published immutable bundle'
    write_zip(archive, files)
    assert archive.stat().st_size <= 512 * 1024 * 1024
    result = {'version': VERSION, 'nativeBytes': archive.stat().st_size, 'sha256': digest(archive),
              'mediaBytes': prepared['mediaBytes'], 'privateStateIncluded': False, 'serverModeChanged': False}
    (a.output / 'BUILD-RESULT.json').write_bytes(js(result)); print(json.dumps(result, indent=2))
    raise SystemExit()

assert not (a.output / 'PREPARED.json').exists() and not (native / 'RELEASE.json').exists(), 'Never replace a completed release'
if a.output.exists():
    assert all(x.relative_to(a.output).as_posix() == 'install-catalog.json' or
               re.fullmatch(r'Media/kesas-rc35-[a-z0-9-]+-[a-f0-9]{64}\.zip', x.relative_to(a.output).as_posix())
               for x in a.output.rglob('*') if x.is_file()), 'Unknown partial release contents'
baseline = C / 'baselines/kesas-rc34'
subprocess.run([str(C / 'staging/UpdateTool/ApfUpdateTool.exe'), '--verify', '--manifest', str(baseline / 'APFClientUpdate.json'),
                '--archive', str(baseline / 'APFClient-kesas-rc34.zip'), '--public-key-xml', str(a.public_key)], check=True)
feed = load(baseline / 'APFClientUpdate.json')
assert feed['version'] == 'kesas-rc34' and digest(baseline / 'APFClient-kesas-rc34.zip') == feed['sha256']
records = {x['path']: x for x in feed['files']}
assert len(records) == len(feed['files']) == 463
with zipfile.ZipFile(baseline / 'APFClient-kesas-rc34.zip') as z:
    assert len(z.namelist()) == len(records) and set(z.namelist()) == set(records)
    files = {name: z.read(name) for name in records}
assert all(sha(data) == records[name]['sha256'] and len(data) == records[name]['sizeBytes'] for name, data in files.items())
plugins = 'GameData/LunaMultiplayer/Plugins/'
client = C / 'build/rc35-client'
versioned = C / 'build/rc35-release-client'
engine = C / 'build/rc35-engine'
audit = load(client / 'LmpClient.dll.continuity-audit.json')
version_audit = load(versioned / 'version-audit.json')
assert audit['inputSha256'].lower() == records[plugins + 'LmpClient.dll']['sha256'] and len(audit['changed']) == 9
assert audit['outputSha256'].lower() == digest(client / 'LmpClient.dll') == version_audit['inputSha256'].lower()
assert version_audit['version'] == VERSION and len(version_audit['changed']) == 1
assert version_audit['outputSha256'].lower() == digest(versioned / 'LmpClient.dll')
engine_audit = load(engine / 'method-audit.json')
assert engine_audit['version'] == VERSION and engine_audit['allOtherMethodsUnchanged'] and engine_audit['methodCount'] == 535
assert len(engine_audit['engineChanges']) == 2 and engine_audit['newSha256'].lower() == digest(engine / 'KesasInstall.dll')
files[plugins + 'LmpClient.dll'] = (versioned / 'LmpClient.dll').read_bytes()
files[plugins + 'AetherfrontContinuityClient.dll'] = (client / 'AetherfrontContinuityClient.dll').read_bytes()
files[plugins + 'KesasInstall.dll'] = (engine / 'KesasInstall.dll').read_bytes()
files['GameData/AetherfrontCompatibility/BetterBurnTime.cfg'] = (pathlib.Path(__file__).parent / 'Compatibility/BetterBurnTime.cfg').read_bytes()
for notice in (a.inputs / 'notices').rglob('*'):
    if notice.is_file() and ('licen' in notice.name.lower() or 'copying' in notice.name.lower()):
        files['KESAS/Licenses/RC35/' + notice.relative_to(a.inputs / 'notices').as_posix()] = notice.read_bytes()
catalog_path = 'GameData/LunaMultiplayer/Launcher/install-catalog.json'
catalog = json.loads(files[catalog_path]); assert catalog['version'] == 'kesas-rc34'
catalog['version'] = VERSION
native.mkdir(parents=True, exist_ok=True); media = a.output / 'Media'; media.mkdir(exist_ok=True)
staged = load(a.inputs / 'STAGED-INPUTS.json')
assert staged['visualEffectsProfiles'] == ['regular'] and staged['sharedPartsProfiles'] == ['regular', 'potato']
groups = {}
for record in staged['files']:
    path = a.inputs / record['layer'] / record['path']
    assert digest(path) == record['sha256'] and path.stat().st_size == record['bytes']
    root = pathlib.PurePosixPath(record['path']).parts[1]
    groups.setdefault((record['layer'], root), {})[record['path'][len('GameData/'):]] = path.read_bytes()
packages = []
for (layer, root), members in sorted(groups.items()):
    package_id = 'kesas-rc35-' + re.sub('[^a-z0-9-]', '-', root.lower())
    temp = media / (package_id + '.zip'); write_zip(temp, members)
    fingerprint = digest(temp); asset = media / (package_id + '-' + fingerprint + '.zip')
    if asset.exists():
        assert digest(asset) == fingerprint; temp.unlink()
    else: temp.rename(asset)
    package = {'id': package_id, 'version': 'sha-' + fingerprint, 'archiveUrl': 'https://217.216.49.250:18866/' + asset.name,
               'sha256': fingerprint, 'sizeBytes': asset.stat().st_size, 'destination': 'GameData', 'stripPrefix': root}
    packages.append((layer, package))
    catalog['sizes'].append({'id': package_id, 'expandedBytes': sum(map(len, members.values()))})

pack = load(C / 'build/rc34-release/Native/APFPack-template.json')
def update_profile(profile, regular):
    assert profile['packVersion'] == profile['requiredClientVersion'] == 'kesas-rc34'
    profile['packVersion'] = profile['requiredClientVersion'] = VERSION
    profile['sharedDeepFreeze'] = True
    if 'managedPackages' in profile and profile['managedPackages']:
        profile['managedPackages'] += [package for layer, package in packages if regular or layer == 'shared']
    existing = {root: entry for entry in profile['clientOnly'] for root in entry['pathRoots']}
    for name, data in files.items():
        if not name.startswith('GameData/') or not name.endswith('.dll'): continue
        root = '/'.join(name.split('/')[:2])
        if root not in existing: continue
        entry = existing[root]
        entry['dlls'] = [d for d in entry['dlls'] if d['path'].casefold() != name.casefold()]
        entry['dlls'].append({'path': name, 'sha256': sha(data)})
    additions = [('shared', 'AetherfrontCompatibility', {})] + [(layer, root, members) for (layer, root), members in sorted(groups.items())]
    for layer, root, members in additions:
        if layer != 'shared' and not regular: continue
        path = 'GameData/' + root
        assert path not in existing, 'New mod root overlaps existing profile: ' + path
        profile['clientOnly'].append({'id': 'rc35-' + re.sub('[^a-z0-9-]', '-', root.lower()), 'pathRoots': [path],
            'dlls': [{'path': 'GameData/' + name, 'sha256': sha(data)} for name, data in sorted(members.items()) if name.endswith('.dll')]})
update_profile(catalog['full'], True); update_profile(catalog['potato'], False); update_profile(pack, True)
assert pack['managedPackages'] == []
files[catalog_path] = js(catalog); (a.output / 'install-catalog.json').write_bytes(files[catalog_path])
settings_path = 'GameData/LunaMultiplayer/Data/APFClient.json'
settings = json.loads(files[settings_path]); assert settings['clientVersion'] == 'kesas-rc34'
settings['clientVersion'] = VERSION; files[settings_path] = js(settings)
inventory = 'KESAS/PATCH-MANIFEST.json'
files[inventory] = js({'version': VERSION, 'files': [{'path': n, 'bytes': len(d), 'sha256': sha(d)} for n, d in sorted(files.items()) if n != inventory]})
assert not any('ControllerTest' in n or n.endswith('.key') or '/saves/' in n.casefold() for n in files)
write_zip(native / 'ClientTemplate.zip', files)
(native / 'APFPack-template.json').write_bytes(js(pack))
shutil.copyfile(C / 'staging/Server/Config/AetherfrontTechnology.json', native / 'AetherfrontTechnology.json')
server = C / 'build/rc35-server'; server_audit = load(server / 'Server.dll.continuity-audit.json')
assert len(server_audit['changed']) == 10 and server_audit['outputSha256'].lower() == digest(server / 'Server.dll')
payload = native / 'ServerPayload'; payload.mkdir()
for name in ('Server.dll', 'AetherfrontContinuityServer.dll', 'Server.deps.json'):
    shutil.copyfile(server / name, payload / name)
# The reviewed matching plugin is mandatory; never copy its mutable Universe data.
compat = C / 'handoff/Runtime/Server/Plugins/LunaCompat'
assert digest(compat / 'LunaCompatServerPlugin.dll') == '151228b8494e5d4103ca2626aca76427b055771960ecee164a3c2b2c7d530e7f'
for name in ('LunaCompatServerPlugin.dll', 'LunaCompatServerPlugin.deps.json', 'LICENSE.txt'):
    target = payload / 'Plugins/LunaCompat' / name; target.parent.mkdir(parents=True, exist_ok=True); shutil.copyfile(compat / name, target)
prepared = {'version': VERSION, 'changedClientFiles': [n for n, d in files.items() if n not in records or sha(d) != records[n]['sha256']],
            'mediaAssets': [package['archiveUrl'].rsplit('/', 1)[-1] for _, package in packages],
            'mediaBytes': sum(package['sizeBytes'] for _, package in packages), 'serverModeChanged': False, 'signedUpdaterGameplayAcceptance': False}
(a.output / 'PREPARED.json').write_bytes(js(prepared)); print(json.dumps(prepared, indent=2))
