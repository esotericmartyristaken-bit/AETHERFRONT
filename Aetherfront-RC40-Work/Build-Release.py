"""Derive a small transport-only RC40 update from the authenticated RC39 bundle."""
from pathlib import Path
import argparse
import hashlib
import io
import json
import zipfile

parser = argparse.ArgumentParser()
parser.add_argument('--baseline', type=Path, required=True)
parser.add_argument('--code', type=Path, required=True)
parser.add_argument('--output', type=Path, required=True)
args = parser.parse_args()
sha = lambda data: hashlib.sha256(data).hexdigest()
load = lambda data: json.loads(data.decode('utf-8-sig'))
encode = lambda value: (json.dumps(value, ensure_ascii=False, separators=(',', ':')) + '\n').encode()
version = 'kesas-rc40'
baseline_sha = '4453b000e1400ef4625e3fae0adfa6e04b952655907d20ce72d6cc410d0df08d'
baseline_bytes = args.baseline.read_bytes()
assert sha(baseline_bytes) == baseline_sha, 'Unreviewed RC39 native release'

def read_zip(data):
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        names = archive.namelist()
        assert len(set(names)) == len(names), 'Duplicate archive entry'
        assert sum(entry.file_size for entry in archive.infolist()) <= 512 * 1024 * 1024
        for name in names:
            assert name and not name.startswith('/') and '\\' not in name and ':' not in name and '..' not in Path(name).parts
        assert archive.testzip() is None
        return {name: archive.read(name) for name in names}

def archive_bytes(entries):
    output = io.BytesIO()
    with zipfile.ZipFile(output, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for name, data in sorted(entries.items()):
            info = zipfile.ZipInfo(name, (2026, 11, 3, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            archive.writestr(info, data)
    data = output.getvalue()
    assert read_zip(data) == entries
    return data

native = read_zip(baseline_bytes)
release = load(native.pop('RELEASE.json'))
assert release['version'] == 'kesas-rc39'
assert set(native) == {record['path'] for record in release['files']}
assert all(sha(native[record['path']]) == record['sha256'] for record in release['files'])
old_native = native.copy()
client = read_zip(native['ClientTemplate.zip'])
old_client = client.copy()
prefix = 'GameData/LunaMultiplayer/Plugins/'
audits = {}
for name in ('Lidgren.Network.dll', 'Lidgren.Core.dll'):
    audit = load((args.code / (name + '.transport-audit.json')).read_bytes())
    assert audit['allOtherMethodsUnchanged'] and audit['originalGuardBodyUnchanged'] and audit['referencesUnchanged']
    assert audit['perConnectionLimitBytes'] == 4194304 and audit['perPeerLimitBytes'] == 67108864
    data = (args.code / name).read_bytes()
    assert sha(data) == audit['outputSha256']
    if name == 'Lidgren.Network.dll':
        assert sha(client[prefix + name]) == audit['inputSha256']
        client[prefix + name] = data
    else:
        assert audit['inputSha256'] == '90a61a0e2db1f7e62aa8b63aa4d4e1f10c877a7a60ce5338d278c2bbc1fe6dc0'
        native['ServerPayload/' + name] = data
    audits[name] = audit
version_audit = load((args.code / 'LmpClient.dll.version-audit.json').read_bytes())
assert version_audit['version'] == version and version_audit['allOtherMethodsUnchanged']
assert sha(client[prefix + 'LmpClient.dll']) == version_audit['inputSha256']
client[prefix + 'LmpClient.dll'] = (args.code / 'LmpClient.dll').read_bytes()
assert sha(client[prefix + 'LmpClient.dll']) == version_audit['outputSha256']

catalog_path = 'GameData/LunaMultiplayer/Launcher/install-catalog.json'
settings_path = 'GameData/LunaMultiplayer/Data/APFClient.json'
manifest_path = 'KESAS/PATCH-MANIFEST.json'
catalog = load(client[catalog_path])
pack = load(native['APFPack-template.json'])
assert catalog['version'] == 'kesas-rc39'
catalog['version'] = version
for profile in (catalog['full'], catalog['potato'], pack):
    assert profile['packVersion'] == profile['requiredClientVersion'] == 'kesas-rc39'
    profile['packVersion'] = profile['requiredClientVersion'] = version
    # Shared signed updater inventory owns these DLLs. Do not create a second
    # overlapping LunaMultiplayer catalog owner (the RC37 collision regression).
    assert not any(any(root.casefold() == 'gamedata/lunamultiplayer' for root in rule['pathRoots']) for rule in profile['clientOnly'])
client[catalog_path] = encode(catalog)
settings = load(client[settings_path])
assert settings['clientVersion'] == 'kesas-rc39'
settings['clientVersion'] = version
client[settings_path] = encode(settings)
client[manifest_path] = encode({'version': version, 'files': [
    {'path': name, 'bytes': len(data), 'sha256': sha(data)}
    for name, data in sorted(client.items()) if name != manifest_path
]})
expected_client = {prefix + 'Lidgren.Network.dll', prefix + 'LmpClient.dll', catalog_path, settings_path, manifest_path}
assert set(client) == set(old_client)
assert {name for name in client if client[name] != old_client[name]} == expected_client
native['ClientTemplate.zip'] = archive_bytes(client)
native['APFPack-template.json'] = encode(pack)
media = load(native['MediaReferences.json'])
assert media['applicationVersion'] == 'kesas-rc39'
media['applicationVersion'] = version
native['MediaReferences.json'] = encode(media)
expected_native = {'ClientTemplate.zip', 'APFPack-template.json', 'MediaReferences.json', 'ServerPayload/Lidgren.Core.dll'}
assert {name for name in native if native[name] != old_native.get(name)} == expected_native
assert set(native) == set(old_native) | {'ServerPayload/Lidgren.Core.dll'}
assert not any('/universe/' in name.casefold() or '/secrets/' in name.casefold() or name.casefold().endswith(('.key', '.pfx', '.p12')) for name in native)
native['RELEASE.json'] = encode({'version': version, 'files': [
    {'path': name, 'sha256': sha(data)} for name, data in sorted(native.items())
]})
args.output.mkdir(parents=True, exist_ok=True)
target = args.output / f'Aetherfront-Release-{version}.zip'
target.write_bytes(archive_bytes(native))
assert target.stat().st_size <= 512 * 1024 * 1024
result = {
    'version': version, 'baselineSha256': baseline_sha, 'archive': str(target),
    'sha256': sha(target.read_bytes()), 'bytes': target.stat().st_size,
    'changedClientFiles': sorted(expected_client), 'changedNativeFiles': sorted(expected_native),
    'unchangedMediaArchives': True, 'unchangedGameplayAndProgression': True,
    'productionConfigurationIncluded': False, 'transportAudits': audits,
    'status': 'Built and locally checked; guarded host publication still required'
}
(args.output / 'BUILD-RESULT.json').write_bytes(encode(result))
print(json.dumps(result, indent=2))
