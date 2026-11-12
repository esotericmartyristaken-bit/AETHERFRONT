"""Assemble matching RC57 client/server bytes from the pinned shipping RC55 archive."""
import argparse
import json
from pathlib import Path
import runpy

helpers = runpy.run_path(str(Path(__file__).resolve().parents[1] / "Aetherfront-RC50-Work/Build-Release.py"))
digest, pack, unpack, read, write = (helpers[name] for name in ("digest", "pack", "unpack", "read_json", "write_json"))
BASE, VERSION = "kesas-rc55", "kesas-rc57"
BASE_SHA = "74b3be5f3bb507d420d595ad73b5ecfff302fbb8f46905e5d4d7fc97f1498b03"
STABILITY_SHA = "c865243cc05ff3762a4997f027809ee0c0bb9e7488e12911c766b8bcea2ff437"
PREFIX = "GameData/LunaMultiplayer/"

def build(baseline, candidate):
    if digest(baseline) != BASE_SHA:
        raise ValueError("Shipping RC55 archive identity differs")
    native = unpack(baseline)
    original = dict(native)
    release = read(native.pop("RELEASE.json"))
    assert release["version"] == BASE
    assert set(native) == {row["path"] for row in release["files"]}
    assert all(digest(native[row["path"]]) == row["sha256"] for row in release["files"])
    assert "AccountReset.json" not in native
    client = unpack(native["ClientTemplate.zip"])
    original_client = dict(client)
    for side, names in (("Server", ("Server.dll", "AetherfrontContinuityServer.dll")), ("Client", ("LmpClient.dll",))):
        for name in names:
            files = native if side == "Server" else client
            path = ("ServerPayload/" if side == "Server" else PREFIX + "Plugins/") + name
            data = (candidate / side / name).read_bytes()
            audit = read((candidate / side / (name + ".audit.json")).read_bytes())
            assert audit["inputSha256"] == digest(files[path]) and audit["outputSha256"] == digest(data)
            assert audit["identityPreserved"] and audit["unrelatedMethodsPreserved"]
            files[path] = data
    stability = PREFIX + "Plugins/AetherfrontStabilityClient.dll"
    assert digest(client[stability]) == STABILITY_SHA
    client[stability] = (candidate / "Client/AetherfrontStabilityClient.dll").read_bytes()
    def bump(value):
        assert value["packVersion"] == value["requiredClientVersion"] == BASE
        value["packVersion"] = value["requiredClientVersion"] = VERSION
    catalog_path, settings_path, patch_path = PREFIX + "Launcher/install-catalog.json", PREFIX + "Data/APFClient.json", "KESAS/PATCH-MANIFEST.json"
    catalog, settings = read(client[catalog_path]), read(client[settings_path])
    assert catalog["version"] == settings["clientVersion"] == read(client[patch_path])["version"] == BASE
    catalog["version"] = settings["clientVersion"] = VERSION
    bump(catalog["full"]); bump(catalog["potato"])
    client[catalog_path], client[settings_path] = write(catalog), write(settings)
    client[patch_path] = write({"version": VERSION, "files": [
        {"path": path, "bytes": len(data), "sha256": digest(data)} for path, data in sorted(client.items()) if path != patch_path]})
    manifest = read(native["APFPack-template.json"]); bump(manifest)
    native["APFPack-template.json"] = write(manifest)
    media = read(native["MediaReferences.json"])
    assert media["applicationVersion"] == BASE
    media["applicationVersion"] = VERSION; native["MediaReferences.json"] = write(media)
    native["ClientTemplate.zip"] = pack(client)
    native["RELEASE.json"] = write({"version": VERSION, "files": [
        {"path": path, "sha256": digest(data)} for path, data in sorted(native.items())]})
    client_changes = {path for path in client if client[path] != original_client[path]}
    assert client_changes == {PREFIX + "Plugins/LmpClient.dll", stability, catalog_path, settings_path, patch_path}
    native_changes = {path for path in native if native[path] != original[path]}
    assert native_changes == {"ClientTemplate.zip", "APFPack-template.json", "MediaReferences.json", "RELEASE.json",
                              "ServerPayload/Server.dll", "ServerPayload/AetherfrontContinuityServer.dll"}
    assert set(native) == set(original)
    return pack(native), {"version": VERSION, "baselineSha256": BASE_SHA, "clientChanged": sorted(client_changes),
                          "nativeChanged": sorted(native_changes), "unchangedAssetsPreserved": True, "accountReset": False}

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("baseline", type=Path); parser.add_argument("candidate", type=Path); parser.add_argument("output", type=Path)
    args = parser.parse_args()
    result, audit = build(args.baseline.read_bytes(), args.candidate)
    with args.output.open("xb") as output:
        output.write(result)
    audit.update(sha256=digest(result), bytes=len(result))
    with args.output.with_suffix(".assembly.json").open("x", encoding="utf-8") as output:
        json.dump(audit, output, indent=2)
    print(json.dumps(audit))
