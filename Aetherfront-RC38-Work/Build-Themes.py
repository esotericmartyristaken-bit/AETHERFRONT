"""Stage pinned theme packages and both profile/server manifests; never upload."""
from pathlib import Path, PurePosixPath
import hashlib
import io
import json
import re
import sys
import zipfile
from PIL import Image

controller = Path(sys.argv[1]).resolve()
source = Path(__file__).resolve().parent
lock = json.loads((source/'Theme-Dependencies.json').read_text())
inputs = controller/'build/rc38-theme-inputs'
out = controller/'build/rc38-themes'
media = out/'Media'
media.mkdir(parents=True, exist_ok=True)
base = controller/'build/rc37-release'
catalog = json.loads((base/'install-catalog.json').read_text())
server = json.loads((base/'Native/APFPack-template.json').read_text())
references = json.loads((base/'Native/MediaReferences.json').read_text())
sha = lambda data: hashlib.sha256(data).hexdigest()
checks = []
packages = []
for root in ('ZTheme', 'HUDReplacer'):
    spec = lock[root]
    archive = inputs/spec['archive']
    assert sha(archive.read_bytes()) == spec['sha256'], 'Upstream release digest mismatch'
    files = {}
    with zipfile.ZipFile(archive) as z:
        assert z.testzip() is None
        for item in z.infolist():
            if item.is_dir(): continue
            name = item.filename
            if root == 'HUDReplacer':
                assert name.startswith('GameData/')
                name = name[len('GameData/'):]
            path = PurePosixPath(name)
            assert not path.is_absolute() and path.parts[0] == root and '..' not in path.parts and '\\' not in name and ':' not in name
            assert ((item.external_attr >> 16) & 0xf000) != 0xa000
            assert name.lower() not in {n.lower() for n in files}
            assert item.file_size <= 16*1024*1024
            data = z.read(item)
            if name.lower().endswith('.png'):
                with Image.open(io.BytesIO(data)) as image: image.verify()
            assert Path(name).suffix.lower() not in ('.exe', '.ps1', '.bat', '.cmd', '.vbs')
            files[name] = data
    assert root+'/LICENSE' in files
    if root == 'ZTheme': assert not any(n.lower().endswith('.dll') for n in files)
    assert not any(re.search(r'lmpbutton|lunamultiplayer', n, re.I) for n in files), 'Theme would replace APF branding'
    if root == 'ZTheme':
        config = files['ZTheme/config.cfg'].decode()
        for directory in re.findall(r'^\s*filePath\s*=\s*([^\r\n]+)',config,re.M):
            prefix=directory.strip().removeprefix('GameData/').rstrip('/')+'/'
            assert any(n.startswith(prefix) for n in files), 'Missing theme directory: '+directory
    for name,data in files.items():
        if name.endswith(('.dll','.cfg','.version')):
            checkFile=out/'Checks/GameData'/name
            checkFile.parent.mkdir(parents=True,exist_ok=True)
            checkFile.write_bytes(data)
    normalized = io.BytesIO()
    with zipfile.ZipFile(normalized, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for name, data in sorted(files.items()):
            item = zipfile.ZipInfo(name, (2026,10,30,0,0,0)); item.compress_type = zipfile.ZIP_DEFLATED
            z.writestr(item, data)
    data = normalized.getvalue()
    digest = sha(data)
    asset = f'kesas-rc38-{root.lower()}-{digest}.zip'
    (media/asset).write_bytes(data)
    package = {'id': 'kesas-rc38-'+root.lower(), 'version': spec['version']+'-sha-'+digest[:16],
               'archiveUrl': 'https://217.216.49.250:18866/'+asset, 'sha256': digest,
               'sizeBytes': len(data), 'destination': 'GameData', 'stripPrefix': root}
    for mode in ('full','potato'):
        assert not any(p['id'] == package['id'] or p.get('stripPrefix') == root for p in catalog[mode]['managedPackages'])
        catalog[mode]['managedPackages'].append(package.copy())
    catalog['sizes'].append({'id': package['id'], 'expandedBytes': sum(map(len,files.values()))})
    server['clientOnly'].append({'id': 'rc38-'+root.lower(), 'pathRoots': ['GameData/'+root],
        'dlls': [{'path': 'GameData/'+n, 'sha256':sha(b)} for n,b in sorted(files.items()) if n.lower().endswith('.dll')]})
    references['assets'].append({'name': asset, 'sha256': digest, 'sizeBytes': len(data)})
    packages.append({'root':root, 'package':package, 'upstream':spec, 'files':len(files),
                     'expandedBytes':sum(map(len,files.values())), 'licensesPreserved':True})
    checks.append(root+': upstream SHA, ZIP CRC, paths, texture decoding, executable exclusions, license and APF-branding checks passed')

catalog['version'] = 'kesas-rc38'
for manifest in (catalog['full'],catalog['potato'],server):
    manifest['packVersion'] = manifest['requiredClientVersion'] = 'kesas-rc38'
references['applicationVersion'] = 'kesas-rc38'
for name,value in [('install-catalog.json',catalog),('APFPack-template.json',server),('MediaReferences.json',references)]:
    (out/name).write_text(json.dumps(value,separators=(',',':'))+'\n')
report = {'version':'kesas-rc38', 'packages':packages, 'checks':checks, 'profiles':['regular','potato'],
          'sharedDownloadReuse':True, 'existingDependencies':['ModuleManager','HarmonyKSP'],
          'upstreamVersionNote':lock['upstreamRecommendation'], 'published':False,
          'liveGameModified':False, 'liveHudAndKalTests':'pending'}
(out/'THEME-RESULTS.json').write_text(json.dumps(report,indent=2)+'\n')
print(json.dumps({'packages':[{k:p[k] for k in ('root','files','expandedBytes')} for p in packages],
                  'downloadBytes':sum(p['package']['sizeBytes'] for p in packages),'published':False},indent=2))
