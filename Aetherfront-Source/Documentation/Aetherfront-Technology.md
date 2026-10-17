# Aetherfront technology catalog

`Server\Config\AetherfrontTechnology.json` is authoritative when present. It defines the
technology IDs accepted by the faction progression system, science costs, prerequisites,
descriptions, and starter nodes. `UnlockTechnology` rejects IDs outside the catalog and
rejects missing prerequisites. New factions receive every catalog entry marked `Starter`.

The stock LMP technology relay is also gated by this catalog in the Aetherfront server. A
client cannot inject an unlocked stock technology into the shared scenario unless its active
faction already owns that catalog entry. A server without the catalog retains legacy LMP
behavior for compatibility during migration.

Keep the catalog aligned with the KSP tech-node IDs used by the selected tech tree. Research
projects may grant a catalog technology directly, allowing the game master to make a branch
available through faction work instead of a simple science purchase.
