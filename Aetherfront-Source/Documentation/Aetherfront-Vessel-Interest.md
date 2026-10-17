# Aetherfront vessel interest feed

The fork keeps full LMP vessel packets faction/permission filtered. A client does not receive
unrelated vessel ConfigNode data merely because it is in the same Space Center.

To keep the universe socially legible, Aetherfront snapshots carry a bounded, transient
activity feed. It contains recently observed vessel metadata: vessel name, owning faction
name, body, landed/splashed state, subspace, last-seen time, and whether the requesting client
has full-view or control permission. It never contains a vessel ConfigNode, crew roster,
resource data, position vector, account identifier, or craft hash.

The feed is held in memory, expires after five minutes without a position/proto update, and is
capped at 256 entries. It is recomputed per client using the same owner, faction, alliance,
license, permission, and subspace checks used by vessel relay. A public faction may expose a
summary; full vessel synchronization still requires the relevant access grant or active
alliance and matching subspace.

This is the lightweight first stage of interest management. Snapshot/delta packet lanes and
event-specific interest bubbles remain separate scale work; they must not bypass the current
full-packet authorization gate.
