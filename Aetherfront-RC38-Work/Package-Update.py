"""Save a local review bundle, deliberately separate from the deployment format."""
from pathlib import Path
import hashlib
import json
import sys
import zipfile

controller = Path(sys.argv[1]).resolve()
build = controller/'build'
out = build/'rc38-candidate'
out.mkdir(exist_ok=True)
sha = lambda data: hashlib.sha256(data).hexdigest()
baseline = build/'rc35-server/Server.dll'
assert baseline.read_bytes() == (build/'rc37-release/Native/ServerPayload/Server.dll').read_bytes(), 'Reviewed server must match RC37 native payload'
checks = {
    'presentation': (build/'rc38-presentation/TEST-RESULTS.txt', 67),
    'clock_boundary': (build/'rc38-clock-server/BOUNDARY-TESTS.txt', 72),
    'server_continuity': (build/'rc38-clock-server/SERVER-REGRESSIONS.txt', 110),
    'client_clock': (build/'rc38-clock-client/CLIENT-CLOCK-TESTS.txt', 33),
    'client_continuity': (build/'rc38-clock-client/CONTINUITY-MONO-REGRESSIONS.txt', 401),
    'expansion_repair': (build/'rc38-expansion-bootstrap/TEST-RESULTS.txt', 138),
}
for name, (file, count) in checks.items():
    assert f'PASS {count} ' in file.read_text(encoding='utf-8-sig'), f'Passing evidence absent: {name}'
inputs = {}
overlay = build/'rc38-presentation/overlay'
for relative in (
    'GameData/AetherfrontCompatibility/ParallaxNoCollisions.cfg',
    'GameData/LunaMultiplayer/Button/LMPButton.png',
    'GameData/LunaMultiplayer/Flags/lmp.png',
    'GameData/LunaMultiplayer/Presentation/APF.png',
    'GameData/LunaMultiplayer/Presentation/APF.ico',
    'GameData/LunaMultiplayer/Plugins/AetherfrontPresentationClient.dll',
):
    inputs['ClientOverlay/'+relative] = overlay/relative
inputs['ClientOverlay/GameData/LunaMultiplayer/Plugins/AetherfrontContinuityClient.dll'] = build/'rc38-clock-client/AetherfrontContinuityClient.dll'
inputs['ClientOverlay/GameData/LunaMultiplayer/Plugins/AetherfrontExpansionBootstrap.dll'] = build/'rc38-expansion-bootstrap/AetherfrontExpansionBootstrap.dll'
for name in ('Server.dll', 'AetherfrontContinuityServer.dll'):
    inputs['ServerOverlay/'+name] = build/'rc38-clock-server'/name
theme = build/'rc38-themes'
if theme.exists():
    validation = json.loads((theme/'CATALOG-VALIDATION-CLR.json').read_text())
    assert validation['valid'] and validation['version']=='kesas-rc38'
    mono = json.loads((theme/'CATALOG-VALIDATION-MONO.json').read_text(encoding='utf-8-sig'))
    assert mono['valid'] and mono['catalogSha256']==validation['catalogSha256']
    dll = json.loads((theme/'DLL-VALIDATION.json').read_text(encoding='utf-8-sig'))
    assert dll['metadataDependencyResolution']=='passed'
    for name in ('install-catalog.json','APFPack-template.json','MediaReferences.json','THEME-RESULTS.json','DLL-VALIDATION.json'):
        inputs['PackMetadata/'+name] = theme/name
    for file in (theme/'Media').glob('*.zip'):
        inputs['Media/'+file.name] = file
inventory = []
for name, source in inputs.items():
    data = source.read_bytes()
    target = out/name
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_bytes(data)
    inventory.append({'path': name, 'sizeBytes': len(data), 'sha256': sha(data)})
report = {
    'status': 'Unpublished local review candidate; not an updater payload or starter',
    'prospectiveRelease': 'kesas-rc38', 'sourceBase': '900212a',
    'features': ['APF toolbar, flag and Windows taskbar branding', 'UI-safe WASD camera wheel reads',
                 'Native borderless window without exclusive-fullscreen focus minimization',
                 'Parallax scatter collisions disabled with global level -1',
                 'Signed DLC repair before stock loading with atomic rollback and crash recovery',
                 'ZTheme and HUDReplacer managed packages shared by Regular and Potato',
                 'Monotonic own-subspace warp boundary with forward-only client acknowledgement'],
    'tests': {name: count for name, (_, count) in checks.items()},
    'totalAssertions': sum(count for _, count in checks.values()),
    'baselineReproduced': 'Actual RC37 native server enters rollback/disconnect path at 50 ms offset skew',
    'nativeServerAudit': json.loads((build/'rc38-clock-server/Server.dll.clock-audit.json').read_text()),
    'pending': ['Exact Escape/on-rails/physics warp sequence in Unity', 'Live VAB/SPH scrolling and taskbar/Alt-Tab behavior',
                'Live ZTheme/HUDReplacer KAL and editor checks; upstream recommends unpublished HUDReplacer 1.4.3',
                'Fresh-launch Parallax log readback must show collisionLevel = -1',
                'Packet loss/reordering and multiplayer life-support verification',
                'Stock auto-warp UT overshoot correction and non-clock disconnect classification',
                'Complete signed release, profile inventories/catalogs and updater gates'],
    'files': inventory, 'productionModified': False, 'currentClientModified': True,
    'currentClientChanges': ['Added only ParallaxNoCollisions.cfg; loaded runtime remains unchanged until restart'],
    'parallax': {'savedProfile':'full (Regular)','lastLoadedLevel':4,'configuredLevel':-1,'freshLaunchVerified':False},
    'currentClientRestarted': False, 'pushed': False, 'signed': False,
}
completed = build/'rc38-release/RESULTS.json'
if completed.exists():
    final = json.loads(completed.read_text())
    assert final['version']=='kesas-rc38' and final['automatedAssertions']==report['totalAssertions']
    report['status']='Completed local update candidate; unpublished; see remaining explicitly skipped interaction/multiplayer checks'
    report['pending']=final['skipped']
    report['localAcceptance']=final['passed']
    report['nativeRelease']=final['release']
    report['parallax']['disposableTestLoadedLevel']=-1
    report['parallax']['disposableTestRuntimeVerified']=True
    report['productionSigningHeld']=True
(out/'CANDIDATE.json').write_text(json.dumps(report, indent=2)+'\n')
archive = out/'Aetherfront-RC38-LOCAL-REVIEW.zip'
with zipfile.ZipFile(archive, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as z:
    for item in inventory:
        z.write(out/item['path'], item['path'])
    z.write(out/'CANDIDATE.json', 'CANDIDATE.json')
with zipfile.ZipFile(archive) as z:
    assert z.testzip() is None
    assert len(z.namelist()) == len(inventory)+1
    for item in inventory:
        assert sha(z.read(item['path'])) == item['sha256']
(out/'BUNDLE-SHA256.txt').write_text(sha(archive.read_bytes())+'  '+archive.name+'\n')
print(json.dumps({'path': str(out), 'bytes': archive.stat().st_size, 'assertions': report['totalAssertions'],
                  'files': len(inventory), 'published': False}, indent=2))
