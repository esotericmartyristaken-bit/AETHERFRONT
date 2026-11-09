"""Assemble personal checkpoint RC53 from the exact deployed RC52 bundle."""
import argparse
from pathlib import Path
import runpy

helpers = runpy.run_path(str(Path(__file__).resolve().parents[1] / "Aetherfront-RC50-Work/Build-Release.py"))
digest, pack, unpack, read, write = (helpers[name] for name in ("digest", "pack", "unpack", "read_json", "write_json"))
BASELINE, RELEASE = "kesas-rc52", "kesas-rc53"
BASE_SHA = "aab842b62a6e7c20ac2485589f49b7927879f6cf90fa07b54040e688a3e53517"
PREFIX = "GameData/LunaMultiplayer/"

def build(baseline, candidate):
    assert digest(baseline) == BASE_SHA
    native = unpack(baseline)
    original = dict(native)
    manifest = read(native.pop("RELEASE.json"))
    assert manifest["version"] == BASELINE
    assert set(native) == {row["path"] for row in manifest["files"]}
    assert all(digest(native[row["path"]]) == row["sha256"] for row in manifest["files"])
    client = unpack(native["ClientTemplate.zip"])
    original_client = dict(client)
    for side, names in (("Server", ("Server.dll", "AetherfrontPersonalServer.dll", "AetherfrontContinuityServer.dll")),
                        ("Client", ("LmpClient.dll", "AetherfrontPersonalClient.dll"))):
        for name in names:
            path = ("ServerPayload/" if side == "Server" else PREFIX + "Plugins/") + name
            files = native if side == "Server" else client
            data = (candidate / side / name).read_bytes()
            audit = read((candidate / side / (name + ".audit.json")).read_bytes())
            assert audit["inputSha256"] == digest(files[path]) and audit["outputSha256"] == digest(data)
            assert audit["identityPreserved"] and audit["unrelatedMethodsPreserved"]
            files[path] = data
    def bump_pack(value):
        assert value["packVersion"] == value["requiredClientVersion"] == BASELINE
        value["packVersion"] = value["requiredClientVersion"] = RELEASE
    catalog_path, settings_path, patch_path = PREFIX + "Launcher/install-catalog.json", PREFIX + "Data/APFClient.json", "KESAS/PATCH-MANIFEST.json"
    catalog, settings = read(client[catalog_path]), read(client[settings_path])
    assert catalog["version"] == settings["clientVersion"] == read(client[patch_path])["version"] == BASELINE
    catalog["version"] = settings["clientVersion"] = RELEASE
    bump_pack(catalog["full"]); bump_pack(catalog["potato"])
    client[catalog_path], client[settings_path] = write(catalog), write(settings)
    client[patch_path] = write({"version": RELEASE, "files": [
        {"path": path, "bytes": len(data), "sha256": digest(data)} for path, data in sorted(client.items()) if path != patch_path]})
    pack_manifest = read(native["APFPack-template.json"]); bump_pack(pack_manifest)
    native["APFPack-template.json"] = write(pack_manifest)
    media = read(native["MediaReferences.json"])
    assert media["applicationVersion"] == BASELINE
    media["applicationVersion"] = RELEASE; native["MediaReferences.json"] = write(media)
    native["ClientTemplate.zip"] = pack(client)
    client_changes = {path for path in client if client[path] != original_client[path]}
    assert client_changes == {PREFIX + "Plugins/LmpClient.dll", PREFIX + "Plugins/AetherfrontPersonalClient.dll", catalog_path, settings_path, patch_path}
    allowed = {"ClientTemplate.zip", "APFPack-template.json", "MediaReferences.json", "RELEASE.json"} | {
        "ServerPayload/" + name for name in ("Server.dll", "AetherfrontPersonalServer.dll", "AetherfrontContinuityServer.dll")}
    native["RELEASE.json"] = write({"version": RELEASE, "files": [
        {"path": path, "sha256": digest(data)} for path, data in sorted(native.items())]})
    assert set(native) == set(original)
    assert {path for path in native if native[path] != original[path]} == allowed
    return pack(native)

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("baseline", type=Path); parser.add_argument("candidate", type=Path); parser.add_argument("output", type=Path)
    args = parser.parse_args()
    result = build(args.baseline.read_bytes(), args.candidate)
    with args.output.open("xb") as output: output.write(result)
    print(f"{RELEASE} {digest(result)} {len(result)} bytes; unchanged pack archives, dependencies and settings verified")
