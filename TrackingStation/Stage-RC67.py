"""Stage RC67 recovery or its matched RC68 sender follow-up."""
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
parser.add_argument("--sender-client", type=Path)
parser.add_argument("--sender-client-sha256")
args = parser.parse_args()
if bool(args.sender_client) != bool(args.sender_client_sha256):
    parser.error("RC68 requires both its tested sender client and exact SHA-256")
if args.sender_client:
    old, version = "kesas-rc67", "kesas-rc68"
    base_sha = "d2c47329106622d7a828527632c1de035407d24af35e47ef48003047b221c598"
else:
    old, version = "kesas-rc66", "kesas-rc67"
    base_sha = "66f239748f60445f8f9af2c0dddff27133730eba25dba9d603a2e77c7f95deff"
base = args.baseline.read_bytes()
if digest(base) != base_sha:
    raise ValueError("Expected exact " + old + " archive")
release = unpack(base)
if "AccountReset.json" in release:
    raise ValueError("Account changes require independent approval")
server = args.server.read_bytes()
if digest(server) != args.server_sha256 or server == release["ServerPayload/Server.dll"]:
    raise ValueError("Expected the exact tested recovery server")
release["ServerPayload/Server.dll"] = server
client = unpack(release["ClientTemplate.zip"])
prefix = "GameData/LunaMultiplayer/"
if args.sender_client:
    sender = args.sender_client.read_bytes()
    plugin = prefix + "Plugins/AetherfrontStabilityClient.dll"
    if digest(sender) != args.sender_client_sha256 or sender == client[plugin]:
        raise ValueError("Expected the exact tested sender client")
    client[plugin] = sender
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
