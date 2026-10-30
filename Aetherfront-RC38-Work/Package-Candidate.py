"""Package only the local overlay; never sign, upload or modify an install."""
from pathlib import Path
import hashlib
import json
import sys
import zipfile

build = Path(sys.argv[1]).resolve()
overlay = build/'overlay'
tests = (build/'TEST-RESULTS.txt').read_text(encoding='utf-8-sig')
assert 'PASS 67 assertions:' in tests, 'Run the candidate tests before packaging'
paths = sorted(p for p in overlay.rglob('*') if p.is_file())
expected = {
    'GameData/AetherfrontCompatibility/ParallaxNoCollisions.cfg',
    'GameData/LunaMultiplayer/Button/LMPButton.png',
    'GameData/LunaMultiplayer/Flags/lmp.png',
    'GameData/LunaMultiplayer/Presentation/APF.png',
    'GameData/LunaMultiplayer/Presentation/APF.ico',
    'GameData/LunaMultiplayer/Plugins/AetherfrontPresentationClient.dll',
}
assert {p.relative_to(overlay).as_posix() for p in paths} == expected
digest = lambda data: hashlib.sha256(data).hexdigest()
inventory = [{'path': p.relative_to(overlay).as_posix(), 'sizeBytes': p.stat().st_size,
              'sha256': digest(p.read_bytes())} for p in paths]
archive = build/'Aetherfront-RC38-Presentation-UNPUBLISHED.zip'
with zipfile.ZipFile(archive, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as z:
    for p in paths:
        info = zipfile.ZipInfo(p.relative_to(overlay).as_posix(), (2026, 10, 29, 0, 0, 0))
        info.compress_type = zipfile.ZIP_DEFLATED
        z.writestr(info, p.read_bytes())
with zipfile.ZipFile(archive) as z:
    assert z.testzip() is None and set(z.namelist()) == expected
    for item in inventory:
        assert digest(z.read(item['path'])) == item['sha256']
report = {
    'status': 'Local overlay candidate; not signed, published or installed',
    'sourceBase': '900212a', 'prospectiveRelease': 'kesas-rc38',
    'archive': archive.name, 'sha256': digest(archive.read_bytes()), 'bytes': archive.stat().st_size,
    'files': inventory,
    'passed': ['Build: zero warnings/errors', '67 assertions through actual KSP Mono',
               'Four shipped WASD wheel sites and existing input-lock patches coexist',
               'Hidden native window icons, borderless bounds, no activation/visibility changes',
               'Logo asset inspection and ZIP inventory/CRC/hash verification'],
    'pending': ['Live VAB and SPH part-list/viewport scrolling',
                'Live KSP taskbar icon and Alt-Tab/multimonitor behavior',
                'Complete signed-release/catalog generation and normal updater tests'],
    'currentGameModified': False, 'currentGameRestarted': False,
    'serverModified': False, 'pushed': False,
}
(build/'CANDIDATE.json').write_text(json.dumps(report, indent=2)+'\n', encoding='utf-8')
print(json.dumps({'archive': str(archive), 'bytes': archive.stat().st_size,
                  'sha256': report['sha256'], 'files':len(inventory), 'published':False}, indent=2))
