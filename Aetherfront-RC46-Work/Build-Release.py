"""Layer only research sequencing and version metadata onto immutable RC45."""
from pathlib import Path
import argparse
import hashlib
import io
import json
import sys
import zipfile

if sys.flags.optimize:
    raise RuntimeError('Release invariants require unoptimized Python')
parser = argparse.ArgumentParser()
for name in ('baseline', 'personal', 'client', 'output'):
    parser.add_argument('--' + name, type=Path, required=True)
args = parser.parse_args()
version = 'kesas-rc46'
sha = lambda data: hashlib.sha256(data).hexdigest()
load = lambda data: json.loads(data.decode('utf-8-sig'))
encode = lambda data: (json.dumps(data, ensure_ascii=False, separators=(',', ':')) + '\n').encode()

def unzip(data):
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        names = archive.namelist()
        assert len(names) == len(set(names)) and sum(row.file_size for row in archive.infolist()) < 512 * 1024 * 1024
        assert all(name and not name.startswith('/') and '\\' not in name and ':' not in name and '..' not in Path(name).parts for name in names)
        assert archive.testzip() is None
        return {name: archive.read(name) for name in names}

def zip_bytes(files):
    stream = io.BytesIO()
    with zipfile.ZipFile(stream, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for name, data in sorted(files.items()):
            item = zipfile.ZipInfo(name, (2026, 11, 4, 0, 0, 0))
            item.compress_type = zipfile.ZIP_DEFLATED
            archive.writestr(item, data)
    result = stream.getvalue()
    assert unzip(result) == files
    return result

baseline = args.baseline.read_bytes()
assert sha(baseline) == 'fafb18ac5af215fea3d40e22a77104a2a20b032cae10e0a6ecc2264a718ec2f4'
native = unzip(baseline)
release = load(native.pop('RELEASE.json'))
assert release['version'] == 'kesas-rc45' and set(native) == {row['path'] for row in release['files']}
assert all(sha(native[row['path']]) == row['sha256'] for row in release['files'])
old_native = native.copy()
client = unzip(native['ClientTemplate.zip'])
old_client = client.copy()
prefix = 'GameData/LunaMultiplayer/'
audits = {}
for path, mode, member in (
    (args.personal, 'research', prefix + 'Plugins/AetherfrontPersonalClient.dll'),
    (args.client, 'stamp', prefix + 'Plugins/LmpClient.dll'),
):
    audit = load(Path(str(path) + '.audit.json').read_bytes())
    assert audit['mode'] == mode and audit['version'] == version
    assert audit['identityPreserved'] and audit['allOtherMethodsUnchanged']
    assert audit['inputSha256'] == sha(client[member]) and sha(path.read_bytes()) == audit['outputSha256']
    client[member] = path.read_bytes()
    audits[mode] = audit
catalog_path = prefix + 'Launcher/install-catalog.json'
settings_path = prefix + 'Data/APFClient.json'
manifest_path = 'KESAS/PATCH-MANIFEST.json'
catalog = load(client[catalog_path])
pack = load(native['APFPack-template.json'])
assert catalog['version'] == 'kesas-rc45'
catalog['version'] = version
for profile in (catalog['full'], catalog['potato'], pack):
    assert profile['packVersion'] == profile['requiredClientVersion'] == 'kesas-rc45'
    profile['packVersion'] = profile['requiredClientVersion'] = version
client[catalog_path] = encode(catalog)
settings = load(client[settings_path])
assert settings['clientVersion'] == 'kesas-rc45'
settings['clientVersion'] = version
client[settings_path] = encode(settings)
client[manifest_path] = encode({'version': version, 'files': [
    {'path': name, 'bytes': len(data), 'sha256': sha(data)} for name, data in sorted(client.items()) if name != manifest_path
]})
expected_client = {prefix + 'Plugins/LmpClient.dll', prefix + 'Plugins/AetherfrontPersonalClient.dll', catalog_path, settings_path, manifest_path}
assert set(client) == set(old_client) and {name for name in client if client[name] != old_client[name]} == expected_client
assert sha(client[prefix + 'Plugins/AetherfrontContinuityClient.dll']) == '564cd10e83bc8b3bce298044ef9e54048616b7557651b678c1bd45519e893b58'
native['ClientTemplate.zip'] = zip_bytes(client)
native['APFPack-template.json'] = encode(pack)
media = load(native['MediaReferences.json'])
assert media['applicationVersion'] == 'kesas-rc45'
media['applicationVersion'] = version
native['MediaReferences.json'] = encode(media)
expected_native = {'ClientTemplate.zip', 'APFPack-template.json', 'MediaReferences.json'}
assert set(native) == set(old_native) and {name for name in native if native[name] != old_native[name]} == expected_native
assert sha(native['ServerPayload/AetherfrontContinuityServer.dll']) == 'ff3a6c99ef8a8a076ca77c0344788f7f8b59110cdb959b81537e2c2bc745dea9'
assert not any('/universe/' in name.lower() or '/secrets/' in name.lower() or name.lower().endswith(('.key', '.pfx', '.p12')) for name in native)
native['RELEASE.json'] = encode({'version': version, 'files': [{'path': name, 'sha256': sha(data)} for name, data in sorted(native.items())]})
args.output.mkdir(parents=True, exist_ok=True)
target = args.output / f'Aetherfront-Release-{version}.zip'
assert not target.exists(), 'Use a new immutable output directory'
target.write_bytes(zip_bytes(native))
result = {'version': version, 'baselineSha256': sha(baseline), 'sha256': sha(target.read_bytes()), 'bytes': target.stat().st_size,
          'changedClientFiles': sorted(expected_client), 'changedNativeFiles': sorted(expected_native),
          'clockGroundOrbitRepairsIncluded': True, 'allNativePayloadsUnchangedFromRC45': True,
          'unchangedMediaArchives': True, 'productionStateOrKeysIncluded': False, 'audits': audits}
(args.output / 'BUILD-RESULT.json').write_bytes(encode(result))
print(json.dumps({key: value for key, value in result.items() if key != 'audits'}, indent=2))
