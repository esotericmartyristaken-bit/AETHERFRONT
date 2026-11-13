"""Package RC64 from the exact published RC63 baseline without rebuilding its installer."""
import argparse
import json
from pathlib import Path
import runpy

helpers = runpy.run_path(str(Path(__file__).resolve().parents[1] / "Aetherfront-RC50-Work/Build-Release.py"))
digest, pack, unpack, read, write = (helpers[n] for n in ("digest", "pack", "unpack", "read_json", "write_json"))
parser = argparse.ArgumentParser(description=__doc__)
for name in ("baseline", "validated", "output"):
    parser.add_argument("--" + name, type=Path, required=True)
a = parser.parse_args()
proof = json.loads((a.validated / "Validated.json").read_text(encoding="utf-8-sig"))
extra = json.loads((a.validated / "Supplemental.json").read_text(encoding="utf-8-sig"))
assert extra["shippingFilesUnchanged"] and extra["actualKspMono"]
assert proof["output"] == str(a.validated)
for row in proof["shippingFiles"]:
    assert digest(Path(row["path"]).read_bytes()) == row["sha256"]
baseline = a.baseline.read_bytes()
assert digest(baseline) == "a2b5be67db4be80d490758f559c2ffcaa3f0b03816d0ab34643497ca8ddd6532"
native = unpack(baseline)
old_native = dict(native)
release = read(native["RELEASE.json"])
assert release["version"] == "kesas-rc63" and "AccountReset.json" not in native
assert all(digest(native[r["path"]]) == r["sha256"] for r in release["files"])
client = unpack(native["ClientTemplate.zip"])
old_client = dict(client)
prefix = "GameData/LunaMultiplayer/"
for name in ("LmpClient.dll", "AetherfrontPersonalClient.dll", "AetherfrontStabilityClient.dll"):
    client[prefix + "Plugins/" + name] = (a.validated / "Client" / name).read_bytes()
for name in ("Server.dll", "AetherfrontContinuityServer.dll"):
    native["ServerPayload/" + name] = (a.validated / "Server" / name).read_bytes()
for name, folder in (("LmpClient.dll", "Client"), ("AetherfrontPersonalClient.dll", "Client"), ("Server.dll", "Server"), ("AetherfrontContinuityServer.dll", "Server")):
    audit = read((a.validated / folder / (name + ".audit.json")).read_bytes())
    assert audit["version"] == "kesas-rc64" and audit["allOtherMethodsUnchanged"] and audit["identityPreserved"]
    assert audit["outputSha256"] == digest((a.validated / folder / name).read_bytes())
settings = read(client[prefix + "Data/APFClient.json"])
catalog = read(client[prefix + "Launcher/install-catalog.json"])
assert settings["clientVersion"] == catalog["version"] == "kesas-rc63"
settings["clientVersion"] = catalog["version"] = "kesas-rc64"

def bump(value):
    assert value["packVersion"] == value["requiredClientVersion"] == "kesas-rc63"
    value["packVersion"] = value["requiredClientVersion"] = "kesas-rc64"

for profile in ("full", "potato"):
    bump(catalog[profile])
client[prefix + "Data/APFClient.json"] = write(settings)
client[prefix + "Launcher/install-catalog.json"] = write(catalog)
client["KESAS/PATCH-MANIFEST.json"] = write({"version": "kesas-rc64", "files": [{"path": p, "bytes": len(b), "sha256": digest(b)} for p, b in sorted(client.items()) if p != "KESAS/PATCH-MANIFEST.json"]})
manifest = read(native["APFPack-template.json"])
bump(manifest)
native["APFPack-template.json"] = write(manifest)
media = read(native["MediaReferences.json"])
assert media["applicationVersion"] == "kesas-rc63"
media["applicationVersion"] = "kesas-rc64"
native["MediaReferences.json"] = write(media)
native["ClientTemplate.zip"] = pack(client)
native["RELEASE.json"] = write({"version": "kesas-rc64", "files": [{"path": p, "sha256": digest(b)} for p, b in sorted(native.items()) if p != "RELEASE.json"]})
client_changed = {p for p in client if client[p] != old_client[p]}
server_changed = {p for p in native if p.startswith("ServerPayload/") and native[p] != old_native[p]}
assert client_changed == {prefix + p for p in ("Plugins/LmpClient.dll", "Plugins/AetherfrontPersonalClient.dll", "Plugins/AetherfrontStabilityClient.dll", "Data/APFClient.json", "Launcher/install-catalog.json")} | {"KESAS/PATCH-MANIFEST.json"}
assert server_changed == {"ServerPayload/Server.dll", "ServerPayload/AetherfrontContinuityServer.dll"}
assert set(client) == set(old_client) and set(native) == set(old_native)
assert client[prefix + "Plugins/KesasInstall.dll"] == old_client[prefix + "Plugins/KesasInstall.dll"]
data = pack(native)
with a.output.open("xb") as f:
    f.write(data)
proof = {"version": "kesas-rc64", "sha256": digest(data), "bytes": len(data), "clientChanged": sorted(client_changed), "serverChanged": sorted(server_changed), "clientFiles": len(client), "mediaPreserved": True, "accountReset": False, "rc63InstallerUnchanged": True, "validatedBuild": str(a.validated)}
with a.output.with_suffix(".assembly.json").open("x") as f:
    json.dump(proof, f, indent=2)
print(json.dumps(proof, indent=2))
