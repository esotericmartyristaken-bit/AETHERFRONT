"""Build the RC52 pacing hotfix from the exact deployed RC51 native bundle."""

import argparse
from pathlib import Path
import runpy

BASELINE = "kesas-rc51"
RELEASE = "kesas-rc52"
NATIVE_SHA256 = "08363c573500d67e834bb3343dbfd8da4bb9c5ff6dca6727fcc61a6d5baf73aa"
OLD_TRANSPORT_SHA256 = "b8fa4194d427843007cfc40c8e64fa9e5c084910a4bfa9e55d16315faeba40b3"
TRANSPORT_SHA256 = "52538e1cb45176d4e27242196a133a3075534fadcb621b59be0d926f80cba13a"
PERSONAL_SHA256 = "e86176701d4e036764965e1fe8da60a54a9e10c76577ce84bf632c7d618736dc"
FIXED_PERSONAL_SHA256 = "ccd52d49da5c1e6237340fbb19872935d409bda74ed448b7e31b4c0576c55afc"
CLIENT_SHA256 = "74421c66ca6cdd5c6fb27f2711b5486d438e4eaf9c08b5b7279f32c319c50a3d"
STAMPED_CLIENT_SHA256 = "e653cfb3b10eb84ad8f211832b11e7a33849b33ee94128107a810cfe5f9fb9bb"
PREFIX = "GameData/LunaMultiplayer/"
PLUGIN = PREFIX + "Plugins/"

helpers = runpy.run_path(str(Path(__file__).resolve().parents[2] / "Aetherfront-RC50-Work" / "Build-Release.py"))
digest, pack, unpack = (helpers[name] for name in ("digest", "pack", "unpack"))
read, write = (helpers[name] for name in ("read_json", "write_json"))


def stamp(data, expected_hash, old, new):
    assert digest(data) == expected_hash
    before, after = (value.encode("utf-16le") for value in (old, new))
    # Both stamps update a metadata constant and the string used in the
    # method body. Keep the original PE layout and all unrelated bytes.
    assert len(before) == len(after) and data.count(before) == 2 and after not in data
    return data.replace(before, after)


def hotfix_client(client):
    transport = client[PLUGIN + "Lidgren.Network.dll"]
    assert digest(transport) == TRANSPORT_SHA256
    personal_path = PLUGIN + "AetherfrontPersonalClient.dll"
    client[personal_path] = stamp(client[personal_path], PERSONAL_SHA256, OLD_TRANSPORT_SHA256, digest(transport))
    assert digest(client[personal_path]) == FIXED_PERSONAL_SHA256
    client_path = PLUGIN + "LmpClient.dll"
    client[client_path] = stamp(client[client_path], CLIENT_SHA256, BASELINE, RELEASE)
    assert digest(client[client_path]) == STAMPED_CLIENT_SHA256


def bump_pack(manifest):
    assert manifest["packVersion"] == manifest["requiredClientVersion"] == BASELINE
    manifest["packVersion"] = manifest["requiredClientVersion"] = RELEASE


def build(baseline):
    assert digest(baseline) == NATIVE_SHA256
    native = unpack(baseline)
    manifest = read(native.pop("RELEASE.json"))
    assert manifest["version"] == BASELINE
    assert set(native) == {item["path"] for item in manifest["files"]}
    assert all(digest(native[item["path"]]) == item["sha256"] for item in manifest["files"])
    client = unpack(native["ClientTemplate.zip"])
    hotfix_client(client)

    catalog_path = PREFIX + "Launcher/install-catalog.json"
    settings_path = PREFIX + "Data/APFClient.json"
    patch_path = "KESAS/PATCH-MANIFEST.json"
    catalog, settings = read(client[catalog_path]), read(client[settings_path])
    assert catalog["version"] == settings["clientVersion"] == read(client[patch_path])["version"] == BASELINE
    catalog["version"] = settings["clientVersion"] = RELEASE
    bump_pack(catalog["full"])
    bump_pack(catalog["potato"])
    client[catalog_path], client[settings_path] = write(catalog), write(settings)
    client[patch_path] = write({"version": RELEASE, "files": [
        {"path": path, "bytes": len(data), "sha256": digest(data)}
        for path, data in sorted(client.items()) if path != patch_path
    ]})

    pack_manifest = read(native["APFPack-template.json"])
    bump_pack(pack_manifest)
    native["APFPack-template.json"] = write(pack_manifest)
    media = read(native["MediaReferences.json"])
    assert media["applicationVersion"] == BASELINE
    media["applicationVersion"] = RELEASE
    native["MediaReferences.json"] = write(media)
    native["ClientTemplate.zip"] = pack(client)
    native["RELEASE.json"] = write({"version": RELEASE, "files": [
        {"path": path, "sha256": digest(data)} for path, data in sorted(native.items())
    ]})
    return pack(native)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("baseline", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    result = build(args.baseline.read_bytes())
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with args.output.open("xb") as output:
        output.write(result)
    print(f"{RELEASE}  {digest(result)}  {len(result)} bytes")


if __name__ == "__main__":
    main()
