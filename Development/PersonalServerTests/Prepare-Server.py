"""Create one isolated Science server from the preserved Windows runtime.

Only immutable runtime files are hardlinked. Configuration is copied; the
universe, player identities and server signing keys are never imported.
"""
import hashlib, json, pathlib, shutil, stat, os

controller = pathlib.Path(__file__).resolve().parents[3]
source = controller / 'staging/Server'
candidate = controller / 'build/rc33-server'
destination = controller / 'staging/PersonalServer'
assert not destination.exists(), 'Preserve an existing test server and evidence'
replacements = {'Server.dll', 'AetherfrontCore.dll', 'AetherfrontPersonalServer.dll', 'Server.deps.json'}
for root in (source, candidate, destination.parent):
    for ancestor in [root, *root.parents]:
        assert not ancestor.is_symlink() and not (ancestor.stat().st_file_attributes & stat.FILE_ATTRIBUTE_REPARSE_POINT), 'Reparse path refused'
for name in replacements:
    assert (candidate/name).is_file(), name
destination.mkdir()
for path in source.iterdir():
    if path.is_file() and path.suffix in ('.dll', '.exe', '.json') and path.name not in replacements:
        assert not path.is_symlink() and not (path.stat().st_file_attributes & stat.FILE_ATTRIBUTE_REPARSE_POINT)
        os.link(path, destination/path.name)
for name in replacements:
    shutil.copy2(candidate/name, destination/name)
for path in (source/'Config').rglob('*'):
    assert not path.is_symlink() and not (path.stat().st_file_attributes & stat.FILE_ATTRIBUTE_REPARSE_POINT), 'Reparse config refused'
shutil.copytree(source/'Config', destination/'Config', symlinks=False)
general = destination/'Config/GeneralSettings.xml'
text = general.read_text(encoding='utf-8-sig')
assert '<GameMode>Sandbox</GameMode>' in text
general.write_text(text.replace('<GameMode>Sandbox</GameMode>', '<GameMode>Science</GameMode>').replace('<Cheats>true</Cheats>', '<Cheats>false</Cheats>'), encoding='utf-8')
connection = (destination/'Config/ConnectionSettings.xml').read_text(encoding='utf-8-sig')
assert '<ListenAddress>127.0.0.1</ListenAddress>' in connection and '<Port>28865</Port>' in connection
profile = destination/'Config/APFCareer.json'
data = json.loads(profile.read_text(encoding='utf-8-sig'))
data.update(profileId='aetherfront-personal-science-test', displayName='Personal Science local test', saveNamespace='APF/PersonalScienceTest', mode='Science')
profile.write_text(json.dumps(data, indent=2)+'\n', encoding='utf-8')
manifest = destination/'Config/APFPack.json'
pack = json.loads(manifest.read_text(encoding='utf-8-sig'))
# Keep the release version at the immutable baseline for this isolated code
# test. This fixture is not a new release or a skipped-version updater test.
pack['packVersion'] = pack['requiredClientVersion'] = 'kesas-rc31'
manifest.write_text(json.dumps(pack, separators=(',', ':'))+'\n', encoding='utf-8')
report = {'root':str(destination), 'mode':'Science', 'loopbackOnly':True,
          'productionChanged':False, 'productionKeysUsed':False, 'universeImported':False,
          'candidateFiles':{n:hashlib.sha256((destination/n).read_bytes()).hexdigest() for n in sorted(replacements)}}
(controller/'tests/personal-server-fixture.json').write_text(json.dumps(report, indent=2)+'\n', encoding='utf-8')
print(json.dumps(report, indent=2))
