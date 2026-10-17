# APF staged server updates

Updates are local, staged, hash-verified file packages. The Discord bot never downloads or executes an update from the network.

## Stage

Build/copy only the files intended for the server into a directory, then run:

```powershell
npm run stage:update -- --source C:\path\to\update --id apf-2026-08-13-1 --version 2026.08.13.1 --notes "Flag validation and crash diagnostics"
```

The source directory may contain a `payload` subdirectory. Otherwise its files become payload files. The staged manifest records SHA-256 and byte size. Paths are copied to the same relative path beneath the LMP server root.

`Universe`, `Logs`, `Backups`, and `Updates` destinations are rejected. No files are deleted. Existing files are copied into a rollback directory before replacement.

## Schedule

In Discord `#console`:

- `/deploy staged` — list valid staged packages
- `/deploy queue id:<package-id> minutes:<1-1440> changelog:<text>` — schedule a timed restart; default 15 minutes
- `/deploy status` — show state, phase, deadline, and last player-gate observation
- `/deploy cancel` — cancel a queued or failed update
- `/deploy force` — explicitly arm immediate deployment without waiting for the deadline

The same operation is available locally:

```powershell
npm run schedule:update -- --id apf-2026-08-13-1 --minutes 15 --changelog "Short public changelog"
```

Normal timed behavior:

1. Status card shows version, relative restart time, and concise changelog.
2. Bot broadcasts at 15, 10, 5, and 1 minute, then 15 seconds. Successful LMP broadcasts mirror once into `#luna-chat`.
3. If players and connections both reach zero before the deadline, deployment starts immediately. Otherwise the deadline forces the scheduled restart.
4. Bot creates a pre-deploy universe backup, gracefully stops LMP, verifies it is offline, applies hash-verified files, and starts it.
5. Bot waits for readiness. Failure triggers file rollback, one recovery start attempt, and a failed state; no infinite retry loop.

`/deploy force` remains available for emergencies. A normal timed deployment already restarts at its deadline, even while occupied.
