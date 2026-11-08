"""Build an RC50 reliability candidate from the exact reviewed RC49 native archive."""

import argparse
import hashlib
import io
import json
from pathlib import Path, PurePosixPath
import sys
import zipfile

if sys.flags.optimize:
    raise RuntimeError("Release checks require unoptimized Python")

RC49 = "kesas-rc49"
RC50 = "kesas-rc50"
NATIVE_BASE = "cdf1be63fe7ff7c693e4a7b0ab9a7053302064485591a5d1ca796f6a1cb885b5"
SERVER_BASE = "e0be19482434aa5e41340033619edece16379bc3c0b1554f2131a177f307a130"
INSTALLER_BASE = "a962689c04b033d36ba945e8fd7e88a08596e93ea35308f9cf9e4d2b08998d69"
CLIENT_BASE = "2615e1a83fc2d46d626f7188442aa6035ff3428e8ee55bf5ab40a1f8a33ba204"


def digest(data):
    return hashlib.sha256(data).hexdigest()


def read_json(data):
    return json.loads(data.decode("utf-8-sig"))


def write_json(value):
    return (json.dumps(value, ensure_ascii=False, separators=(",", ":")) + "\n").encode()


def unpack(data):
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        rows = archive.infolist()
        assert len(rows) == len({row.filename.casefold() for row in rows})
        assert sum(row.file_size for row in rows) < 512 * 1024 * 1024
        assert all(row.filename and not row.is_dir() and not row.filename.startswith("/")
                   and "\\" not in row.filename and ":" not in row.filename
                   and ".." not in PurePosixPath(row.filename).parts for row in rows)
        assert archive.testzip() is None
        return {row.filename: archive.read(row) for row in rows}


def pack(files):
    output = io.BytesIO()
    with zipfile.ZipFile(output, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for path, data in sorted(files.items()):
            entry = zipfile.ZipInfo(path, (2026, 11, 7, 0, 0, 0))
            entry.compress_type = zipfile.ZIP_DEFLATED
            archive.writestr(entry, data)
    result = output.getvalue()
    assert unpack(result) == files
    return result


def replace(files, path, original, candidate, expected):
    assert digest(files[path]) == original
    data = candidate.read_bytes()
    assert digest(data) == expected and expected != original
    files[path] = data


def bump_manifest(manifest):
    assert manifest["packVersion"] == manifest["requiredClientVersion"] == RC49
    manifest["packVersion"] = manifest["requiredClientVersion"] = RC50


def main():
    parser = argparse.ArgumentParser()
    for name in ("baseline", "server", "installer", "client", "output"):
        parser.add_argument("--" + name, required=True, type=Path)
    for name in ("server-sha", "installer-sha", "client-sha"):
        parser.add_argument("--" + name, required=True)
    args = parser.parse_args()
    baseline = args.baseline.read_bytes()
    assert digest(baseline) == NATIVE_BASE
    native = unpack(baseline)
    manifest = read_json(native.pop("RELEASE.json"))
    assert manifest["version"] == RC49
    assert set(native) == {row["path"] for row in manifest["files"]}
    assert all(digest(native[row["path"]]) == row["sha256"] for row in manifest["files"])
    client = unpack(native["ClientTemplate.zip"])
    prefix = "GameData/LunaMultiplayer/"
    replace(native, "ServerPayload/Server.dll", SERVER_BASE, args.server, args.server_sha)
    replace(client, prefix + "Plugins/KesasInstall.dll", INSTALLER_BASE, args.installer, args.installer_sha)
    replace(client, prefix + "Plugins/LmpClient.dll", CLIENT_BASE, args.client, args.client_sha)

    catalog_path = prefix + "Launcher/install-catalog.json"
    settings_path = prefix + "Data/APFClient.json"
    patch_path = "KESAS/PATCH-MANIFEST.json"
    catalog, settings = read_json(client[catalog_path]), read_json(client[settings_path])
    assert catalog["version"] == settings["clientVersion"] == RC49
    catalog["version"] = settings["clientVersion"] = RC50
    bump_manifest(catalog["full"])
    bump_manifest(catalog["potato"])
    client[catalog_path] = write_json(catalog)
    client[settings_path] = write_json(settings)
    assert read_json(client[patch_path])["version"] == RC49
    client[patch_path] = write_json({"version": RC50, "files": [
        {"path": path, "bytes": len(data), "sha256": digest(data)}
        for path, data in sorted(client.items()) if path != patch_path
    ]})

    pack_manifest = read_json(native["APFPack-template.json"])
    bump_manifest(pack_manifest)
    native["APFPack-template.json"] = write_json(pack_manifest)
    native["ClientTemplate.zip"] = pack(client)
    media = read_json(native["MediaReferences.json"])
    assert media["applicationVersion"] == RC49
    media["applicationVersion"] = RC50
    native["MediaReferences.json"] = write_json(media)
    native["RELEASE.json"] = write_json({"version": RC50, "files": [
        {"path": path, "sha256": digest(data)} for path, data in sorted(native.items())
    ]})
    output = pack(native)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with args.output.open("xb") as archive:
        archive.write(output)
    print(json.dumps({"version": RC50, "sha256": digest(output), "bytes": len(output),
                      "client": args.client_sha, "installer": args.installer_sha, "server": args.server_sha}))


if __name__ == "__main__":
    main()
