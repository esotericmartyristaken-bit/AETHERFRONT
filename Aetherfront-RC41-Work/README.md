# RC41 identity messages and public science

The server now distinguishes invalid account details, an occupied player name,
a changed account key, and a reserved legacy name. Only rejection reason
assignments change. Authentication conditions, challenge verification, account
ownership, operator enrollment, and binding persistence remain unchanged.
Existing clients display the server's full reason. Discord also maps the old
generic rejection to a complete player-name instruction.

`Patch/Patch.csproj` requires .NET 8 and an explicit existing Mono.Cecil assembly
through `-p:CecilPath=...`. Run `server INPUT OUTPUT DEPENDENCIES` or
`client INPUT OUTPUT KSP_MANAGED_REFERENCE_DIRECTORY`. Inputs are pinned to the
reviewed RC40 hashes. The server audit removes the four reason assignments and
compares the recovered method body with the original. All other method bodies,
references, resources and fields are checked unchanged. The client changes only
its release getter and build constant to `kesas-rc41`.

`IdentityTests` exercises original and patched shipping server assemblies with
generated identities in isolated temporary directories. Twenty cases verify
identical acceptance decisions, protected occupied names and account keys,
operator enrollment, legacy reservations, and non-mutating rejection paths.
No production connection or player account is created by these tests.

The public status card reads the authoritative personal progression ledger.
New-player science is starting science plus half of all earned science. Spent
wallet balances and previously credited shared science are not summed into it.
Missing, malformed or inconsistent data displays `Unknown`, not a legacy zero.

`Build-Release.py` derives RC41 from the authenticated RC40 native bundle. The
signed client inventory, Regular and Potato catalog versions are updated
together. Mod archives and the RC40 reliable transport fix remain unchanged.
The builder accepts a separately supplied `--account-reset` manifest; actual
player identities and deployment state are not included in this source tree.

An optional, strictly validated `AccountReset.json` permits one operator-
authorized name/key release while retaining the old account's owned data. Native publication applies it only after
the normal occupied-server warning/guard, clean stop, full Universe/Aetherfront
backup, and durable activation journal. The expected old identity must still
match, without aliases, conflicting enrollment or legacy name reservations.
Only the old identity binding is removed; the old world account receives a
deterministic retired display name. Account IDs, personal progression, crafts
and historical records remain unchanged. No content moves to the new claimant.
Unexpected state fails closed. Changes are covered by the existing activation
rollback. No replacement key or enrollment is created.

Publish the reviewed control bundle before publishing RC41 through the normal
authenticated application channel. Never modify the running world, bypass the
15-minute occupied-server guard, overwrite immutable releases, or touch the
vanilla server. A successful repeated publication does not run the reset again.
