"""Compose RC55 stability changes with the verified RC54 checkpoint/recovery bundle."""
import argparse
from pathlib import Path
import runpy, sys
if sys.flags.optimize: raise RuntimeError('Release assertions must remain enabled')
helpers = runpy.run_path(str(Path(__file__).resolve().parents[1] / 'Aetherfront-RC50-Work/Build-Release.py'))
digest, pack, unpack, read, write = (helpers[name] for name in ('digest','pack','unpack','read_json','write_json'))
BASE_SHA = '3ef979923bea93adadd6859dcf0a07ed480ae4747d39f70a3f2370efd343077f'
PREFIX = 'GameData/LunaMultiplayer/'
def build(baseline, candidate, helper):
    assert digest(baseline) == BASE_SHA
    native = unpack(baseline); original = dict(native)
    manifest = read(native.pop('RELEASE.json'))
    assert manifest['version'] == 'kesas-rc54'
    assert set(native) == {row['path'] for row in manifest['files']}
    assert all(digest(native[row['path']]) == row['sha256'] for row in manifest['files'])
    client = unpack(native['ClientTemplate.zip']); original_client = dict(client)
    for side, relative in [('Server','ServerPayload/Server.dll'),('Client',PREFIX+'Plugins/LmpClient.dll')]:
        name = Path(relative).name; target = native if side == 'Server' else client
        binary = (candidate / side / name).read_bytes(); audit = read((candidate / side / (name+'.audit.json')).read_bytes())
        assert audit['inputSha256'] == digest(target[relative]) and audit['outputSha256'] == digest(binary)
        assert audit['identityPreserved'] and audit['unrelatedMethodsPreserved']
        target[relative] = binary
    assert helper.name == 'AetherfrontStabilityClient.dll' and helper.stat().st_size < 262144
    client[PREFIX+'Plugins/'+helper.name] = helper.read_bytes()
    def bump(value):
        assert value['packVersion'] == value['requiredClientVersion'] == 'kesas-rc54'
        value['packVersion'] = value['requiredClientVersion'] = 'kesas-rc55'
    catalog_path, settings_path, patch_path = PREFIX+'Launcher/install-catalog.json', PREFIX+'Data/APFClient.json', 'KESAS/PATCH-MANIFEST.json'
    catalog, settings = read(client[catalog_path]), read(client[settings_path])
    assert catalog['version'] == settings['clientVersion'] == read(client[patch_path])['version'] == 'kesas-rc54'
    catalog['version'] = settings['clientVersion'] = 'kesas-rc55'; bump(catalog['full']); bump(catalog['potato'])
    client[catalog_path], client[settings_path] = write(catalog), write(settings)
    client[patch_path] = write({'version':'kesas-rc55','files':[{'path':p,'bytes':len(data),'sha256':digest(data)} for p,data in sorted(client.items()) if p != patch_path]})
    template = read(native['APFPack-template.json']); bump(template); native['APFPack-template.json'] = write(template)
    media = read(native['MediaReferences.json']); assert media['applicationVersion'] == 'kesas-rc54'
    media['applicationVersion'] = 'kesas-rc55'; native['MediaReferences.json'] = write(media)
    native['ClientTemplate.zip'] = pack(client)
    client_changes = {p for p in client if p not in original_client or client[p] != original_client[p]}
    assert client_changes == {PREFIX+'Plugins/LmpClient.dll',PREFIX+'Plugins/'+helper.name,catalog_path,settings_path,patch_path}
    allowed = {'ServerPayload/Server.dll','ClientTemplate.zip','APFPack-template.json','MediaReferences.json','RELEASE.json'}
    native['RELEASE.json'] = write({'version':'kesas-rc55','files':[{'path':p,'sha256':digest(data)} for p,data in sorted(native.items())]})
    assert set(native) == set(original) and {p for p in native if native[p] != original[p]} == allowed
    assert not any('probe' in p.lower() or 'test' in p.lower() for p in client if p.endswith('.dll') and p not in original_client)
    return pack(native)
if __name__ == '__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('baseline',type=Path); parser.add_argument('candidate',type=Path); parser.add_argument('helper',type=Path); parser.add_argument('output',type=Path)
    args=parser.parse_args(); data=build(args.baseline.read_bytes(),args.candidate,args.helper)
    with args.output.open('xb') as stream: stream.write(data)
    print(f'kesas-rc55 {digest(data)} {len(data)} bytes; original mod/media archives and RC54 checkpoints preserved')
