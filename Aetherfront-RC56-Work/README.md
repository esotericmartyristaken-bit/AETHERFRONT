# RC56 synchronization candidate

Baseline: reviewed RC55 source commit `7f466ddb93aee768e854bde53632be5ca88ca384`, release archive SHA-256 `74b3be5f3bb507d420d595ad73b5ecfff302fbb8f46905e5d4d7fc97f1498b03`. Never rebuild the historical server source as a substitute for the shipping assemblies.

Scope: identified warp acknowledgements, unscaled synchronization lifecycle and safe exit, bounded/deduplicated transfers, revision-aware snapshots, phase timings, and backup staging outside the fleet transaction lock. Preserve authentication, permissions, science accounting, durable commits, deletion tombstones, and reliable mutation order. Do not deploy or restart production as part of this implementation task.

This directory is a development candidate, not an approved release. Validation results and limitations must be recorded after testing; compilation alone is not a Unity flight test.
