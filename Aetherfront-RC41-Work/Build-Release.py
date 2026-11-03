"""Build RC41 from the authenticated RC40 bundle; keep all media unchanged."""
from pathlib import Path
import argparse
import hashlib
import io
import json
import zipfile

p = argparse.ArgumentParser()
p.add_argument('--baseline', type=Path, required=True)
p.add_argument('--code', type=Path, required=True)
p.add_argument('--account-reset', type=Path, required=True)
p.add_argument('--output', type=Path, required=True)
a = p.parse_args()
sha = lambda b: hashlib.sha256(b).hexdigest()
load = lambda b: json.loads(b.decode('utf-8-sig'))
encode = lambda o: (json.dumps(o, ensure_ascii=False, separators=(',', ':')) + '\n').encode()
version = 'kesas-rc41'
baseline_sha = '84fc1d34a3e5c81debd36f55de90d32818294e698dcb1a3e0964cc6fa04b4df4'

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
r = load(native.pop('RELEASE.json'))
assert r['version'] == 'kesas-rc40' and set(native) == {f['path'] for f in r['files']}
assert all(sha(native[f['path']]) == f['sha256'] for f in r['files'])
old_native = native.copy()
client = read_zip(native['ClientTemplate.zip']); old_client = client.copy()
prefix = 'GameData/LunaMultiplayer/'
audits = {}
for mode, filename, target in [('client', 'LmpClient.dll', prefix + 'Plugins/LmpClient.dll'), ('server', 'Server.dll', 'ServerPayload/Server.dll')]:
    audit = load((a.code / (filename + '.audit.json')).read_bytes())
    assert audit['mode'] == mode and audit['version'] == version and audit['allOtherMethodsUnchanged'] and audit['authenticationConditionsUnchanged']
    data = (a.code / filename).read_bytes(); assert sha(data) == audit['outputSha256']
    if mode == 'client':
        assert sha(client[target]) == audit['inputSha256']; client[target] = data
    else:
        assert sha(native[target]) == audit['inputSha256']; native[target] = data
    audits[mode] = audit
catalog_path = prefix + 'Launcher/install-catalog.json'
settings_path = prefix + 'Data/APFClient.json'
manifest_path = 'KESAS/PATCH-MANIFEST.json'
catalog = load(client[catalog_path]); pack = load(native['APFPack-template.json'])
assert catalog['version'] == 'kesas-rc40'; catalog['version'] = version
for profile in (catalog['full'], catalog['potato'], pack):
    assert profile['packVersion'] == profile['requiredClientVersion'] == 'kesas-rc40'
    profile['packVersion'] = profile['requiredClientVersion'] = version
    assert not any(any(root.casefold() == 'gamedata/lunamultiplayer' for root in rule['pathRoots']) for rule in profile['clientOnly'])
client[catalog_path] = encode(catalog)
settings = load(client[settings_path]); assert settings['clientVersion'] == 'kesas-rc40'; settings['clientVersion'] = version
client[settings_path] = encode(settings)
client[manifest_path] = encode({'version': version, 'files': [{'path': n, 'bytes': len(d), 'sha256': sha(d)} for n, d in sorted(client.items()) if n != manifest_path]})
expected_client = {prefix + 'Plugins/LmpClient.dll', catalog_path, settings_path, manifest_path}
assert set(client) == set(old_client) and {n for n in client if client[n] != old_client[n]} == expected_client
native['ClientTemplate.zip'] = archive(client); native['APFPack-template.json'] = encode(pack)
media = load(native['MediaReferences.json']); assert media['applicationVersion'] == 'kesas-rc40'; media['applicationVersion'] = version
native['MediaReferences.json'] = encode(media)
reset = load(a.account_reset.read_bytes())
assert set(reset) == {'schemaVersion', 'applicationVersion', 'playerName', 'expectedAccountId', 'preserveAccountData'}
assert reset['schemaVersion'] == 1 and reset['applicationVersion'] == version and reset['preserveAccountData'] is True
assert isinstance(reset['playerName'], str) and isinstance(reset['expectedAccountId'], str)
native['AccountReset.json'] = encode(reset)
expected_native = {'ClientTemplate.zip', 'APFPack-template.json', 'MediaReferences.json', 'ServerPayload/Server.dll', 'AccountReset.json'}
assert set(native) == set(old_native) | {'AccountReset.json'}
assert {n for n in native if native[n] != old_native.get(n)} == expected_native
assert not any('/universe/' in n.casefold() or '/secrets/' in n.casefold() or n.casefold().endswith(('.key', '.pfx', '.p12')) for n in native)
native['RELEASE.json'] = encode({'version': version, 'files': [{'path': n, 'sha256': sha(d)} for n, d in sorted(native.items())]})
a.output.mkdir(parents=True, exist_ok=True)
target = a.output / f'Aetherfront-Release-{version}.zip'; target.write_bytes(archive(native))
result = {'version': version, 'baselineSha256': baseline_sha, 'sha256': sha(target.read_bytes()), 'bytes': target.stat().st_size,
          'changedClientFiles': sorted(expected_client), 'changedNativeFiles': sorted(expected_native), 'unchangedMediaArchives': True,
          'unchangedGameplayAndProgressionCode': True, 'productionConfigurationIncluded': False, 'audits': audits}
(a.output / 'BUILD-RESULT.json').write_bytes(encode(result)); print(json.dumps(result, indent=2))
