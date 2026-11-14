"""Package the DeepFreeze client fix over the exact published RC64 release."""
import argparse
import json
from pathlib import Path
import runpy

helpers = runpy.run_path(str(Path(__file__).resolve().parents[1] / "Aetherfront-RC50-Work/Build-Release.py"))
digest, pack, unpack, read, write = (helpers[n] for n in ("digest", "pack", "unpack", "read_json", "write_json"))
parser = argparse.ArgumentParser(description=__doc__)
for name in ("baseline", "client", "test-proof", "output"):
    parser.add_argument("--" + name, type=Path, required=True)
a = parser.parse_args()
if __import__("sys").flags.optimize:
    raise RuntimeError("Release checks require unoptimized Python")
proof = json.loads(a.test_proof.read_text(encoding="utf-8-sig"))
if not proof["passed"] or proof["version"] != "kesas-rc65" or not proof["actualKspMono"]:
    raise ValueError("Passing matching candidate tests are required")
for row in proof["files"]:
    if digest(Path(row["path"]).read_bytes()) != row["sha256"]:
        raise ValueError("A tested input changed")
baseline = a.baseline.read_bytes()
assert digest(baseline) == "41290fd22d18edaadef7d99c0df5df02ff276de6792cbc2b590899a87cebee1f"
native = unpack(baseline)
old_native = dict(native)
release = read(native["RELEASE.json"])
assert release["version"] == "kesas-rc64" and "AccountReset.json" not in native
assert set(native) == {r["path"] for r in release["files"]} | {"RELEASE.json"}
assert all(digest(native[r["path"]]) == r["sha256"] for r in release["files"])
client = unpack(native["ClientTemplate.zip"])
old_client = dict(client)
prefix = "GameData/LunaMultiplayer/"
assert digest(client[prefix + "Plugins/AetherfrontContinuityClient.dll"]) == "564cd10e83bc8b3bce298044ef9e54048616b7557651b678c1bd45519e893b58"
assert digest((a.client / "AetherfrontContinuityClient.dll").read_bytes()) == "399246b20fca30e8bc5f9ed6e57b01391f722f9c820e384d2c6937949b15562d"
audit = read((a.client / "LmpClient.dll.version-audit.json").read_bytes())
assert audit["version"] == "kesas-rc65" and audit["inputSha256"] == "eee970ac7e1cea2e3b70b66fe331a04f26edfaeefc878af165b9064cdef8520e"
assert audit["outputSha256"] == digest((a.client / "LmpClient.dll").read_bytes())
assert audit["unchangedMethods"] > 1000
for name in ("LmpClient.dll", "AetherfrontContinuityClient.dll"):
    source = a.client / name
    assert any(Path(r["path"]) == source and r["sha256"] == digest(source.read_bytes()) for r in proof["files"])
    client[prefix + "Plugins/" + name] = source.read_bytes()
settings = read(client[prefix + "Data/APFClient.json"])
catalog = read(client[prefix + "Launcher/install-catalog.json"])
assert settings["clientVersion"] == catalog["version"] == "kesas-rc64"
settings["clientVersion"] = catalog["version"] = "kesas-rc65"

def bump(value):
    assert value["packVersion"] == value["requiredClientVersion"] == "kesas-rc64"
    value["packVersion"] = value["requiredClientVersion"] = "kesas-rc65"

for profile in ("full", "potato"):
    bump(catalog[profile])
client[prefix + "Data/APFClient.json"] = write(settings)
client[prefix + "Launcher/install-catalog.json"] = write(catalog)
client["KESAS/PATCH-MANIFEST.json"] = write({"version": "kesas-rc65", "files": [
    {"path": p, "bytes": len(b), "sha256": digest(b)} for p, b in sorted(client.items()) if p != "KESAS/PATCH-MANIFEST.json"]})
manifest = read(native["APFPack-template.json"])
bump(manifest)
native["APFPack-template.json"] = write(manifest)
media = read(native["MediaReferences.json"])
assert media["applicationVersion"] == "kesas-rc64"
media["applicationVersion"] = "kesas-rc65"
native["MediaReferences.json"] = write(media)
native["ClientTemplate.zip"] = pack(client)
native["RELEASE.json"] = write({"version": "kesas-rc65", "files": [
    {"path": p, "sha256": digest(b)} for p, b in sorted(native.items()) if p != "RELEASE.json"]})
client_changed = {p for p in client if client[p] != old_client[p]}
server_changed = {p for p in native if p.startswith("ServerPayload/") and native[p] != old_native[p]}
assert client_changed == {prefix + p for p in ("Plugins/LmpClient.dll", "Plugins/AetherfrontContinuityClient.dll",
    "Data/APFClient.json", "Launcher/install-catalog.json")} | {"KESAS/PATCH-MANIFEST.json"}
assert not server_changed
assert set(client) == set(old_client) and set(native) == set(old_native)
assert client[prefix + "Plugins/KesasInstall.dll"] == old_client[prefix + "Plugins/KesasInstall.dll"]
data = pack(native)
with a.output.open("xb") as output:
    output.write(data)
result = {"version": "kesas-rc65", "sha256": digest(data), "bytes": len(data),
    "clientChanged": sorted(client_changed), "serverChanged": sorted(server_changed), "clientFiles": len(client),
    "mediaPreserved": True, "accountReset": False, "installerUnchanged": True,
    "clientOnlyFix": True, "testProof": str(a.test_proof), "testProofSha256": digest(a.test_proof.read_bytes())}
with a.output.with_suffix(".assembly.json").open("x") as output:
    json.dump(result, output, indent=2)
print(json.dumps(result, indent=2))
