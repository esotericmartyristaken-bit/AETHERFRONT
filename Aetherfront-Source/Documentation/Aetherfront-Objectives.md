# Aetherfront objectives

`Server\Config\AetherfrontObjectives.json` is the baseline server authority for faction objectives. Clients may display and request snapshots, but cannot submit objective progress directly.

Moderators can create, update, and archive objectives live from the Aetherfront faction window. These edits use server-side permission checks, append Chronicle events, and persist as dynamic (`ConfigManaged=false`) objectives. A dynamic objective with the same ID overrides the baseline config after restart; archived dynamic objectives remain archived until explicitly recreated or changed by an administrator. This allows story beats and rotating events to be published without editing files or restarting the server.

Each entry supports:

- `EventType`: server-generated event, such as `FactionCreated`, `CraftRegistered`, `VesselLaunched`, `BodyReached`, `MissionCompleted`, or `AchievementCompleted`.
- `Target` and reward fields: applied once when the faction reaches the target.
- `StartsAtUtc` / `EndsAtUtc`: optional UTC window.
- `RepeatPeriodDays`: `0` for permanent objectives; positive values create independent rotating periods anchored at `StartsAtUtc` or Unix epoch.
- `CatchUpWindowDays`: optional grace window after `EndsAtUtc`; requires `EndsAtUtc`, max 14 days. Progress is stored in a separate `:catch-up` period.

Progress is keyed by `(objective id, faction id, period key)`. This prevents a completed weekly objective from blocking the next period and prevents one faction from consuming another faction's reward. Legacy progress without a period key is treated as permanent.

Example weekly objective:

```json
{
  "Id": "weekly-expedition",
  "SeasonId": "rotating-expeditions",
  "Title": "Weekly expedition",
  "Description": "Reach a new body during this rotating faction objective.",
  "EventType": "BodyReached",
  "Target": 1,
  "ScienceReward": 50,
  "FundsRewardMicro": 500000,
  "ReputationReward": 2,
  "PublicProgress": true,
  "RepeatPeriodDays": 7
}
```

Example timed objective with late-participant grace:

```json
{
  "Id": "station-window",
  "SeasonId": "station-season-1",
  "Title": "Establish the station",
  "Description": "Complete the station objective before the season closes.",
  "EventType": "MissionCompleted",
  "Target": 1,
  "ScienceReward": 100,
  "FundsRewardMicro": 1000000,
  "ReputationReward": 5,
  "PublicProgress": true,
  "StartsAtUtc": "2026-08-14T00:00:00Z",
  "EndsAtUtc": "2026-08-21T00:00:00Z",
  "CatchUpWindowDays": 2
}
```

Only the server's validated LMP events can advance these entries. `RecordObjectiveEvent` is intentionally rejected at the wire command layer.
