"""Derive RC42 allocator update from authenticated RC41; never replay its account operation."""
from pathlib import Path
import argparse
import hashlib
import io
import json
import zipfile

p = argparse.ArgumentParser()
p.add_argument('--baseline', type=Path, required=True)
p.add_argument('--code', type=Path, required=True)
p.add_argument('--output', type=Path, required=True)
a = p.parse_args()
sha = lambda b: hashlib.sha256(b).hexdigest()
load = lambda b: json.loads(b.decode('utf-8-sig'))
encode = lambda value: (json.dumps(value, ensure_ascii=False, separators=(',', ':')) + '\n').encode()
version = 'kesas-rc42'
baseline_sha = '78e932abcdc03951168f22c78b0d8f05eaa494ca614a84b4c73c4b8fd115ff3c'

def read_zip(data):
    with zipfile.ZipFile(io.BytesIO(data)) as z:
        names = z.namelist()
        assert len(names) == len(set(names)) and sum(i.file_size for i in z.infolist()) < 512 * 1024 * 1024
        assert all(n and not n.startswith('/') and '\\' not in n and ':' not in n and '..' not in Path(n).parts for n in names)
        assert z.testzip() is None
        return {n: z.read(n) for n in names}

def archive(entries):
    out = io.BytesIO()
    with zipfile.ZipFile(out, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for n, data in sorted(entries.items()):
            i = zipfile.ZipInfo(n, (2026, 11, 3, 0, 0, 0)); i.compress_type = zipfile.ZIP_DEFLATED
            z.writestr(i, data)
    result = out.getvalue()
    assert read_zip(result) == entries
    return result

baseline = a.baseline.read_bytes()
assert sha(baseline) == baseline_sha
native = read_zip(baseline)
release = load(native.pop('RELEASE.json'))
assert release['version'] == 'kesas-rc41' and set(native) == {f['path'] for f in release['files']}
assert all(sha(native[f['path']]) == f['sha256'] for f in release['files'])
old_native = native.copy()
client = read_zip(native['ClientTemplate.zip']); old_client = client.copy()
prefix = 'GameData/LunaMultiplayer/'
audits = {}
for name, target, owner in [('Lidgren.Network.dll', prefix + 'Plugins/Lidgren.Network.dll', client), ('Lidgren.Core.dll', 'ServerPayload/Lidgren.Core.dll', native)]:
    audit = load((a.code / (name + '.audit.json')).read_bytes())
    assert audit['mode'] == 'transport' and audit['version'] == version
    assert audit['allOtherMethodsUnchanged'] and audit['originalAllocatorBodyPreserved'] and audit['existingReceiveFixPreserved'] and audit['sendReservationAndFragmentationMethodsUnchanged']
    assert audit['sendMemoryLimitBytes'] == audit['poolMemoryLimitBytes'] == 67108864
    assert sha(owner[target]) == audit['inputSha256']
    owner[target] = (a.code / name).read_bytes(); assert sha(owner[target]) == audit['outputSha256']
    audits[name] = audit
audit = load((a.code / 'LmpClient.dll.audit.json').read_bytes())
assert audit['mode'] == 'version' and audit['version'] == version and audit['allOtherMethodsUnchanged']
assert sha(client[prefix + 'Plugins/LmpClient.dll']) == audit['inputSha256']
client[prefix + 'Plugins/LmpClient.dll'] = (a.code / 'LmpClient.dll').read_bytes()
assert sha(client[prefix + 'Plugins/LmpClient.dll']) == audit['outputSha256']
catalog_path = prefix + 'Launcher/install-catalog.json'
settings_path = prefix + 'Data/APFClient.json'
manifest_path = 'KESAS/PATCH-MANIFEST.json'
catalog = load(client[catalog_path]); pack = load(native['APFPack-template.json'])
assert catalog['version'] == 'kesas-rc41'; catalog['version'] = version
for profile in (catalog['full'], catalog['potato'], pack):
    assert profile['packVersion'] == profile['requiredClientVersion'] == 'kesas-rc41'
    profile['packVersion'] = profile['requiredClientVersion'] = version
    assert not any(any(root.casefold() == 'gamedata/lunamultiplayer' for root in rule['pathRoots']) for rule in profile['clientOnly'])
client[catalog_path] = encode(catalog)
settings = load(client[settings_path]); assert settings['clientVersion'] == 'kesas-rc41'; settings['clientVersion'] = version
client[settings_path] = encode(settings)
client[manifest_path] = encode({'version': version, 'files': [{'path': n, 'bytes': len(data), 'sha256': sha(data)} for n, data in sorted(client.items()) if n != manifest_path]})
expected_client = {prefix + 'Plugins/LmpClient.dll', prefix + 'Plugins/Lidgren.Network.dll', catalog_path, settings_path, manifest_path}
assert set(client) == set(old_client) and {n for n in client if client[n] != old_client[n]} == expected_client
native['ClientTemplate.zip'] = archive(client); native['APFPack-template.json'] = encode(pack)
media = load(native['MediaReferences.json']); assert media['applicationVersion'] == 'kesas-rc41'; media['applicationVersion'] = version
native['MediaReferences.json'] = encode(media)
# RC41's authorized one-time name release already ran. A later version must not
# retry it or include any live state or account identifier in its public bundle.
assert 'AccountReset.json' in native
native.pop('AccountReset.json')
expected_native = {'ClientTemplate.zip', 'APFPack-template.json', 'MediaReferences.json', 'ServerPayload/Lidgren.Core.dll'}
assert set(native) == set(old_native) - {'AccountReset.json'}
assert {n for n in native if native[n] != old_native[n]} == expected_native
assert not any('/universe/' in n.casefold() or '/secrets/' in n.casefold() or n.casefold().endswith(('.key', '.pfx', '.p12')) for n in native)
assert sha(native['ServerPayload/Server.dll']) == 'd37641645592b80b4e22065d813e65c68ba35b5cbd258b4a0b118d38109df59d'
native['RELEASE.json'] = encode({'version': version, 'files': [{'path': n, 'sha256': sha(data)} for n, data in sorted(native.items())]})
a.output.mkdir(parents=True, exist_ok=True)
target = a.output / f'Aetherfront-Release-{version}.zip'
assert not target.exists(), 'Use a new immutable output directory'
target.write_bytes(archive(native))
result = {'version': version, 'baselineSha256': baseline_sha, 'sha256': sha(target.read_bytes()), 'bytes': target.stat().st_size,
          'changedClientFiles': sorted(expected_client), 'changedNativeFiles': sorted(expected_native), 'removedNativeFiles': ['AccountReset.json'],
          'unchangedMediaArchives': True, 'unchangedGameplayProgressionAndIdentityCode': True,
          'productionConfigurationIncluded': False, 'oneTimeAccountOperationIncluded': False, 'transportAudits': audits}
(a.output / 'BUILD-RESULT.json').write_bytes(encode(result)); print(json.dumps(result, indent=2))
