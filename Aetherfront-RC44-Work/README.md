# RC44 personal-clock checkpoint persistence

The server disconnects a player when their mission clock cannot be saved. This
repair retains that fail-closed guard, all rollback and checksum validation,
account/universe isolation, record format, sequence handling, and flushed atomic
writes. It does not reset time or modify any account, science balance, vessel,
permission, or unrelated persistence writer.

The graft replaces one File.Replace call in the shipping ClockStore.Write with
five private helper methods. Only sharing/lock violations and Windows error 1175
are eligible for retries, bounded by two seconds and sixteen attempts. Each
retry first proves that the flushed source and original destination bytes are
unchanged and both paths are regular files. An uncertain completed replacement
is accepted only if the source is gone and destination exactly matches the
intended record. Other outcomes, corruption, and permanent failures remain
errors. There is no in-place overwrite, copy fallback, or deletion of the saved
clock. The existing final cleanup retains ownership of its temporary file.

The patcher pins RC43 helper and client hashes. It audits all existing methods,
fields, types, resources, identities, and dependencies; restoring the one call
must recover the original atomic writer. Client changes only the release getter
and constant to RC44, preserving RC43 science receipts, retry policy and sender
pacing. Both install profiles require the coordinated version. Media is reused.

Build ClockRepair and Patch with .NET SDK 8, keeping output outside Git. Patch
accepts `clock BASELINE REPAIR OUTPUT` or `client BASELINE OUTPUT`; client mode
requires AETHERFRONT_KSP_MANAGED to locate the licensed KSP managed references.
Build-Release.py accepts immutable RC43, audited clock/client DLLs and a fresh
output directory. Publish only through the existing guarded native workflow,
with fifteen-minute warnings when players are online. Do not copy live DLLs,
reset deployment journals, or overwrite clock records manually.
