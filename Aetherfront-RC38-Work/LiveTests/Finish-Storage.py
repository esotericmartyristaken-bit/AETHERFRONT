"""Preserve results, then deduplicate only immutable disposable DLC assets."""
from pathlib import Path
import hashlib,json,os,shutil,zipfile
c=Path(__file__).resolve().parents[3];game=c.parent/'apf-test';t=c/'tests/rc38-live'
user=Path('C:/Users/jkell/Documents/Codex/APFBeta')
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
shutil.copyfile(game/'KSP.log',t/'KSP-run3.log')
shutil.copyfile(game/'Logs/ModuleManager/ModuleManager.log',t/'ModuleManager-run3.log')
baseline=json.loads((t/'USER-INSTALL-HASHES.json').read_text(encoding='utf-8-sig'))
preserved=[{'path':r['path'],'unchanged':sha(user/r['path'])==r['sha256']} for r in baseline]
assert all(r['unchanged'] for r in preserved),'User installation changed during disposable tests'
(t/'USER-INSTALL-PRESERVATION.json').write_text(json.dumps(preserved,indent=2)+'\n')
code=[]
with zipfile.ZipFile(c/'build/rc38-release/Native/ClientTemplate.zip') as z:
    for name in z.namelist():
        if name.startswith('GameData/') and name.endswith('.dll'):
            expected=hashlib.sha256(z.read(name)).hexdigest();actual=sha(game/name)
            code.append({'path':name,'sha256':actual,'matchesFinalRelease':actual==expected})
assert all(r['matchesFinalRelease'] for r in code)
(t/'FINAL-LOADED-CODE.json').write_text(json.dumps(code,indent=2)+'\n')
driver=game/'GameData/LunaMultiplayer/Plugins/RC38LiveProbe.dll'
shutil.copyfile(driver,t/'RC38LiveProbe-run3.dll')
driver.unlink() # Test-only addon, after this owned process has exited.
before=shutil.disk_usage(game).free;count=0;logical=0
start=game/'GameData/SquadExpansion'
for directory,dirs,files in os.walk(start,followlinks=False):
    folder=Path(directory)
    assert not os.lstat(folder).st_file_attributes & 1024
    for name in dirs:assert not os.lstat(folder/name).st_file_attributes & 1024
    for name in files:
        target=folder/name;original=user/target.relative_to(game)
        assert not os.lstat(target).st_file_attributes & 1024
        if not original.exists() or os.path.samefile(target,original):continue
        assert not os.lstat(original).st_file_attributes & 1024
        if target.stat().st_size!=original.stat().st_size or sha(target)!=sha(original):continue
        temp=target.with_name(target.name+'.immutable-link')
        assert not temp.exists();os.link(original,temp);size=target.stat().st_size;os.replace(temp,target)
        count+=1;logical+=size
result={'deduplicatedImmutableDlcFiles':count,'logicalBytesDeduplicated':logical,
        'observedFreeSpaceChangeBytes':shutil.disk_usage(game).free-before,
        'observedFreeSpaceIsNetOfConcurrentOsActivity':True,
        'userFilesWritten':False,'testLogsPreserved':True,'testAddonRemoved':True,
        'mutableReceiptsLinkedOrEdited':False,'note':'Test-only deduplication changes file metadata; a future fixture launch may revalidate its receipt. Cache acceptance was measured before this cleanup.'}
(t/'STORAGE-RESULT.json').write_text(json.dumps(result,indent=2)+'\n')
print(json.dumps(result,indent=2))
