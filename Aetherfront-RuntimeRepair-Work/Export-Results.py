"""Record bounded, reproducible evidence without changing any installation."""
from pathlib import Path
import hashlib,json,re,sys,zipfile
controller,game=map(lambda x:Path(x).resolve(),sys.argv[1:])
out=controller/'build/runtime-repair-candidate'
source=Path(__file__).resolve().parent
sha=lambda data:hashlib.sha256(data).hexdigest()
counts={}
for runtime in ['CLR','MONO']:
    text=(out/f'TESTS-{runtime}.txt').read_text(encoding='utf-8-sig')
    matches=re.findall(r'PASS (\d+) runtime repair assertions; no KSP process launched',text)
    assert matches==['63'] and 'FAIL ' not in text
    counts[runtime]=int(matches[0])
audit=json.loads((out/'code/METHOD-AUDIT.json').read_text())
assert audit['allOtherMethodsUnchanged'] and len(audit['changedEngineMethods'])==5 and len(audit['changedContinuityMethods'])==1
assert audit['engineOutputSha256']==sha((out/'code/KesasInstall.dll').read_bytes())
assert audit['continuityOutputSha256']==sha((out/'code/AetherfrontContinuityClient.dll').read_bytes())
with zipfile.ZipFile(controller/'build/rc38-public-readback/APFClient-kesas-rc38.zip') as z:
    for name in ['KesasInstall.dll','AetherfrontContinuityClient.dll']:
        assert z.read('GameData/LunaMultiplayer/Plugins/'+name)==(game/'GameData/LunaMultiplayer/Plugins'/name).read_bytes()
assert sha((game/'KSP_x64_Data/Managed/Assembly-CSharp.dll').read_bytes())=='d9e42483f25ee80a9c11d6c1c0a0d29b4ec78c1e08d76c971b71580c9cce51e4'
result={'status':'Scoped repair implementation and headless verification complete; unpublished',
        'tests':counts,'nativeMethodAudit':audit,'kspLaunched':False,'userInstallModified':False,
        'mainRC39GoalResumed':False,'pushed':False,'published':False,
        'verified':['Exact pinned payload replacement and original-DLL recovery copy',
            'Healthy reconnect checks preserve installed DLL timestamp',
            'Missing payload invalidates client-receipt shortcut',
            'Loaded incompatible module defers continuity initialization and requires restart',
            'Tampered payload, forged publisher signature, other builds/runtimes and locked/read-only files refused',
            'Interrupted commit retry and ambiguous-recovery evidence preservation',
            'Actual native client and pack transactions finish without launching KSP',
            'Actual native failure receipt and retry without stale busy state',
            'Runtime-only Regular/Potato repairs preserve graphics selection and preferences',
            'Settings, identity and save markers remain byte-identical'],
        'skipped':['Live Unity restart and server join (user forbids game launch)',
            'Affected player supporting-runtime fingerprint compatibility',
            'Older-helper bootstrap and seamless original-run acceptance',
            'macOS/Linux support'],
        'bootstrapLimitation':json.loads((out/'PACKAGE.json').read_text())['bootstrapLimitation'],
        'sourceFiles':[{'path':p.relative_to(source).as_posix(),'sha256':sha(p.read_bytes())}
            for p in sorted(source.rglob('*')) if p.is_file() and not any(x in ['bin','obj'] for x in p.relative_to(source).parts)]}
content=(json.dumps(result,indent=2)+'\n').encode()
(out/'RESULTS.json').write_bytes(content)
archive=out/'Aetherfront-RuntimeRepair-UNPUBLISHED.zip'
with zipfile.ZipFile(archive) as z:files={n:z.read(n) for n in z.namelist()}
files['RUNTIME-REPAIR-TEST-RESULTS.json']=content
files['RUNTIME-REPAIR-README.txt']=(source/'README.md').read_bytes()
with zipfile.ZipFile(archive,'w',zipfile.ZIP_DEFLATED,compresslevel=9) as z:
    for name,data in sorted(files.items()):z.writestr(name,data)
with zipfile.ZipFile(archive) as z:
    assert z.testzip() is None and all(z.read(n)==v for n,v in files.items())
    assert not any(n.endswith('.key') or '/saves/' in n.casefold() for n in z.namelist())
(out/'SHA256.txt').write_text(sha(archive.read_bytes())+'  '+archive.name+'\n')
print(json.dumps({'tests':counts,'archiveBytes':archive.stat().st_size,'published':False,'userInstallUnchanged':True}))
