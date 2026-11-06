"""Build an immutable RC48 client overlay from the pinned RC47 release."""
from pathlib import Path
import argparse, hashlib, io, json, sys, zipfile

if sys.flags.optimize:
    raise RuntimeError('Release assertions require unoptimized Python')
p = argparse.ArgumentParser()
for name in ('baseline', 'code', 'output'):
    p.add_argument('--' + name, required=True, type=Path)
p.add_argument('--version', choices=('kesas-rc48', 'kesas-rc49'), default='kesas-rc48')
a = p.parse_args()
sha = lambda data: hashlib.sha256(data).hexdigest()
load = lambda data: json.loads(data.decode('utf-8-sig'))
encode = lambda value: (json.dumps(value, ensure_ascii=False, separators=(',', ':')) + '\n').encode()
version, previous = a.version, 'kesas-rc48' if a.version == 'kesas-rc49' else 'kesas-rc47'

def unpack(data):
    with zipfile.ZipFile(io.BytesIO(data)) as z:
        assert len(z.namelist()) == len(set(z.namelist()))
        assert sum(x.file_size for x in z.infolist()) < 512 * 1024 * 1024
        assert all(n and not n.startswith('/') and '\\' not in n and ':' not in n and '..' not in Path(n).parts for n in z.namelist())
        assert z.testzip() is None
        return {n: z.read(n) for n in z.namelist()}

def pack(files):
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for name, data in sorted(files.items()):
            entry = zipfile.ZipInfo(name, (2026, 11, 5, 0, 0, 0))
            entry.compress_type = zipfile.ZIP_DEFLATED
            z.writestr(entry, data)
    data = buffer.getvalue()
    assert unpack(data) == files
    return data

baseline = a.baseline.read_bytes()
assert sha(baseline) == ('7640f0d0de5f2ce93e39a83f74e894fbf3735849d2148271e46d575079c30aac' if version == 'kesas-rc49' else '466d66c5f92a769377fe2ee9cd12a578dc4a2f947ff9d70f09a06a798f419f85')
native = unpack(baseline)
release = load(native.pop('RELEASE.json'))
assert release['version'] == previous and set(native) == {x['path'] for x in release['files']}
assert all(sha(native[x['path']]) == x['sha256'] for x in release['files'])
old_native = native.copy()
client = unpack(native['ClientTemplate.zip'])
old_client = client.copy()
prefix = 'GameData/LunaMultiplayer/'
audits = {}
for name, mode, files, member in (
    ('LmpClient.dll', 'client', client, prefix + 'Plugins/LmpClient.dll'),
    ('AetherfrontPersonalClient.dll', 'personal', client, prefix + 'Plugins/AetherfrontPersonalClient.dll'),
    ('AetherfrontPersonalServer.dll', 'server', native, 'ServerPayload/AetherfrontPersonalServer.dll'),
):
    data = (a.code / name).read_bytes()
    audit = load((a.code / (name + '.audit.json')).read_bytes())
    assert audit['mode'] == mode and audit['version'] == version and audit['identityPreserved'] and audit['allOtherMethodsUnchanged']
    assert audit['inputSha256'] == sha(files[member]) and audit['outputSha256'] == sha(data)
    files[member] = data
    audits[name] = audit
if version == 'kesas-rc49':
    startup = load((a.code / 'AetherfrontPersonalClient.dll.startup.json').read_bytes())
    assert startup['passed'] and startup['discovered'] and startup['fallback'] and startup['recovery']
    assert startup['sha256'] == sha(client[prefix + 'Plugins/AetherfrontPersonalClient.dll'])
catalog_path, settings_path, manifest_path = prefix + 'Launcher/install-catalog.json', prefix + 'Data/APFClient.json', 'KESAS/PATCH-MANIFEST.json'
catalog, settings, apf_pack = load(client[catalog_path]), load(client[settings_path]), load(native['APFPack-template.json'])
assert catalog['version'] == settings['clientVersion'] == previous
catalog['version'] = settings['clientVersion'] = version
for profile in (catalog['full'], catalog['potato'], apf_pack):
    assert profile['packVersion'] == profile['requiredClientVersion'] == previous
    profile['packVersion'] = profile['requiredClientVersion'] = version
client[catalog_path], client[settings_path] = encode(catalog), encode(settings)
client[manifest_path] = encode({'version': version, 'files': [{'path': n, 'bytes': len(d), 'sha256': sha(d)} for n, d in sorted(client.items()) if n != manifest_path]})
changed_client = {prefix + 'Plugins/LmpClient.dll', prefix + 'Plugins/AetherfrontPersonalClient.dll', catalog_path, settings_path, manifest_path}
assert set(client) == set(old_client) and {n for n in client if client[n] != old_client[n]} == changed_client
native['ClientTemplate.zip'], native['APFPack-template.json'] = pack(client), encode(apf_pack)
media = load(native['MediaReferences.json'])
assert media['applicationVersion'] == previous
media['applicationVersion'] = version
native['MediaReferences.json'] = encode(media)
changed_native = {'ClientTemplate.zip', 'APFPack-template.json', 'MediaReferences.json', 'ServerPayload/AetherfrontPersonalServer.dll'}
assert set(native) == set(old_native) and {n for n in native if native[n] != old_native[n]} == changed_native
assert not any('/universe/' in n.lower() or '/secrets/' in n.lower() or n.lower().endswith(('.key', '.pfx', '.p12')) for n in native)
native['RELEASE.json'] = encode({'version': version, 'files': [{'path': n, 'sha256': sha(d)} for n, d in sorted(native.items())]})
a.output.mkdir(parents=True, exist_ok=True)
target = a.output / ('Aetherfront-Release-' + version + '.zip')
assert not target.exists()
target.write_bytes(pack(native))
result = {'version': version, 'sha256': sha(target.read_bytes()), 'bytes': target.stat().st_size, 'baselineSha256': sha(baseline), 'changedClientFiles': sorted(changed_client), 'changedNativeFiles': sorted(changed_native), 'audits': audits, 'productionStateOrKeysIncluded': False, 'mediaAndAllOtherPayloadsUnchanged': True}
(a.output / 'BUILD-RESULT.json').write_bytes(encode(result))
print(json.dumps({k: v for k, v in result.items() if k != 'audits'}, indent=2))
