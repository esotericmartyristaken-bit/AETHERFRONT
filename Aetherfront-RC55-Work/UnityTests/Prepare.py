"""Prepare only the two existing owned test roots; never reset saves/identity."""
from pathlib import Path
import hashlib, io, json, os, shutil, zipfile

source = Path(__file__).resolve().parents[2]
work = source.parent
evidence = work / 'rc50-input/rc55-stability'
evidence.mkdir(exist_ok=True)
baseline = work / 'rc50-input/rc53-checkpoint/Aetherfront-Release-kesas-rc54.zip'
assert hashlib.sha256(baseline.read_bytes()).hexdigest() == '3ef979923bea93adadd6859dcf0a07ed480ae4747d39f70a3f2370efd343077f'
native = zipfile.ZipFile(baseline)
client = zipfile.ZipFile(io.BytesIO(native.read('ClientTemplate.zip')))
probe = source / 'Aetherfront-RC55-Work/UnityTests/bin/Debug/net472/RC55TestProbe.dll'
helper = source / 'Aetherfront-RC55-Work/Client/bin/Debug/net472/AetherfrontStabilityClient.dll'
candidate = evidence / 'candidate'
def atomic(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + '.rc55tmp')
    temporary.write_bytes(data); os.replace(temporary, path)
def js(path, value): atomic(path, json.dumps(value, indent=2).encode())

for root_name, port in [('apf-test', 28876), ('apf-peer', 28877)]:
    root = work / root_name
    assert root.resolve() == (work / root_name).absolute() and (root / 'KSP_x64.exe').stat().st_size == 661336
    backup = evidence / ('before-' + root_name + '.zip')
    if not backup.exists():
        with zipfile.ZipFile(backup, 'x', compression=zipfile.ZIP_DEFLATED) as archive:
            for relative in ['settings.cfg', 'KSP.log', 'GameData/LunaMultiplayer/Data/APFPackState.json', 'GameData/LunaMultiplayer/Data/APFClient.json']:
                path = root / relative
                if path.exists(): archive.write(path, relative)
    for row in client.infolist():
        if not row.is_dir():
            assert not row.filename.startswith('/') and '..' not in Path(row.filename).parts
            atomic(root / row.filename, client.read(row))
    plugins = root / 'GameData/LunaMultiplayer/Plugins'
    atomic(plugins / 'LmpClient.dll', (candidate / 'Client/LmpClient.dll').read_bytes())
    atomic(plugins / helper.name, helper.read_bytes())
    atomic(root / 'GameData/RC55TestProbe/Plugins' / probe.name, probe.read_bytes())
    data = root / 'GameData/LunaMultiplayer/Data'
    js(data / 'APFClient.json', {'enabled': True, 'clientVersion': 'kesas-rc55'})
    js(data / 'APFPackState.json', {'packId': 'kesas', 'packVersion': 'kesas-rc55', 'packages': []})
    atomic(data / 'APFLocalServer.txt', f'127.0.0.1:{port}'.encode())
    settings = (root / 'settings.cfg').read_text()
    import re
    settings = re.sub(r'(?m)^(MASTER|SHIP|AMBIENCE|MUSIC|UI|VOICE)_VOLUME\s*=.*$', lambda m: m[1] + '_VOLUME = 0', settings)
    atomic(root / 'settings.cfg', settings.encode())

runtime = work / 'rc50-input/rc53-checkpoint/staging-2/PersonalServer'
server = evidence / 'staging/PersonalServer'
assert not server.exists(), 'Reuse existing fixture explicitly; do not reset it.'
server.mkdir(parents=True)
for path in runtime.iterdir():
    if path.is_file() and path.suffix.lower() in ('.dll', '.exe', '.json', '.dat'):
        if path.name == 'Server.dll': shutil.copy2(candidate / 'Server/Server.dll', server / path.name)
        else: os.link(path, server / path.name)
shutil.copytree(runtime / 'Config', server / 'Config')
for name in ('AetherfrontPersonalServer.dll', 'AetherfrontContinuityServer.dll'):
    atomic(server / name, native.read('ServerPayload/' + name))
for name in ('Universe', 'logs'): (server / name).mkdir()
pack = json.loads((server / 'Config/APFPack.json').read_text())
pack.update(serverId='aetherfront-local-staging', packVersion='kesas-rc55', requiredClientVersion='kesas-rc55')
pack['clientOnly'].append({'id':'rc55-test-probe', 'pathRoots':['GameData/RC55TestProbe'], 'dlls':[{'path':'GameData/RC55TestProbe/Plugins/RC55TestProbe.dll','sha256':hashlib.sha256(probe.read_bytes()).hexdigest()}]})
for name in ['AetherfrontBreakingGround','AetherfrontMakingHistory','HUDReplacer','WasdEditorCamera','ZTheme']:
    root = work / 'apf-test'
    pack['clientOnly'].append({'id':'rc55-fixture-'+name.lower(),'pathRoots':['GameData/'+name],
        'dlls':[{'path':p.relative_to(root).as_posix(),'sha256':hashlib.sha256(p.read_bytes()).hexdigest()} for p in (root/'GameData'/name).rglob('*.dll')]})
js(server / 'Config/APFPack.json', pack)
career = json.loads((server / 'Config/APFCareer.json').read_text()); career.update(mode='Sandbox', saveNamespace='APF/RC55Loopback', displayName='RC55 isolated warp reproduction', profileId='rc55-loopback')
js(server / 'Config/APFCareer.json', career)
general = server / 'Config/GeneralSettings.xml'
atomic(general, general.read_text().replace('<GameMode>Science</GameMode>', '<GameMode>Sandbox</GameMode>').encode())
connection = server / 'Config/ConnectionSettings.xml'
assert '<ListenAddress>127.0.0.1</ListenAddress>' in connection.read_text() and '<Port>28875</Port>' in connection.read_text()
assert '<RegisterWithMasterServer>false</RegisterWithMasterServer>' in (server / 'Config/MasterServerSettings.xml').read_text()
print('Prepared owned modded fixtures and isolated loopback server; prior logs/preferences saved, saves/identities retained; no production changes.')
