"""Stage the tracker with non-destructive DeepFreeze startup recovery."""
import argparse
import json
import runpy
from pathlib import Path

helper = runpy.run_path(str(Path(__file__).resolve().parents[1] / "Aetherfront-RC50-Work/Build-Release.py"))
digest, pack, unpack, read, write = (helper[n] for n in ("digest", "pack", "unpack", "read_json", "write_json"))
parser = argparse.ArgumentParser(description=__doc__)
for name in ("baseline", "server", "versioned-client", "output"):
    parser.add_argument("--" + name, type=Path, required=True)
parser.add_argument("--server-sha256", required=True)
args = parser.parse_args()
old, version = "kesas-rc66", "kesas-rc67"
base = args.baseline.read_bytes()
if digest(base) != "66f239748f60445f8f9af2c0dddff27133730eba25dba9d603a2e77c7f95deff":
    raise ValueError("Expected exact tracker-only RC66 archive")
release = unpack(base)
if "AccountReset.json" in release:
    raise ValueError("Account changes require independent approval")
server = args.server.read_bytes()
if digest(server) != args.server_sha256 or server == release["ServerPayload/Server.dll"]:
    raise ValueError("Expected the exact tested recovery server")
release["ServerPayload/Server.dll"] = server
client = unpack(release["ClientTemplate.zip"])
prefix = "GameData/LunaMultiplayer/"
versioned = args.versioned_client.read_bytes()
audit = read(args.versioned_client.with_suffix(".dll.version-audit.json").read_bytes())
if (audit["version"] != version or audit["outputSha256"] != digest(versioned)
        or audit["inputSha256"] != "eee970ac7e1cea2e3b70b66fe331a04f26edfaeefc878af165b9064cdef8520e"
        or audit["unchangedMethods"] != 6245):
    raise ValueError("Versioned client differs from the audited RC64 binary")
client[prefix + "Plugins/LmpClient.dll"] = versioned


def bump(data, key):
    if data[key] != old:
        raise ValueError("Unexpected previous version at " + key)
    data[key] = version


def update(container, path, fields):
    data = read(container[path])
    for field in fields:
        bump(data, field)
    container[path] = write(data)


settings = prefix + "Data/APFClient.json"
update(client, settings, ("clientVersion",))
catalog_path = prefix + "Launcher/install-catalog.json"
catalog = read(client[catalog_path])
bump(catalog, "version")
for profile in ("full", "potato"):
    for field in ("packVersion", "requiredClientVersion"):
        bump(catalog[profile], field)
client[catalog_path] = write(catalog)
update(release, "APFPack-template.json", ("packVersion", "requiredClientVersion"))
update(release, "MediaReferences.json", ("applicationVersion",))
patch = "KESAS/PATCH-MANIFEST.json"
manifest = read(client[patch])
bump(manifest, "version")
manifest["files"] = [{"path": p, "bytes": len(blob), "sha256": digest(blob)}
                     for p, blob in sorted(client.items()) if p != patch]
client[patch] = write(manifest)
release["ClientTemplate.zip"] = pack(client)
manifest = read(release["RELEASE.json"])
bump(manifest, "version")
manifest["files"] = [{"path": p, "sha256": digest(blob)}
                     for p, blob in sorted(release.items()) if p != "RELEASE.json"]
release["RELEASE.json"] = write(manifest)
archive = pack(release)
if unpack(archive) != release or unpack(release["ClientTemplate.zip"]) != client:
    raise ValueError("Archive verification failed")
with args.output.open("xb") as output:
    output.write(archive)
print(json.dumps({"version": version, "sha256": digest(archive), "bytes": len(archive), "serverSha256": digest(server)}))
