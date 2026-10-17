# Aetherfront public web feed

When the existing LMP website is enabled, its read-only JSON response now includes an
`Aetherfront` object. The object is refreshed with the normal website interval and contains:

- public faction profiles and rosters;
- public faction-scoped progression and research progress;
- the latest bounded public event ledger entries;
- approved public Chronicle stories;
- server-owned objectives, research projects, and technology definitions.

The feed is projected with the same visibility rules as an unauthenticated in-game snapshot:
moderator IDs, command receipts, private factions, private progress, private stories, and bank
reserves are not exposed. It is intended as the stable read-only input for a future Discord/web
bridge; no Discord token or bot code belongs in the fork server.
