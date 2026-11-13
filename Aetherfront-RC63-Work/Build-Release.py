"""Combine corrected installer and startup guard with the exact published RC62 release."""
import argparse
import json
from pathlib import Path
import runpy

helpers=runpy.run_path(str(Path(__file__).resolve().parents[1]/"Aetherfront-RC50-Work/Build-Release.py"))
digest,pack,unpack,read,write=(helpers[n] for n in ("digest","pack","unpack","read_json","write_json"))
parser=argparse.ArgumentParser(description=__doc__)
for name in ("baseline","engine","stability","client","output"):parser.add_argument(name,type=Path)
a=parser.parse_args()
baseline=a.baseline.read_bytes()
assert digest(baseline)=="d4d0639a73749935e1c200d2d54f7a21762300921d53a35cd4faa9517591e2b5"
native=unpack(baseline);original=dict(native);release=read(native["RELEASE.json"])
assert release["version"]=="kesas-rc62" and "AccountReset.json" not in native
assert all(digest(native[r["path"]])==r["sha256"] for r in release["files"])
client=unpack(native["ClientTemplate.zip"]);old_client=dict(client);prefix="GameData/LunaMultiplayer/"
for name,path in (("KesasInstall.dll",a.engine),("AetherfrontStabilityClient.dll",a.stability),("LmpClient.dll",a.client)):
 client[prefix+"Plugins/"+name]=path.read_bytes()
audit=read(Path(str(a.client)+".version-audit.json").read_bytes())
assert audit["version"]=="kesas-rc63" and audit["inputSha256"]==digest(old_client[prefix+"Plugins/LmpClient.dll"])
assert audit["outputSha256"]==digest(client[prefix+"Plugins/LmpClient.dll"]) and audit["unchangedMethods"]==6245
settings=read(client[prefix+"Data/APFClient.json"]);catalog=read(client[prefix+"Launcher/install-catalog.json"])
assert settings["clientVersion"]==catalog["version"]=="kesas-rc62"
settings["clientVersion"]=catalog["version"]="kesas-rc63"
def bump(value):
 assert value["packVersion"]==value["requiredClientVersion"]=="kesas-rc62"
 value["packVersion"]=value["requiredClientVersion"]="kesas-rc63"
for profile in ("full","potato"):bump(catalog[profile])
client[prefix+"Data/APFClient.json"]=write(settings);client[prefix+"Launcher/install-catalog.json"]=write(catalog)
client["KESAS/PATCH-MANIFEST.json"]=write({"version":"kesas-rc63","files":[{"path":p,"bytes":len(b),"sha256":digest(b)} for p,b in sorted(client.items()) if p!="KESAS/PATCH-MANIFEST.json"]})
manifest=read(native["APFPack-template.json"]);bump(manifest);native["APFPack-template.json"]=write(manifest)
media=read(native["MediaReferences.json"]);assert media["applicationVersion"]=="kesas-rc62"
media["applicationVersion"]="kesas-rc63";native["MediaReferences.json"]=write(media)
native["ClientTemplate.zip"]=pack(client)
native["RELEASE.json"]=write({"version":"kesas-rc63","files":[{"path":p,"sha256":digest(b)} for p,b in sorted(native.items()) if p!="RELEASE.json"]})
changed={p for p in client if client[p]!=old_client[p]}
assert changed=={prefix+p for p in ("Plugins/KesasInstall.dll","Plugins/AetherfrontStabilityClient.dll","Plugins/LmpClient.dll","Data/APFClient.json","Launcher/install-catalog.json")}|{"KESAS/PATCH-MANIFEST.json"}
assert set(client)==set(old_client) and set(native)==set(original)
assert all(native[p]==original[p] for p in native if p.startswith("ServerPayload/"))
data=pack(native)
with a.output.open("xb") as f:f.write(data)
proof={"version":"kesas-rc63","sha256":digest(data),"bytes":len(data),"clientChanged":sorted(changed),"serverPayloadUnchanged":True,"mediaPreserved":True,"accountReset":False,"retainsRc62ThrustProtectionAndIncrementalUpdater":True}
with a.output.with_suffix(".assembly.json").open("x") as f:json.dump(proof,f,indent=2)
print(json.dumps(proof,indent=2))
