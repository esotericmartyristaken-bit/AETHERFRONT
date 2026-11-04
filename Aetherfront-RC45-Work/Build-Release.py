"""Build the combined repair from immutable RC44; never include live state."""
from pathlib import Path
import argparse
import hashlib
import io
import json
import sys
import zipfile

if sys.flags.optimize:
    raise RuntimeError('Release invariants require unoptimized Python; remove -O/PYTHONOPTIMIZE')

parser = argparse.ArgumentParser()
for name in ('baseline', 'ground', 'client', 'output'):
    parser.add_argument('--' + name, type=Path, required=True)
args = parser.parse_args()
version = 'kesas-rc45'
sha = lambda data: hashlib.sha256(data).hexdigest()
load = lambda data: json.loads(data.decode('utf-8-sig'))
encode = lambda data: (json.dumps(data, ensure_ascii=False, separators=(',', ':')) + '\n').encode()


def unzip(data):
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        names = archive.namelist()
        assert len(names) == len(set(names)) and sum(row.file_size for row in archive.infolist()) < 512 * 1024 * 1024
        assert all(name and not name.startswith('/') and '\\' not in name and ':' not in name
                   and '..' not in Path(name).parts for name in names)
        assert archive.testzip() is None
        return {name: archive.read(name) for name in names}


def zip_bytes(files):
    stream = io.BytesIO()
    with zipfile.ZipFile(stream, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for name, data in sorted(files.items()):
            item = zipfile.ZipInfo(name, (2026, 11, 4, 0, 0, 0))
            item.compress_type = zipfile.ZIP_DEFLATED
            archive.writestr(item, data)
    data = stream.getvalue()
    assert unzip(data) == files
    return data


baseline = args.baseline.read_bytes()
assert sha(baseline) == '1751b1f8772fadb1c809852d954fea8c1d1fc472907f7190193202be06a89787'
native = unzip(baseline)
release = load(native.pop('RELEASE.json'))
assert release['version'] == 'kesas-rc44' and set(native) == {row['path'] for row in release['files']}
assert all(sha(native[row['path']]) == row['sha256'] for row in release['files'])
assert 'AccountReset.json' not in native
old_native = native.copy()
client = unzip(native['ClientTemplate.zip'])
old_client = client.copy()
prefix = 'GameData/LunaMultiplayer/'
audits = {}
for path, mode, member in (
    (args.ground, 'ground', prefix + 'Plugins/AetherfrontContinuityClient.dll'),
    (args.client, 'orbit', prefix + 'Plugins/LmpClient.dll'),
):
    audit = load(Path(str(path) + '.audit.json').read_bytes())
    assert audit['mode'] == mode and audit['version'] == version
    assert audit['identityPreserved'] and audit['allOtherMethodsUnchanged']
    assert audit['inputSha256'] == sha(client[member])
    assert sha(path.read_bytes()) == audit['outputSha256']
    client[member] = path.read_bytes()
    audits[mode] = audit

catalog_path = prefix + 'Launcher/install-catalog.json'
settings_path = prefix + 'Data/APFClient.json'
manifest_path = 'KESAS/PATCH-MANIFEST.json'
catalog = load(client[catalog_path])
pack = load(native['APFPack-template.json'])
assert catalog['version'] == 'kesas-rc44'
catalog['version'] = version
for profile in (catalog['full'], catalog['potato'], pack):
    assert profile['packVersion'] == profile['requiredClientVersion'] == 'kesas-rc44'
    profile['packVersion'] = profile['requiredClientVersion'] = version
client[catalog_path] = encode(catalog)
settings = load(client[settings_path])
assert settings['clientVersion'] == 'kesas-rc44'
settings['clientVersion'] = version
client[settings_path] = encode(settings)
client[manifest_path] = encode({'version': version, 'files': [
    {'path': name, 'bytes': len(data), 'sha256': sha(data)}
    for name, data in sorted(client.items()) if name != manifest_path
]})
expected_client = {prefix + 'Plugins/LmpClient.dll', prefix + 'Plugins/AetherfrontContinuityClient.dll',
                   catalog_path, settings_path, manifest_path}
assert set(client) == set(old_client) and {name for name in client if client[name] != old_client[name]} == expected_client
native['ClientTemplate.zip'] = zip_bytes(client)
native['APFPack-template.json'] = encode(pack)
media = load(native['MediaReferences.json'])
assert media['applicationVersion'] == 'kesas-rc44'
media['applicationVersion'] = version
native['MediaReferences.json'] = encode(media)
expected_native = {'ClientTemplate.zip', 'APFPack-template.json', 'MediaReferences.json'}
assert set(native) == set(old_native) and {name for name in native if native[name] != old_native[name]} == expected_native
for name, digest in {
    'ServerPayload/Server.dll': 'd37641645592b80b4e22065d813e65c68ba35b5cbd258b4a0b118d38109df59d',
    'ServerPayload/Lidgren.Core.dll': '99288eb5872e7a9608dc830295eb353b15db9b5fa26b33ceab94846201e329fa',
    'ServerPayload/AetherfrontPersonalServer.dll': 'da7be39a359b58fc32b5f7143546c0a8c30e5f620f42d9e587ee4d28826cfae7',
    'ServerPayload/AetherfrontContinuityServer.dll': 'ff3a6c99ef8a8a076ca77c0344788f7f8b59110cdb959b81537e2c2bc745dea9',
}.items():
    assert sha(native[name]) == digest
assert sha(client[prefix + 'Plugins/AetherfrontPersonalClient.dll']) == '02157055d47a3fe3c68b05fe8637f666db5af1815b8d5d23e14f2b78eb36da61'
assert sha(client[prefix + 'Plugins/Lidgren.Network.dll']) == 'b8fa4194d427843007cfc40c8e64fa9e5c084910a4bfa9e55d16315faeba40b3'
assert not any('/universe/' in name.lower() or '/secrets/' in name.lower()
               or name.lower().endswith(('.key', '.pfx', '.p12')) for name in native)
native['RELEASE.json'] = encode({'version': version, 'files': [
    {'path': name, 'sha256': sha(data)} for name, data in sorted(native.items())
]})
args.output.mkdir(parents=True, exist_ok=True)
target = args.output / f'Aetherfront-Release-{version}.zip'
assert not target.exists(), 'Use a new immutable output directory'
target.write_bytes(zip_bytes(native))
result = {'version': version, 'baselineSha256': sha(baseline), 'sha256': sha(target.read_bytes()),
          'bytes': target.stat().st_size, 'changedClientFiles': sorted(expected_client),
          'changedNativeFiles': sorted(expected_native), 'clockRepairIncluded': True,
          'unchangedMediaArchives': True, 'unchangedScienceAndTransport': True,
          'productionStateOrKeysIncluded': False, 'oneTimeAccountOperationIncluded': False,
          'audits': audits}
(args.output / 'BUILD-RESULT.json').write_bytes(encode(result))
print(json.dumps({key: value for key, value in result.items() if key != 'audits'}, indent=2))
