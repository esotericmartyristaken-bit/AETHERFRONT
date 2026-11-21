"""Stage a local-only RC65 tracker candidate; never approve or publish it."""
import argparse
import json
from pathlib import Path
import re
import runpy

helpers = runpy.run_path(str(Path(__file__).resolve().parents[1] / "Aetherfront-RC50-Work/Build-Release.py"))
digest, pack, unpack, read, write = (helpers[n] for n in ("digest", "pack", "unpack", "read_json", "write_json"))
parser = argparse.ArgumentParser(description=__doc__)
for name in ("baseline", "server", "client", "output"):
    parser.add_argument("--" + name, type=Path, required=True)
parser.add_argument("--account-reset", type=Path)
parser.add_argument("--versioned-client", type=Path,
                    help="Optional RC66 client produced by the pinned version tool")
args = parser.parse_args()


def pinned(path, expected):
    data = path.read_bytes()
    if digest(data) != expected:
        raise ValueError(f"Unexpected bytes: {path.name}")
    return data


native = unpack(pinned(args.baseline, "15b0ac083289bbeadd4dd6dadc7ecb7227f955b276d36f53092be813255fa7d1"))
original = dict(native)
release = read(native["RELEASE.json"])
if release["version"] != "kesas-rc65" or "AccountReset.json" in native:
    raise ValueError("Expected exact RC65 base without account reset")
client = unpack(native["ClientTemplate.zip"])
tracking = "GameData/LunaMultiplayer/Plugins/AetherfrontTrackingClient.dll"
patch = "KESAS/PATCH-MANIFEST.json"
server = "ServerPayload/Server.dll"
if tracking in client or digest(native[server]) != "b4b18584eb00cf62c7b4993542b00aa7c5a6f906fd51deace51aebd908130113":
    raise ValueError("Unexpected tracker or server base")
client[tracking] = pinned(args.client, "45a197404870ae6562290ec21926f9abbbd4e586e2e13c0f9adab6c4b3c4abc4")
native[server] = pinned(args.server, "62444e9ec01aca81822fc0d873e32736058607d24145cc1a1c2c18c69a75f1f5")
version = release["version"]
allowed = {"ClientTemplate.zip", server}
if args.versioned_client:
    version = "kesas-rc66"
    versioned = args.versioned_client.read_bytes()
    audit = read(args.versioned_client.with_suffix(".dll.version-audit.json").read_bytes())
    if (audit["version"] != version or audit["outputSha256"] != digest(versioned)
            or audit["inputSha256"] != "eee970ac7e1cea2e3b70b66fe331a04f26edfaeefc878af165b9064cdef8520e"
            or audit["unchangedMethods"] < 1000):
        raise ValueError("RC66 client version audit mismatch")
    prefix = "GameData/LunaMultiplayer/"
    client[prefix + "Plugins/LmpClient.dll"] = versioned
    settings_path = prefix + "Data/APFClient.json"
    settings = read(client[settings_path])
    if settings["clientVersion"] != "kesas-rc65":
        raise ValueError("RC65 settings mismatch")
    settings["clientVersion"] = version
    client[settings_path] = write(settings)

    def bump_pack(pack_manifest):
        if pack_manifest["packVersion"] != "kesas-rc65" or pack_manifest["requiredClientVersion"] != "kesas-rc65":
            raise ValueError("RC65 pack mismatch")
        pack_manifest["packVersion"] = pack_manifest["requiredClientVersion"] = version

    catalog_path = prefix + "Launcher/install-catalog.json"
    catalog = read(client[catalog_path])
    if catalog["version"] != "kesas-rc65":
        raise ValueError("RC65 catalog mismatch")
    catalog["version"] = version
    for profile in ("full", "potato"):
        bump_pack(catalog[profile])
    client[catalog_path] = write(catalog)
    pack_manifest = read(native["APFPack-template.json"])
    bump_pack(pack_manifest)
    native["APFPack-template.json"] = write(pack_manifest)
    media = read(native["MediaReferences.json"])
    if media["applicationVersion"] != "kesas-rc65":
        raise ValueError("RC65 media descriptor mismatch")
    media["applicationVersion"] = version
    native["MediaReferences.json"] = write(media)
    allowed.update(("APFPack-template.json", "MediaReferences.json"))
manifest = read(client[patch])
manifest["version"] = version
manifest["files"] = [{"path": p, "bytes": len(data), "sha256": digest(data)}
                     for p, data in sorted(client.items()) if p != patch]
client[patch] = write(manifest)
native["ClientTemplate.zip"] = pack(client)
if args.account_reset:
    reset = read(args.account_reset.read_bytes())
    required = {"schemaVersion", "applicationVersion", "playerName", "expectedAccountId", "preserveAccountData"}
    if (set(reset) != required or type(reset["schemaVersion"]) is not int or reset["schemaVersion"] != 1
            or reset["applicationVersion"] != release["version"] or reset["playerName"].lower() != "goober"
            or reset["preserveAccountData"] is not True
            or not re.fullmatch(r"key-[a-f0-9]{64}", reset["expectedAccountId"])):
        raise ValueError("Expected a verified, exact goober reset intent")
    reset["applicationVersion"] = version
    native["AccountReset.json"] = write(reset)
    allowed.add("AccountReset.json")
if {p for p, data in native.items() if original.get(p) != data} != allowed:
    raise ValueError("Unexpected payload diff")
release["version"] = version
release["files"] = [{"path": p, "sha256": digest(data)} for p, data in sorted(native.items()) if p != "RELEASE.json"]
native["RELEASE.json"] = write(release)
archive = pack(native)
verified = unpack(archive)
if verified != native or unpack(verified["ClientTemplate.zip"]) != client:
    raise ValueError("Archive verification failed")
with args.output.open("xb") as out:
    out.write(archive)
print(json.dumps({"sha256": digest(archive), "bytes": len(archive), "version": release["version"],
                  "localOnly": True, "accountResetIncluded": bool(args.account_reset),
                  "requiresWorldStartupGate": True, "clientFiles": len(client)}))
