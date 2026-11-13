"""Combine reviewed RC60, validated automatic updater, and tested docking/warp protection without replacing server code or media."""
import argparse
import json
from pathlib import Path
import runpy

helpers = runpy.run_path(str(Path(__file__).resolve().parents[1] / "Aetherfront-RC50-Work/Build-Release.py"))
digest, pack, unpack, read, write = (helpers[n] for n in ("digest", "pack", "unpack", "read_json", "write_json"))
BASE_SHA = "ae323f3aea9056284fc5163af70cbf5c7aa22af3dd5b43648d3a6d8d840216cc"
PREFIX = "GameData/LunaMultiplayer/"

def build(baseline, updater, stability, versioned):
    assert digest(baseline) == BASE_SHA, "RC60 baseline identity differs"
    proof = read((updater / "Candidate.json").read_bytes())
    assert proof["validated"] and not proof["deployed"] and proof["baseline"] == "kesas-rc60"
    for row in proof["sourceFiles"]:
        assert digest(Path(row["path"]).read_bytes()) == row["sha256"], "Validated updater source changed"
    native = unpack(baseline); original = dict(native)
    release = read(native.pop("RELEASE.json"))
    assert release["version"] == "kesas-rc60" and "AccountReset.json" not in native
    assert set(native) == {row["path"] for row in release["files"]}
    assert all(digest(native[row["path"]]) == row["sha256"] for row in release["files"])
    client = unpack(native["ClientTemplate.zip"]); old_client = dict(client)
    assert len(proof["files"]) == 4
    for row in proof["files"]:
        data = (updater / "ClientOverlay" / row["path"]).read_bytes()
        assert digest(data) == row["sha256"] and len(data) == row["bytes"]
        name = row["path"].replace("\\", "/")
        assert name in client or name == PREFIX + "Launcher/AetherfrontInstallUI.dll", "Unexpected updater overlay path"
        client[name] = data
    version_audit = read(Path(str(versioned) + ".version-audit.json").read_bytes())
    assert version_audit["version"] == "kesas-rc61" and version_audit["inputSha256"] == digest(client[PREFIX + "Plugins/LmpClient.dll"])
    assert version_audit["outputSha256"] == digest(versioned.read_bytes()) and version_audit["unchangedMethods"] == 6245
    client[PREFIX + "Plugins/LmpClient.dll"] = versioned.read_bytes()
    client[PREFIX + "Plugins/AetherfrontStabilityClient.dll"] = stability.read_bytes()
    settings_path = PREFIX + "Data/APFClient.json"
    catalog_path = PREFIX + "Launcher/install-catalog.json"
    patch_path = "KESAS/PATCH-MANIFEST.json"
    settings, catalog = read(client[settings_path]), read(client[catalog_path])
    assert settings["clientVersion"] == catalog["version"] == "kesas-rc60"
    settings["clientVersion"] = catalog["version"] = "kesas-rc61"
    def bump(value):
        assert value["packVersion"] == value["requiredClientVersion"] == "kesas-rc60"
        value["packVersion"] = value["requiredClientVersion"] = "kesas-rc61"
    bump(catalog["full"]); bump(catalog["potato"])
    client[settings_path], client[catalog_path] = write(settings), write(catalog)
    client[patch_path] = write({"version": "kesas-rc61", "files": [{"path": p, "bytes": len(b), "sha256": digest(b)} for p, b in sorted(client.items()) if p != patch_path]})
    manifest = read(native["APFPack-template.json"]); bump(manifest)
    native["APFPack-template.json"] = write(manifest)
    media = read(native["MediaReferences.json"]); assert media["applicationVersion"] == "kesas-rc60"
    media["applicationVersion"] = "kesas-rc61"; native["MediaReferences.json"] = write(media)
    native["ClientTemplate.zip"] = pack(client)
    native["RELEASE.json"] = write({"version": "kesas-rc61", "files": [{"path": p, "sha256": digest(b)} for p, b in sorted(native.items())]})
    client_changes = {p for p in client if client[p] != old_client.get(p)}
    expected = {PREFIX + p for p in ("Plugins/LmpClient.dll", "Plugins/ApfCore.dll", "Plugins/KesasInstall.dll", "Plugins/AetherfrontStabilityClient.dll", "Launcher/AetherfrontInstallUI.dll", "Data/APFClient.json", "Launcher/install-catalog.json")} | {patch_path}
    assert client_changes == expected, "Unreviewed client mutation"
    native_changes = {p for p in native if native[p] != original[p]}
    assert native_changes == {"ClientTemplate.zip", "APFPack-template.json", "MediaReferences.json", "RELEASE.json"}
    assert set(client) == set(old_client) | {PREFIX + "Launcher/AetherfrontInstallUI.dll"} and set(native) == set(original)
    assert all(native[p] == original[p] for p in native if p.startswith("ServerPayload/"))
    return pack(native), {"version": "kesas-rc61", "baselineSha256": BASE_SHA, "clientChanged": sorted(client_changes), "nativeChanged": sorted(native_changes), "serverPayloadUnchanged": True, "mediaPreserved": True, "accountReset": False, "updaterSourceValidated": True}

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("baseline", "updater", "stability", "versioned", "output"):
        parser.add_argument(name, type=Path)
    args = parser.parse_args()
    data, audit = build(args.baseline.read_bytes(), args.updater, args.stability, args.versioned)
    with args.output.open("xb") as target: target.write(data)
    audit.update(bytes=len(data), sha256=digest(data))
    with args.output.with_suffix(".assembly.json").open("x", encoding="utf-8") as target: json.dump(audit, target, indent=2)
    print(json.dumps(audit, indent=2))
