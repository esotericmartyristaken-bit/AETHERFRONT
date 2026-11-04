"""Build the science receipt/pacing release from immutable RC42; no runtime state."""
from pathlib import Path
import argparse
import hashlib
import io
import json
import zipfile

p=argparse.ArgumentParser()
for name in ('baseline','code','output'):p.add_argument('--'+name,type=Path,required=True)
a=p.parse_args()
sha=lambda b:hashlib.sha256(b).hexdigest()
load=lambda b:json.loads(b.decode('utf-8-sig'))
encode=lambda o:(json.dumps(o,ensure_ascii=False,separators=(',',':'))+'\n').encode()
version='kesas-rc43'
def read_zip(data):
    with zipfile.ZipFile(io.BytesIO(data)) as z:
        names=z.namelist()
        assert len(names)==len(set(names)) and sum(i.file_size for i in z.infolist())<512*1024*1024
        assert all(n and not n.startswith('/') and '\\' not in n and ':' not in n and '..' not in Path(n).parts for n in names)
        assert z.testzip() is None
        return {n:z.read(n) for n in names}
def archive(entries):
    stream=io.BytesIO()
    with zipfile.ZipFile(stream,'w',zipfile.ZIP_DEFLATED,compresslevel=9) as z:
        for n,b in sorted(entries.items()):
            i=zipfile.ZipInfo(n,(2026,11,4,0,0,0));i.compress_type=zipfile.ZIP_DEFLATED;z.writestr(i,b)
    b=stream.getvalue();assert read_zip(b)==entries;return b
baseline=a.baseline.read_bytes()
assert sha(baseline)=='7bceff2674ce133750ad7ea5400bce3ef90e7e704399c207c2031c1408b7958f'
native=read_zip(baseline);release=load(native.pop('RELEASE.json'))
assert release['version']=='kesas-rc42' and set(native)=={f['path'] for f in release['files']}
assert all(sha(native[f['path']])==f['sha256'] for f in release['files'])
old_native=native.copy();client=read_zip(native['ClientTemplate.zip']);old_client=client.copy();prefix='GameData/LunaMultiplayer/'
assert 'AccountReset.json' not in native
audits={}
for name,kind,target,owner in [('AetherfrontPersonalClient.dll','client',prefix+'Plugins/AetherfrontPersonalClient.dll',client),('AetherfrontPersonalServer.dll','server','ServerPayload/AetherfrontPersonalServer.dll',native)]:
    audit=load((a.code/(name+'.audit.json')).read_bytes());assert audit['kind']==kind and audit['version']==version
    assert audit['identityPreserved'] and audit['allUnrelatedMethodsUnchanged'] and audit['existingReferencesAndResourcesPreserved']
    if target in owner:
        assert sha(owner[target])==audit['inputSha256']
    else:
        # RC42 is a delta archive: this unchanged-since-RC33 helper was already
        # installed, but absent from its payload. Audit pins that exact baseline.
        assert kind=='server' and audit['inputSha256']=='033461f09f4db405cb6d2e9415936f2e1ef7d25aa24114d2412624aceb0ad06b'
    owner[target]=(a.code/name).read_bytes();assert sha(owner[target])==audit['outputSha256'];audits[name]=audit
audit=load((a.code/'LmpClient.dll.audit.json').read_bytes())
assert audit['version']==version and audit['allOtherMethodsUnchanged'] and audit['originalSenderAndReceiverRecovered'] and audit['recycleFinallyPreserved'] and audit['existingSessionAndEndpointChecksPreserved']
assert audit['helperSha256']==sha((a.code/'AetherfrontPersonalClient.dll').read_bytes())
assert sha(client[prefix+'Plugins/LmpClient.dll'])==audit['inputSha256']
client[prefix+'Plugins/LmpClient.dll']=(a.code/'LmpClient.dll').read_bytes()
assert sha(client[prefix+'Plugins/LmpClient.dll'])==audit['outputSha256'];audits['LmpClient.dll']=audit
catalog_path=prefix+'Launcher/install-catalog.json';settings_path=prefix+'Data/APFClient.json';manifest_path='KESAS/PATCH-MANIFEST.json'
catalog=load(client[catalog_path]);pack=load(native['APFPack-template.json']);assert catalog['version']=='kesas-rc42';catalog['version']=version
for profile in (catalog['full'],catalog['potato'],pack):
    assert profile['packVersion']==profile['requiredClientVersion']=='kesas-rc42';profile['packVersion']=profile['requiredClientVersion']=version
client[catalog_path]=encode(catalog)
settings=load(client[settings_path]);assert settings['clientVersion']=='kesas-rc42';settings['clientVersion']=version;client[settings_path]=encode(settings)
client[manifest_path]=encode({'version':version,'files':[{'path':n,'bytes':len(b),'sha256':sha(b)} for n,b in sorted(client.items()) if n!=manifest_path]})
expected_client={prefix+'Plugins/LmpClient.dll',prefix+'Plugins/AetherfrontPersonalClient.dll',catalog_path,settings_path,manifest_path}
assert set(client)==set(old_client) and {n for n in client if client[n]!=old_client[n]}==expected_client
native['ClientTemplate.zip']=archive(client);native['APFPack-template.json']=encode(pack)
media=load(native['MediaReferences.json']);assert media['applicationVersion']=='kesas-rc42';media['applicationVersion']=version;native['MediaReferences.json']=encode(media)
expected_native={'ClientTemplate.zip','APFPack-template.json','MediaReferences.json','ServerPayload/AetherfrontPersonalServer.dll'}
assert set(native)==set(old_native)|{'ServerPayload/AetherfrontPersonalServer.dll'} and {n for n in native if native[n]!=old_native.get(n)}==expected_native
assert sha(native['ServerPayload/Server.dll'])=='d37641645592b80b4e22065d813e65c68ba35b5cbd258b4a0b118d38109df59d'
assert sha(native['ServerPayload/Lidgren.Core.dll'])=='99288eb5872e7a9608dc830295eb353b15db9b5fa26b33ceab94846201e329fa'
assert sha(client[prefix+'Plugins/Lidgren.Network.dll'])=='b8fa4194d427843007cfc40c8e64fa9e5c084910a4bfa9e55d16315faeba40b3'
assert not any('/universe/' in n.lower() or '/secrets/' in n.lower() or n.lower().endswith(('.key','.pfx','.p12')) for n in native)
native['RELEASE.json']=encode({'version':version,'files':[{'path':n,'sha256':sha(b)} for n,b in sorted(native.items())]})
a.output.mkdir(parents=True,exist_ok=True);target=a.output/f'Aetherfront-Release-{version}.zip';assert not target.exists(),'Use new immutable output'
target.write_bytes(archive(native))
result={'version':version,'baselineSha256':sha(baseline),'sha256':sha(target.read_bytes()),'bytes':target.stat().st_size,'changedClientFiles':sorted(expected_client),'changedNativeFiles':sorted(expected_native),'unchangedMediaArchives':True,'unchangedCoreAndTransport':True,'unchangedServerAndIdentityCode':True,'productionStateOrKeysIncluded':False,'oneTimeAccountOperationIncluded':False,'audits':audits}
(a.output/'BUILD-RESULT.json').write_bytes(encode(result));print(json.dumps(result,indent=2))
