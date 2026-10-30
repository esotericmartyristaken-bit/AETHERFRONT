"""Assemble the unpublished native RC38 bundle from the exact local RC37 release.

No private key, game save or production endpoint is read or modified. Signing
remains the guarded host publisher's responsibility after explicit publication.
"""
import argparse
import hashlib
import json
from pathlib import Path
import zipfile

p = argparse.ArgumentParser()
p.add_argument('controller', type=Path)
a = p.parse_args()
b = a.controller.resolve() / 'build'
out = b / 'rc38-release'
native = out / 'Native'
native.mkdir(parents=True, exist_ok=True)
version = 'kesas-rc38'
sha = lambda data: hashlib.sha256(data).hexdigest()
js = lambda value: (json.dumps(value, separators=(',', ':'))+'\n').encode()
load = lambda path: json.loads(path.read_text(encoding='utf-8-sig'))

def archive(path, files):
    with zipfile.ZipFile(path, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for name, data in sorted(files.items()):
            assert not name.startswith('/') and '\\' not in name and ':' not in name
            assert '..' not in Path(name).parts
            info = zipfile.ZipInfo(name, (2026, 10, 30, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            z.writestr(info, data)
    with zipfile.ZipFile(path) as z:
        assert z.testzip() is None and len(z.namelist()) == len(files)
        assert all(z.read(name) == data for name, data in files.items())

baseline = b / 'rc37-release/Native'
release = load(baseline / 'RELEASE.json')
assert release['version'] == 'kesas-rc37'
for record in release['files']:
    assert sha((baseline / record['path']).read_bytes()) == record['sha256']
with zipfile.ZipFile(baseline / 'ClientTemplate.zip') as z:
    files = {n: z.read(n) for n in z.namelist()}
inventory = load(b / 'rc38-candidate/CANDIDATE.json')
assert inventory['totalAssertions'] == 821 and not inventory['pushed']
reviewed = {x['path']: x['sha256'] for x in inventory['files']}
original = {n: sha(data) for n, data in files.items()}
prefix = 'GameData/LunaMultiplayer/Plugins/'
for name, source in {
    prefix+'LmpClient.dll': b/'rc38-release-client/LmpClient.dll',
    prefix+'KesasInstall.dll': b/'rc38-engine/KesasInstall.dll',
    prefix+'AetherfrontContinuityClient.dll': b/'rc38-clock-client/AetherfrontContinuityClient.dll',
    prefix+'AetherfrontExpansionBootstrap.dll': b/'rc38-expansion-bootstrap/AetherfrontExpansionBootstrap.dll',
}.items():
    files[name] = source.read_bytes()
for f in (b/'rc38-presentation/overlay').rglob('*'):
    if f.is_file(): files[f.relative_to(b/'rc38-presentation/overlay').as_posix()] = f.read_bytes()
client_audit = load(b/'rc38-release-client/version-audit.json')
assert client_audit['version'] == version and len(client_audit['changed']) == 1
assert client_audit['inputSha256'].lower() == original[prefix+'LmpClient.dll']
assert client_audit['outputSha256'].lower() == sha(files[prefix+'LmpClient.dll'])
for name, data in files.items():
    if 'ClientOverlay/'+name in reviewed: assert sha(data) == reviewed['ClientOverlay/'+name]
engine = load(b/'rc38-engine/method-audit.json')
assert engine['allOtherMethodsUnchanged'] and len(engine['engineChanges']) == 2
assert engine['newSha256'].lower() == sha(files[prefix+'KesasInstall.dll'])

catalog = load(b/'rc38-themes/install-catalog.json')
server = load(b/'rc38-themes/APFPack-template.json')
assert catalog['version'] == server['packVersion'] == version
for profile in (catalog['full'], catalog['potato'], server):
    assert profile['packVersion'] == profile['requiredClientVersion'] == version
    rules = [r for r in profile['clientOnly'] if 'GameData/LunaMultiplayer' in r['pathRoots']]
    # The signed client inventory owns the protected multiplayer root. Never
    # add an overlapping client-only exception merely to update its DLLs.
    assert len(rules) <= 1
    assert rules or any(x.casefold() == 'lunamultiplayer' for x in profile['protectedRoots'])
    if rules:
        rule = rules[0]
        for name, data in files.items():
            if not name.startswith(prefix) or not name.endswith('.dll'): continue
            rule['dlls'] = [x for x in rule['dlls'] if x['path'].casefold() != name.casefold()]
            rule['dlls'].append({'path': name, 'sha256': sha(data)})
files['GameData/LunaMultiplayer/Launcher/install-catalog.json'] = js(catalog)
settings = json.loads(files['GameData/LunaMultiplayer/Data/APFClient.json'])
assert settings['clientVersion'] == 'kesas-rc37'
settings['clientVersion'] = version
files['GameData/LunaMultiplayer/Data/APFClient.json'] = js(settings)
manifest = 'KESAS/PATCH-MANIFEST.json'
files[manifest] = js({'version': version, 'files': [
    {'path': n, 'bytes': len(data), 'sha256': sha(data)}
    for n, data in sorted(files.items()) if n != manifest]})
assert not any('ControllerTest' in n or 'Probe' in n or n.endswith('.key') or '/saves/' in n.lower() for n in files)
archive(native/'ClientTemplate.zip', files)
(out/'install-catalog.json').write_bytes(js(catalog))
(native/'APFPack-template.json').write_bytes(js(server))
for name in ('MediaReferences.json',):
    (native/name).write_bytes((b/'rc38-themes'/name).read_bytes())
payload = {f.relative_to(baseline).as_posix(): f.read_bytes()
           for f in baseline.rglob('*') if f.is_file() and
           (f.relative_to(baseline).as_posix().startswith('ServerPayload/') or f.name == 'AetherfrontTechnology.json')}
for name in ('Server.dll', 'AetherfrontContinuityServer.dll'):
    payload['ServerPayload/'+name] = (b/'rc38-clock-server'/name).read_bytes()
    assert sha(payload['ServerPayload/'+name]) == reviewed['ServerOverlay/'+name]
server_audit = inventory['nativeServerAudit']
assert server_audit['allOtherMethodsUnchanged'] and len(server_audit['changed']) == 1
assert server_audit['inputSha256'].lower() == sha((baseline/'ServerPayload/Server.dll').read_bytes())
assert server_audit['outputSha256'].lower() == sha(payload['ServerPayload/Server.dll'])
for name, data in payload.items():
    target = native/name
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_bytes(data)
all_files = {f.relative_to(native).as_posix(): f.read_bytes()
             for f in native.rglob('*') if f.is_file() and f.name != 'RELEASE.json'}
all_files['RELEASE.json'] = js({'version': version, 'files': [
    {'path': n, 'sha256': sha(data)} for n, data in sorted(all_files.items())]})
(native/'RELEASE.json').write_bytes(all_files['RELEASE.json'])
target = out/'Aetherfront-Release-kesas-rc38-UNPUBLISHED.zip'
archive(target, all_files)
result = {'version': version, 'status': 'Unpublished native release candidate; guarded host signing required',
          'archive': target.name, 'bytes': target.stat().st_size, 'sha256': sha(target.read_bytes()),
          'clientFiles': len(files), 'changedClientFiles': [n for n, data in files.items() if original.get(n) != sha(data)],
          'newMediaDirectory': str(b/'rc38-themes/Media'), 'mediaReferences': len(load(native/'MediaReferences.json')['assets']),
          'productionChanged': False, 'privateStateIncluded': False, 'pushed': False}
(out/'BUILD-RESULT.json').write_bytes(js(result))
(out/'SHA256.txt').write_text(result['sha256']+'  '+target.name+'\n')
print(json.dumps(result, indent=2))
