# Aetherfront Fork Design

## Product

Aetherfront is a persistent faction-driven space-politics sandbox built on a fork of Luna Multiplayer.

Shared launchpad creates contact. Faction-scoped progression, vessel ownership, diplomacy, contracts, trade, objectives, and consequences create player-authored stories.

Primary design goals:

- Give players autonomy over identity, strategy, and relationships.
- Make progress legible and earned through visible competence.
- Make faction membership useful without making solo play impossible.
- Generate stories from authoritative events and player decisions.
- Keep large multiplayer sessions responsive through ownership filtering and interest management.
- Keep progression server-authoritative and auditable.
- Make client setup automatic through versioned APF manifests.

Research basis:

- Self-determination research links autonomy, competence, and relatedness with enjoyment and future play: https://selfdeterminationtheory.org/SDT/documents/2010_PrzybylskiRigbyRyan_ROGP.pdf
- Multiplayer social settings can improve enjoyment and need satisfaction: https://www.sciencedirect.com/science/article/pii/S1875952119300370
- Emergent narrative depends on player action, game mechanics, and community interpretation: https://www.sciencedirect.com/science/article/abs/pii/S1875952122000568
- Guild identity and relationships form durable social structures in persistent multiplayer games: https://journals.sagepub.com/doi/10.1177/1555412006292616

## Non-goals for initial release

- No war mode.
- No unrestricted PvP.
- No mandatory faction membership.
- No client-trusted science, funds, ownership, or leaderboard values.
- No arbitrary downloaded installers.
- No forced public faction chat or forced Discord participation.
- No AI-authored canonical event history.

## Player model

An account is the persistent identity. The server binds the authenticated LMP device identity to a persisted Aetherfront account record at handshake; command payload account IDs are never trusted. A player has one primary faction on a server, or may remain factionless.

Faction membership controls access, not account ownership. A player leaving a faction does not erase faction assets or progression.

Recommended initial rules:

- One primary faction per player.
- Factionless play remains valid.
- Faction switching has a configurable cooldown.
- Faction leadership is transferable.
- Member removal is audited.
- Faction profile visibility is public by default, with privacy controls.

## Faction identity and governance

Faction profile fields:

- Stable faction ID.
- Display name.
- Abbreviation.
- Description.
- Primary color.
- Flag asset ID and content hash.
- Public/private profile mode.
- Recruitment policy.
- Creation time.
- Founder account ID.
- Current member count.
- Public activity summary.

Roles:

- Founder.
- Leader.
- Officer.
- Engineer.
- Scientist.
- Pilot.
- Diplomat.
- Historian.

The role list is extensible, but permissions are server-defined. UI labels cannot grant authority.

Flag uploads are validated by the server: PNG only, bounded dimensions, bounded bytes, decoded-image validation, content hash, rate limit, moderation state, and content-addressed storage. Clients cache flags under Aetherfront-owned paths; flags are not silently written into the managed mod pack.

## Faction progression

Each faction has independent:

- Science.
- Technology unlocks.
- Funds.
- Reputation.
- Contracts.
- Achievements.
- Objective progress.
- Research projects.

Research projects are server-authored definitions. Factions spend their own science into a
project through a revision-checked command; completion can unlock technology and award funds
or reputation. Projects support time windows, repeating periods, and bounded catch-up windows.
The client only renders the definition and submits a contribution request; it cannot set
progress or rewards.

KSP scenario state is treated as a client presentation surface. Aetherfront state is authoritative. Every mutation is a command with:

- Actor account ID.
- Faction ID.
- Command ID for idempotency.
- Expected revision.
- Validated input.
- Resulting revision.
- Event IDs.

Stale commands are rejected with a fresh snapshot. Replayed commands return their original result rather than applying twice.

Progression design:

- Factions use the same base technology graph but unlock independently.
- Joint research can produce shared licenses or discounts.
- Starter technology prevents new factions from being irrelevant.
- Catch-up rewards reduce veteran dominance without deleting veteran achievement.
- Progression rewards options and identity, not only raw power.

## Trade and agreements

Trade uses a two-sided proposal interface. Each side selects assets, confirms, and commits through escrow.

Tradable assets may include:

- Funds.
- Science.
- Craft files.
- Craft licenses.
- Research licenses.
- Mission rewards.
- Time-limited services.

Trade invariants:

- Both sides see the same canonical proposal.
- Any edit invalidates prior confirmation.
- Server checks ownership and balances at commit time.
- Commit is atomic.
- Expired proposals release escrow.
- Every transfer has an audit record.
- Science and funds have configurable caps and cooldowns.

Future central-bank role:

- Game master controls base rate, loan caps, collateral, and default policy.
- Loans use explicit principal, APR, repayment schedule, collateral, and grace period.
- Interest is calculated server-side from server time.
- No hidden client timers control debt.

Scenario progression bridge note: APF mode treats stock KSP scenario state as a presentation
surface only. The server rejects every legacy ShareProgress mutation path in APF mode; faction
science, funds, reputation, technology, contracts, achievements, and related progression are
read from the Aetherfront ledger. The client mirrors the approved faction values into local KSP
UI without sending them back. Trusted server events, objectives, moderator commands, and future
signed gameplay evidence are the permitted award sources. Vanilla LMP keeps its original shared
scenario behavior.

## Live event mode

Moderators can start a bounded event mode from the Aetherfront menu. It is recorded in the durable
ledger and can be scoped by body, LMP subspace, and UTC start/end window. While active, all live
craft in the event location become visible and fully synchronized to participants; this does not
grant mutation, launch, or ownership rights. Normal faction craft permissions remain authoritative
outside the event location. This supports shared races, rescue events, and public set pieces without
streaming every vessel to every player.

## Craft ownership and control

Every craft and vessel has an owner faction, creator account, revision, and access policy.

Permissions are independent:

- View metadata.
- View full craft.
- Clone.
- Use.
- Modify.
- Launch.
- Recover.
- Transfer.
- Grant control.

Default behavior:

- Space Center and mission-control lists show own-faction craft.
- APF Craft Library requests are restricted to the requesting player's own library folder;
  cross-faction sharing is handled by server-authorized registered craft rights, not by
  exposing every player's saved-file directory.
- Licensed or allied craft appear only when the permission permits.
- Other faction craft are represented by bounded lightweight metadata when public or relevant.
- Remote full craft data is loaded on demand.
- Control uses a server-side lease and owner/faction permission check.
- Coupling, docking, recovery, revert, deletion, and rename are permission-checked.

Shared launchpad behavior:

- Physical launchpad remains shared.
- Other factions' nearby vehicles can be rendered as noninteractive summaries.
- Launch reservation prevents accidental spawn collisions without making launch ownership private.
- Special events can temporarily widen the interest set and synchronize all relevant vessels;
  event-specific widening remains a scale extension and must retain the same authorization checks.

## Diplomacy

Initial diplomacy:

- Alliance.
- Non-aggression agreement.
- Joint research.
- Rescue agreement.
- Craft license.
- Trade agreement.
- Public declaration.

Each agreement stores parties, scope, permissions, start/end time, revocation rules, confirmation state, and audit events.

War mode is separate and must not leak destructive permissions into the peaceful base mode.

## Objectives and leaderboards

Objective definitions are server-authored and versioned. Objective progress derives from validated events, never client-submitted counters.

Leaderboards are separated by category:

- Total science gain.
- Bodies reached.
- First achievements.
- Successful launches.
- Contracts completed.
- Funds earned.
- Rescue operations.
- Reliability.
- Research breadth.
- Cooperation.
- Story impact.

Use all-time and seasonal views. Normalize or annotate faction-size effects. Avoid one global score that makes all other playstyles irrelevant.

## Event ledger and Chronicle

The event ledger is append-only. Events contain factual state transitions, not prose.

Example event types:

- FactionCreated.
- FactionJoined.
- FactionLeft.
- FlagPublished.
- ResearchUnlocked.
- ContractAccepted.
- ContractCompleted.
- LaunchCompleted.
- OrbitAchieved.
- LandingAchieved.
- BodyReached.
- RescueCompleted.
- VesselLost.
- VesselRecovered.
- TradeCommitted.
- AgreementSigned.
- AgreementRevoked.
- ObjectiveCompleted.
- StoryPublished.

Each event stores timestamp, actors, factions, vessel/craft IDs, location, trigger, outcome, resource deltas, and related event IDs.

Chronicle features:

- Automatic timeline.
- Player-written title and summary.
- Tags.
- Screenshots.
- Participant attribution.
- Verified facts separated from player interpretation.
- Public, faction-only, or private visibility.
- Report and moderation state.
- Markdown/JSON export.
- Discord and web publication.
- Shareable story cards.

Canonical facts are never edited in place. Corrections create a linked correction event. Player prose can be edited with revision history.

## Story pressure without scripted rails

Systems should create conditions for stories, not dictate outcomes.

Useful pressure:

- Shared milestones.
- Limited launch windows.
- Scarce contracts.
- Research forks.
- Joint projects.
- Rescue requests.
- Public promises.
- Temporary world events.
- Conflicting rewards.
- Engineering tradeoffs.

Failure must be consequential but recoverable. A failed launch should create a problem, not erase a faction's future.

## Retention

First-session flow:

1. Create or join faction.
2. Choose name, color, description, and flag.
3. Receive starter objective.
4. Select or build first craft.
5. Launch during shared launchpad activity.
6. See first progression result.
7. Publish or save first Chronicle entry.

Long-term loops:

- Weekly faction objectives.
- Rotating research projects.
- Faction activity feed.
- Return digest.
- Catch-up rewards.
- Seasonal records.
- Public faction browser.
- Server-defined public store for lore titles and research licenses.
- Discord/web story cards.

Avoid daily chores, absence punishment, and permanent faction lock-in.

## Performance architecture

Current LMP behavior can send hundreds of full vessel records repeatedly. Aetherfront must use:

- Faction-filtered snapshots.
- Snapshot ID and acknowledgement.
- Delta synchronization.
- Resumable batches.
- Per-client byte/message budgets.
- Priority lane for control and heartbeat traffic.
- Coalesced position/resource updates.
- Stale queue replacement.
- On-demand craft detail.
- Lightweight remote-vessel summaries.
- Compression for large config-node payloads.
- Client scene-load timeout policy in the fork.

Target behavior: scene transitions never require a full universe download. A client receives only changed state plus relevant faction and interest-bubble data.

Required telemetry:

- Per-client queue depth.
- Reliable bytes queued/sent.
- Snapshot age.
- Snapshot retry count.
- Sync completion duration.
- RTT.
- Largest packet.
- Dropped/coalesced updates.
- Scene-sync failures.
- Disconnect reason and last successful heartbeat.

## Backend layout

Initial deployment may use SQLite in WAL mode for APF state, with a later PostgreSQL option for multi-server administration.

Keep KSP universe files separate from APF domain state:

```text
Server-Host/LMPServer/
  Universe/
  Config/
  Logs/
  Aetherfront/
    state.db
    events/
    snapshots/
    flags/
    stories/
    migrations/
```

Database responsibilities:

- Faction and membership state.
- Progression state.
- Agreements and escrow.
- Craft ownership and licenses.
- Objectives.
- Event index.
- Story drafts/publications.
- Client/mod compatibility state.

File responsibilities:

- Large media blobs.
- Append-only event export.
- Human-readable recovery snapshots.

All writes use transactions. Snapshot creation is atomic. Recovery replays events after the last valid snapshot.

## Server/client split

Server owns:

- Identity-to-session binding.
- Faction membership.
- Permissions.
- Progression.
- Trade.
- Craft ownership.
- Objectives.
- Event ledger.
- Mod manifest.
- Interest filtering.

Client owns:

- Menus and presentation.
- Local flag selection before upload.
- Local craft editing.
- Local GameData sync workflow.
- Rendering remote summaries.
- Story composition UI.

Client never owns authoritative progression or permissions.

## Protocol baseline

Add versioned APF messages:

- `FactionSnapshotRequest` / `FactionSnapshotReply`.
- `FactionCommandRequest` / `FactionCommandReply`.
- `FactionEventBatch`.
- `ProgressionSnapshot`.
- `TradeProposal` / `TradeCommit` / `TradeResult`.
- `VesselAccessRequest` / `VesselAccessReply`.
- `CraftLicenseRequest` / `CraftLicenseReply`.
- `ObjectiveSnapshot`.
- `EventModeStart` / `EventModeClear` / `EventModeSnapshot`.
- `ChronicleEventBatch`.
- `StoryDraftRequest` / `StoryPublishRequest`.
- Existing APF pack messages.

Every mutating message includes command ID and expected revision. Every reply includes accepted/rejected result, reason code, current revision, and relevant events.

The server enforces expected-revision compare-and-swap before applying a new command. A
replayed command ID is allowed to return its original receipt even after the state has
advanced; a new command with a stale revision receives a projected snapshot and is not
applied.

## Mod synchronization

Retain existing APF managed-pack model, add:

- Immutable pack versions.
- Resumable downloads.
- Content-addressed cache.
- Server/client protocol compatibility.
- Join gate before normal LMP handshake.
- Signed manifests in a later security phase.
- CDN support so the game server is not download bottleneck.
- Client-only whitelist.
- Safe helper transaction and relaunch.

## Admin and game-master tools

Game master can:

- Create/edit/archive objectives.
- Set rewards and eligibility.
- Publish world events.
- Create faction starter packages.
- Approve flags/stories.
- Inspect event history.
- Rebuild leaderboards.
- Pause trade or progression.
- Issue compensating transactions.
- Change launch/event interest rules.
- Manage central-bank policy.

Admin actions require reason, actor, timestamp, before state, after state, and audit event.

## Delivery phases

### Phase 0: foundation

- Isolated Aetherfront source/install.
- Domain models.
- Versioned APF protocol.
- Persistent APF state store.
- Event ledger.
- Command/revision/idempotency rules.
- Network and sync telemetry.

### Phase 1: vertical slice

- Create/join faction.
- Identity and flag.
- Separate science/progression.
- Own-craft filtering.
- Shared launchpad summaries.
- One objective.
- One event-to-Chronicle flow.

### Phase 2: permissions and economy

- Roles.
- Craft rights.
- Licenses.
- Trade escrow.
- Science/funds transfer.
- Joint research.

### Phase 3: diplomacy and stories

- Alliances.
- Agreements.
- Public faction browser.
- Chronicle editor.
- Moderation.
- Discord/web export.

### Phase 4: scale

- Snapshot/delta vessel sync.
- Interest bubbles.
- Priority network lanes.
- Compression.
- Load tests with hundreds of vessels and dozens of clients.

The current vertical slice enforces faction-filtered full vessel sync and mutation, plus a bounded
public-safe metadata interest feed. Full snapshot acknowledgements, delta packets, resumable vessel
batches, and byte-budget telemetry remain the next scale phase; the metadata feed is not a substitute
for those controls.

### Phase 5: live operations

- Objective editor.
- Seasons.
- Catch-up systems.
- Analytics.
- Admin dashboard.
- Migration/backup tooling.
- CDN and pack publishing.

## Acceptance tests

- Two factions have independent science, funds, reputation, contracts, and technology.
- Client cannot alter faction state without valid server command.
- Duplicate command ID cannot duplicate reward.
- Stale revision is rejected safely.
- Faction creation, membership, flag, and leadership changes survive restart.
- Trade commits atomically or not at all.
- Craft cannot be controlled without owner/license/alliance permission.
- Space Center does not load unrelated faction craft by default.
- Shared launchpad shows lightweight remote summaries.
- Large universe sync is filtered, resumable, and acknowledged.
- Main APF server and bot remain untouched.
- Pack mismatch blocks join.
- Event ledger reconstructs objective and trade history.
- Player can turn verified events into a moderated Chronicle publication.
- Discord/web export contains no unapproved private data.
- Recovery from snapshot plus event replay restores state.
- New player reaches first objective in first session.
