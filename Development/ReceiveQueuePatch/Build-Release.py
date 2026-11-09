"""Assemble the RC51 reliability overlay from the verified native RC50 release."""

import argparse
from pathlib import Path
import runpy

PREVIOUS = "kesas-rc50"
RELEASE = "kesas-rc51"
NATIVE_HASH = "4fbefab2a6b38438e47bf2d1f0c64b00f5556575157203af25da7fb0eba68c90"
REPLACEMENTS = (
    ("ServerPayload/Server.dll", "127eeaa83eadfade41912d78dc5fb55f3f725cf4a551a599128a67c285473cf9"),
    ("ServerPayload/Lidgren.Core.dll", "99288eb5872e7a9608dc830295eb353b15db9b5fa26b33ceab94846201e329fa"),
    ("GameData/LunaMultiplayer/Plugins/Lidgren.Network.dll", "b8fa4194d427843007cfc40c8e64fa9e5c084910a4bfa9e55d16315faeba40b3"),
    ("GameData/LunaMultiplayer/Plugins/LmpClient.dll", "26de3f09cfb61de58049d86c6d82f3602e6cd1c28eca29f8d838431f3ddfd64b"),
)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("baseline", type=Path)
    parser.add_argument("code", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--server-sha", required=True)
    parser.add_argument("--transport-sha", required=True)
    parser.add_argument("--client-transport-sha", required=True)
    parser.add_argument("--client-sha", required=True)
    args = parser.parse_args()

    prior = Path(__file__).resolve().parents[2] / "Aetherfront-RC50-Work" / "Build-Release.py"
    helpers = runpy.run_path(str(prior))
    digest, pack, unpack = (helpers[name] for name in ("digest", "pack", "unpack"))
    read, write, replace = (helpers[name] for name in ("read_json", "write_json", "replace"))

    archive = args.baseline.read_bytes()
    assert digest(archive) == NATIVE_HASH
    native = unpack(archive)
    manifest = read(native.pop("RELEASE.json"))
    assert manifest["version"] == PREVIOUS
    assert set(native) == {item["path"] for item in manifest["files"]}
    assert all(digest(native[item["path"]]) == item["sha256"] for item in manifest["files"])
    client = unpack(native["ClientTemplate.zip"])
    versions = (args.server_sha, args.transport_sha, args.client_transport_sha, args.client_sha)
    for (path, baseline), candidate in zip(REPLACEMENTS, versions, strict=True):
        files = client if path.startswith("GameData/") else native
        replace(files, path, baseline, args.code / Path(path).name, candidate)

    def bump_pack(pack_manifest):
        assert pack_manifest["packVersion"] == pack_manifest["requiredClientVersion"] == PREVIOUS
        pack_manifest["packVersion"] = pack_manifest["requiredClientVersion"] = RELEASE

    prefix = "GameData/LunaMultiplayer/"
    catalog_path = prefix + "Launcher/install-catalog.json"
    client_path = prefix + "Data/APFClient.json"
    patch_path = "KESAS/PATCH-MANIFEST.json"
    catalog, settings = read(client[catalog_path]), read(client[client_path])
    assert catalog["version"] == settings["clientVersion"] == read(client[patch_path])["version"] == PREVIOUS
    catalog["version"] = settings["clientVersion"] = RELEASE
    bump_pack(catalog["full"])
    bump_pack(catalog["potato"])
    client[catalog_path], client[client_path] = write(catalog), write(settings)
    client[patch_path] = write({"version": RELEASE, "files": [
        {"path": path, "bytes": len(data), "sha256": digest(data)}
        for path, data in sorted(client.items()) if path != patch_path
    ]})

    pack_manifest = read(native["APFPack-template.json"])
    bump_pack(pack_manifest)
    native["APFPack-template.json"] = write(pack_manifest)
    media = read(native["MediaReferences.json"])
    assert media["applicationVersion"] == PREVIOUS
    media["applicationVersion"] = RELEASE
    native["MediaReferences.json"] = write(media)
    native["ClientTemplate.zip"] = pack(client)
    native["RELEASE.json"] = write({"version": RELEASE, "files": [
        {"path": path, "sha256": digest(data)} for path, data in sorted(native.items())
    ]})
    result = pack(native)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with args.output.open("xb") as output:
        output.write(result)
    print(f"{RELEASE}  {digest(result)}  {len(result)} bytes")


if __name__ == "__main__":
    main()
