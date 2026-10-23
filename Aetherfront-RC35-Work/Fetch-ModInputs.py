"""Download explicit upstream inputs for local inspection. Does not install/publish.

Retain only one archive per upstream asset; extraction and licensing decisions are
separate. HTTPS origin, recorded release sizes and ZIP members are checked before
promoting a download. The recorded digest identifies these exact input bytes; it
does not imply that an author digitally signed an otherwise unsigned release.
"""
import argparse
import hashlib
import json
import pathlib
import stat
import subprocess
import urllib.parse
import urllib.request
import zipfile

p = argparse.ArgumentParser()
p.add_argument('--output', required=True, type=pathlib.Path)
a = p.parse_args()
inputs = json.loads(pathlib.Path(__file__).with_name('ModInputs.json').read_text())['inputs']
a.output.mkdir(parents=True, exist_ok=True)
results = []
for item in inputs:
    url = urllib.parse.urlsplit(item['url'])
    assert url.scheme == 'https' and url.netloc in ('github.com', 'spacedock.info') and not url.query and not url.fragment
    name = item.get('file') or urllib.parse.unquote(url.path.rsplit('/', 1)[-1])
    assert name == pathlib.PureWindowsPath(name).name and name.endswith('.zip')
    target = a.output / name
    temporary = target.with_suffix('.download')
    if not target.exists():
        assert not temporary.exists(), 'Preserve/review the previous partial download before retrying'
        # A socket timeout alone permits a stalled stream that trickles bytes to
        # wait indefinitely. Bound total duration and minimum sustained speed.
        subprocess.run(['curl.exe', '--fail', '--location', '--silent', '--show-error',
                        '--proto', '=https', '--proto-redir', '=https', '--max-time', '300',
                        '--speed-time', '30', '--speed-limit', '1024',
                        '--max-filesize', str(item['bytes']), '--user-agent', 'Aetherfront-Local-Mod-Review',
                        '--output', str(temporary), item['url']], check=True, timeout=315)
        assert temporary.stat().st_size == item['bytes'], 'Release size changed'
        temporary.rename(target)
    assert target.stat().st_size == item['bytes']
    hash_value = hashlib.sha256()
    with target.open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            hash_value.update(block)
    with zipfile.ZipFile(target) as archive:
        names = set()
        expanded = 0
        for info in archive.infolist():
            name = info.filename
            path = pathlib.PurePosixPath(name)
            assert not path.is_absolute() and '\\' not in name and ':' not in name
            assert all(part not in ('..', '.') for part in path.parts)
            assert not stat.S_ISLNK(info.external_attr >> 16)
            assert name.casefold() not in names, 'Case-aliased archive entries'
            names.add(name.casefold())
            expanded += info.file_size
        assert expanded <= 2 * 1024 ** 3 and len(names) <= 20000
        assert archive.testzip() is None, 'Archive CRC failure'
        details = {'mod': item['mod'], 'version': item['version'], 'url': item['url'],
                   'file': target.name, 'bytes': target.stat().st_size, 'sha256': hash_value.hexdigest(),
                   'expandedBytes': expanded, 'entries': len(names),
                   'licenseEntries': [n for n in archive.namelist() if 'license' in n.lower() or 'licence' in n.lower()],
                   'dllEntries': [n for n in archive.namelist() if n.lower().endswith('.dll')]}
        results.append(details)
    (a.output / 'INPUT-AUDIT.json').write_text(json.dumps(results, indent=2) + '\n')
    print(item['mod'] + ' ' + item['version'] + ': verified archive ' + hash_value.hexdigest(), flush=True)
