"""Derive the clock-persistence repair from immutable RC43 without live state."""
from pathlib import Path
import argparse
import hashlib
import io
import json
import zipfile

p=argparse.ArgumentParser()
for name in ('baseline','clock','client','output'):p.add_argument('--'+name,type=Path,required=True)
a=p.parse_args();version='kesas-rc44'
sha=lambda b:hashlib.sha256(b).hexdigest()
load=lambda b:json.loads(b.decode('utf-8-sig'))
encode=lambda o:(json.dumps(o,ensure_ascii=False,separators=(',',':'))+'\n').encode()
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
    data=stream.getvalue();assert read_zip(data)==entries;return data
baseline=a.baseline.read_bytes()
assert sha(baseline)=='710ac479c893bb874b706df54838286a139871b9638a2d05e439f649f96ba383'
native=read_zip(baseline);release=load(native.pop('RELEASE.json'))
assert release['version']=='kesas-rc43' and set(native)=={f['path'] for f in release['files']}
assert all(sha(native[f['path']])==f['sha256'] for f in release['files'])
assert 'AccountReset.json' not in native
old_native=native.copy();client=read_zip(native['ClientTemplate.zip']);old_client=client.copy();prefix='GameData/LunaMultiplayer/'
audits={}
for path,mode,target,owner in ((a.clock,'clock','ServerPayload/AetherfrontContinuityServer.dll',native),(a.client,'client',prefix+'Plugins/LmpClient.dll',client)):
    audit=load(Path(str(path)+'.audit.json').read_bytes())
    assert audit['mode']==mode and audit['version']==version and audit['identityPreserved'] and audit['allOtherMethodsUnchanged']
    assert audit['originalAtomicWriterRecovered'] and audit['clockValidationAndSessionGuardsUnchanged'] if mode=='clock' else audit['existingScienceAndPacingHooksUnchanged']
    assert sha(owner[target])==audit['inputSha256']
    owner[target]=path.read_bytes();assert sha(owner[target])==audit['outputSha256'];audits[mode]=audit
catalog_path=prefix+'Launcher/install-catalog.json';settings_path=prefix+'Data/APFClient.json';manifest_path='KESAS/PATCH-MANIFEST.json'
catalog=load(client[catalog_path]);pack=load(native['APFPack-template.json']);assert catalog['version']=='kesas-rc43';catalog['version']=version
for profile in (catalog['full'],catalog['potato'],pack):
    assert profile['packVersion']==profile['requiredClientVersion']=='kesas-rc43';profile['packVersion']=profile['requiredClientVersion']=version
client[catalog_path]=encode(catalog)
settings=load(client[settings_path]);assert settings['clientVersion']=='kesas-rc43';settings['clientVersion']=version;client[settings_path]=encode(settings)
client[manifest_path]=encode({'version':version,'files':[{'path':n,'bytes':len(b),'sha256':sha(b)} for n,b in sorted(client.items()) if n!=manifest_path]})
expected_client={prefix+'Plugins/LmpClient.dll',catalog_path,settings_path,manifest_path}
assert set(client)==set(old_client) and {n for n in client if client[n]!=old_client[n]}==expected_client
native['ClientTemplate.zip']=archive(client);native['APFPack-template.json']=encode(pack)
media=load(native['MediaReferences.json']);assert media['applicationVersion']=='kesas-rc43';media['applicationVersion']=version;native['MediaReferences.json']=encode(media)
expected_native={'ClientTemplate.zip','APFPack-template.json','MediaReferences.json','ServerPayload/AetherfrontContinuityServer.dll'}
assert set(native)==set(old_native) and {n for n in native if native[n]!=old_native[n]}==expected_native
for target,digest in {
 'ServerPayload/Server.dll':'d37641645592b80b4e22065d813e65c68ba35b5cbd258b4a0b118d38109df59d',
 'ServerPayload/Lidgren.Core.dll':'99288eb5872e7a9608dc830295eb353b15db9b5fa26b33ceab94846201e329fa',
 'ServerPayload/AetherfrontPersonalServer.dll':'da7be39a359b58fc32b5f7143546c0a8c30e5f620f42d9e587ee4d28826cfae7'
}.items():assert sha(native[target])==digest
assert sha(client[prefix+'Plugins/AetherfrontPersonalClient.dll'])=='02157055d47a3fe3c68b05fe8637f666db5af1815b8d5d23e14f2b78eb36da61'
assert sha(client[prefix+'Plugins/Lidgren.Network.dll'])=='b8fa4194d427843007cfc40c8e64fa9e5c084910a4bfa9e55d16315faeba40b3'
assert not any('/universe/' in n.lower() or '/secrets/' in n.lower() or n.lower().endswith(('.key','.pfx','.p12')) for n in native)
native['RELEASE.json']=encode({'version':version,'files':[{'path':n,'sha256':sha(b)} for n,b in sorted(native.items())]})
a.output.mkdir(parents=True,exist_ok=True);target=a.output/f'Aetherfront-Release-{version}.zip';assert not target.exists(),'Use new immutable output'
target.write_bytes(archive(native))
result={'version':version,'baselineSha256':sha(baseline),'sha256':sha(target.read_bytes()),'bytes':target.stat().st_size,'changedClientFiles':sorted(expected_client),'changedNativeFiles':sorted(expected_native),'unchangedMediaArchives':True,'unchangedScienceAndTransport':True,'clockSafetyAndRecordFormatPreserved':True,'productionStateOrKeysIncluded':False,'oneTimeAccountOperationIncluded':False,'audits':audits}
(a.output/'BUILD-RESULT.json').write_bytes(encode(result));print(json.dumps({k:v for k,v in result.items() if k!='audits'},indent=2))
