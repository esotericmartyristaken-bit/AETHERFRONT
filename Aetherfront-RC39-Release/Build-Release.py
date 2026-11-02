"""Small RC39 shipping scope: collision policy + signed runtime repair only.

Experimental campaign/arena code is deliberately excluded. The shipping server,
core, media archives, and production configuration/state are preserved.
"""
from pathlib import Path
import hashlib,json,sys,zipfile

c,game=(Path(x).resolve() for x in sys.argv[1:]);b=c/'build';baseline=b/'rc38-release/Native'
out=b/'rc39-release';native=out/'Native';native.mkdir(parents=True,exist_ok=True)
version='kesas-rc39';sha=lambda data:hashlib.sha256(data).hexdigest()
load=lambda p:json.loads(p.read_text(encoding='utf-8-sig'))
js=lambda x:(json.dumps(x,separators=(',',':'),ensure_ascii=False)+'\n').encode()
release=load(baseline/'RELEASE.json');assert release['version']=='kesas-rc38'
for record in release['files']:assert sha((baseline/record['path']).read_bytes())==record['sha256']
with zipfile.ZipFile(baseline/'ClientTemplate.zip') as z:files={n:z.read(n) for n in z.namelist()}
original={n:sha(v) for n,v in files.items()};prefix='GameData/LunaMultiplayer/Plugins/'
join=load(b/'rc39-session-policy/patched/LmpClient.dll.join-policy-audit.json')
v=load(b/'rc39-release-client/version-audit.json');repair=load(b/'runtime-repair-candidate/code/METHOD-AUDIT.json')
assert join['inputSha256']==original[prefix+'LmpClient.dll'] and join['allOtherMethodsUnchanged'] and join['policyAfterInstaller']
assert v['inputSha256'].lower()==join['outputSha256'] and v['version']==version and len(v['changed'])==1
assert repair['allOtherMethodsUnchanged'] and not repair['externalHookDependency']
for key,name in [('engine','KesasInstall.dll'),('continuity','AetherfrontContinuityClient.dll')]:
    assert repair[key+'InputSha256']==original[prefix+name]
    data=(b/'runtime-repair-candidate/code'/name).read_bytes();assert sha(data)==repair[key+'OutputSha256'];files[prefix+name]=data
files[prefix+'LmpClient.dll']=(b/'rc39-release-client/LmpClient.dll').read_bytes();assert sha(files[prefix+'LmpClient.dll'])==v['outputSha256'].lower()
files[prefix+'AetherfrontSessionPolicyClient.dll']=(b/'rc39-session-policy/AetherfrontSessionPolicyClient.dll').read_bytes()
assert sha(files[prefix+'AetherfrontSessionPolicyClient.dll'])==join['policySha256']
payload='GameData/LunaMultiplayer/PluginData/Runtime/Assembly-CSharp.apfbin'
files[payload]=(game/'KSP_x64_Data/Managed/Assembly-CSharp.dll').read_bytes()
assert len(files[payload])==10790912 and sha(files[payload])=='d9e42483f25ee80a9c11d6c1c0a0d29b4ec78c1e08d76c971b71580c9cce51e4'
catalog_path='GameData/LunaMultiplayer/Launcher/install-catalog.json'
catalog=json.loads(files[catalog_path]);pack=load(baseline/'APFPack-template.json')
assert catalog['version']==pack['packVersion']=='kesas-rc38';catalog['version']=version
for profile in (catalog['full'],catalog['potato'],pack):
    assert profile['packVersion']==profile['requiredClientVersion']=='kesas-rc38'
    profile['packVersion']=profile['requiredClientVersion']=version
    # Reuse every immutable managed/mod/media archive without changing its hash.
    for rule in profile['clientOnly']:
        if any(root.casefold()=='gamedata/lunamultiplayer' for root in rule['pathRoots']):
            for n,data in files.items():
                if n.startswith(prefix) and n.endswith('.dll'):
                    rule['dlls']=[entry for entry in rule['dlls'] if entry['path'].casefold()!=n.casefold()]
                    rule['dlls'].append({'path':n,'sha256':sha(data)})
files[catalog_path]=js(catalog)
settings_path='GameData/LunaMultiplayer/Data/APFClient.json'
settings=json.loads(files[settings_path]);assert settings['clientVersion']=='kesas-rc38';settings['clientVersion']=version;files[settings_path]=js(settings)
manifest='KESAS/PATCH-MANIFEST.json';files[manifest]=js({'version':version,'files':[{'path':n,'bytes':len(data),'sha256':sha(data)} for n,data in sorted(files.items()) if n!=manifest]})
assert all(not any(x in n.lower() for x in ('campaignclient','arenarecovery','arenaownedprobe','livescenefixture','testinstrumentation')) for n in files)
assert not any(n.endswith('.key') or '/saves/' in n.lower() for n in files)
assert files[prefix+'AetherfrontCore.dll'] and sha(files[prefix+'AetherfrontCore.dll'])==original[prefix+'AetherfrontCore.dll']

def archive(path,entries):
    with zipfile.ZipFile(path,'w',zipfile.ZIP_DEFLATED,compresslevel=9) as z:
        for n,data in sorted(entries.items()):
            assert not n.startswith('/') and '\\' not in n and ':' not in n and '..' not in Path(n).parts
            info=zipfile.ZipInfo(n,(2026,11,1,0,0,0));info.compress_type=zipfile.ZIP_DEFLATED;z.writestr(info,data)
    with zipfile.ZipFile(path) as z:assert z.testzip() is None and set(z.namelist())==set(entries) and all(z.read(n)==data for n,data in entries.items())

archive(native/'ClientTemplate.zip',files)
entries={record['path']:(baseline/record['path']).read_bytes() for record in release['files'] if record['path'] not in ('ClientTemplate.zip','APFPack-template.json')}
entries['ClientTemplate.zip']=(native/'ClientTemplate.zip').read_bytes();entries['APFPack-template.json']=js(pack)
references=json.loads(entries['MediaReferences.json']);assert references['applicationVersion']=='kesas-rc38'
references['applicationVersion']=version;entries['MediaReferences.json']=js(references)
for n,data in entries.items():
    target=native/n;target.parent.mkdir(parents=True,exist_ok=True);target.write_bytes(data)
entries['RELEASE.json']=js({'version':version,'files':[{'path':n,'sha256':sha(data)} for n,data in sorted(entries.items())]})
(native/'RELEASE.json').write_bytes(entries['RELEASE.json'])
target=out/f'Aetherfront-Release-{version}.zip';archive(target,entries)
report={'version':version,'status':'Built narrow shipping candidate; signed host publication pending','archive':target.name,'bytes':target.stat().st_size,'sha256':sha(target.read_bytes()),
        'changedClientFiles':[n for n,data in files.items() if original.get(n)!=sha(data)],'arenaCodeIncluded':False,'arenaActivationIncluded':False,'serverRuntimeChanged':False,
        'modPackageArchivesChanged':False,'runtimeRepairBootstrap':'Older loaded helpers install the updater feature first; a subsequent Connect can repair an incompatible game assembly.',
        'pushed':False,'signed':False,'privateStateIncluded':False}
(out/'BUILD-RESULT.json').write_bytes(js(report));(out/'SHA256.txt').write_text(report['sha256']+'  '+target.name+'\n')
print(json.dumps(report,indent=2))
