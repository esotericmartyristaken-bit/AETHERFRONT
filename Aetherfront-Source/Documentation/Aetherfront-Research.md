# Aetherfront research projects

`Server\Config\AetherfrontResearch.json` is authoritative. Each definition is visible through
the APF snapshot, while progress is stored per faction and period.

```json
[
  {
    "Id": "frontier-propulsion",
    "SeasonId": "frontier-research",
    "Title": "Frontier propulsion",
    "Description": "Pool faction science to unlock an experimental technology.",
    "TargetScience": 250,
    "RewardTechnologyId": "frontier-propulsion",
    "FundsRewardMicro": 2500000,
    "ReputationReward": 5,
    "PublicProgress": true,
    "RepeatPeriodDays": 7
  }
]
```

A faction Scientist, Officer, Leader, or Founder submits `ContributeResearch`. The server
checks membership, active period, available faction science, target remaining, command ID, and
expected revision. It deducts science atomically; completion applies configured rewards and
records Chronicle events. Clients cannot submit progress, change targets, or choose rewards.

Research progress is separate from ordinary objective progress. `PublicProgress` controls what
other factions see; the owning faction always sees its own progress.
