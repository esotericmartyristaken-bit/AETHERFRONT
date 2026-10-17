# Effect identity and empty-group IL audit

## Recommendation

The reviewed RC26 policy is the safer design: preserve engine/RCS effect-name fields, B9 `IDENTIFIER` predicates, and `EFFECTS` group names; remove each group's child nodes and values. Continue removing the Waterfall renderer and its subtype modifiers, supplementary plume models, and legacy `fx_*`/`sound_*` values. Do not remove or blank effect-name predicates to make matching succeed.

An empty named group is safe in the audited KSP implementation. Loading it adds an empty effect list when the name is absent. With zero child nodes, loading never reaches component creation. Initialization and both event overloads iterate zero effect behaviours. There is no missing-group warning, exception, particle callback, or audio callback on those paths. A missing group is also a silent event no-op, but retaining names preserves more lookup compatibility.

Reviewed policy: `Aetherfront-RC26-Work/ModFixes/Source/ZZZ_KESASPotato/DisableExhaust.cfg:24` selects `EFFECTS`; lines 28–29 remove group children/values. No effect-name clearing remains in that file. This audit does not establish ModuleManager selector application; the main-owned real-MM regression must confirm the generated nodes.

## Why the RC25 field clearing was wrong

- `Aetherfront-RC25-Work/ModFixes/Source/ZZZ_KESASPotato/DisableExhaust.cfg:29` blanks engine `runningEffectName`; line 38 blanks RCS `runningEffectName`.
- `Aetherfront-RC25-Work/P3e9e2e69/GameData/opt_reconfig/CRP/OPT_RCS_toggles.cfg:43` starts the LFO physics modifier's `IDENTIFIER`; line 46 requires `runningEffectName = running`. Its `DATA` preserves thrust, propellants, and atmosphere curves.
- The same file, line 284, deliberately changes the cloned OPTOMS identifier to `running_oms`. Removing the predicate can collapse distinct RCS/OMS targets into an ambiguous name-only match.
- B9 `ModuleMatcher.IsMatch` compares parsed identifier values with actual module fields using `Object.Equals`; `FindModule` throws both for zero and multiple matches. These identifiers are functional discriminators, not renderer instructions.

## Exact binary evidence

All offsets below are method-relative IL offsets. The DLLs were inspected read-only using the existing ILSpy 7.2.1 tool and Mono.Cecil metadata/IL reading. No MM process, KSP process, or compiled test was launched for this audit.

KSP assembly:

`Aetherfront-KSP-Smoke/KSP_x64_Data/Managed/Assembly-CSharp.dll`

SHA256: `d9e42483f25ee80a9c11d6c1c0a0d29b4ec78c1e08d76c971b71580c9cce51e4`

1. `Part.Awake`, token `0x06001d96`, calls `EffectSetup` at `IL_0089`, independently of an `EFFECTS` config block. `Part.EffectSetup`, token `0x06001e80`, constructs `new EffectList(this)` at `IL_001d` if the field is null, then stores it at `IL_0022`.
2. `ModuleRCSFX.SetupFX`, token `0x060020b2`, reads `runningEffectName` at `IL_0009` and calls `Part.Effect(string,float,int)` at `IL_0014`. `UpdatePowerFX`, token `0x060020b3`, calls the same overload at `IL_002d`. Its constructor defaults the identity to `running`.
3. `ModuleEnginesFX.FXUpdate`, token `0x0600215e`, forwards `directThrottleEffectName`, `spoolEffectName`, `runningEffectName`, and `powerEffectName` through `Part.Effect`; the running-effect call is `IL_00fd`. Engage, shutdown and flameout methods also forward their identities. Preserving all effect-name fields avoids analogous predicate breakage outside RCS.
4. `Part.Effect(string,int)`, token `0x06001e85`, forwards to `EffectList.Event` at `IL_000a`. `Part.Effect(string,float,int)`, token `0x06001e86`, forwards at `IL_000b`.
5. `EffectList.OnLoad`, token `0x06001366`, passes each named group to `LoadEffect` at `IL_0025`.
6. `EffectList.LoadEffect`, token `0x06001367`, reads the group name at `IL_0001`. If absent, it creates an empty `List<EffectBehaviour>` at `IL_0031` and adds the named dictionary entry at `IL_0036`. It reads the group's child-node count at `IL_0045`, initializes the loop index to zero, and branches to the loop condition at `IL_013b`. Zero children fail `blt IL_0052` at `IL_013d` and return at `IL_014c`. `GameObject.AddComponent` at `IL_00ee` and `EffectBehaviour.OnLoad` at `IL_0132` are inside that skipped loop.
7. `EffectList.Initialize`, token `0x06001365`, reads each group's list count at `IL_002e`; zero entries skip the `EffectBehaviour.OnInitialize` call at `IL_0041`.
8. `EffectList.Event(string,int)`, token `0x0600136a`, calls `TryGetValue` at `IL_0009`; failure branches directly to `ret` at `IL_0053`. For a present empty group, the zero count at `IL_0028` skips `OnEvent(int)` at `IL_003c`.
9. `EffectList.Event(string,float,int)`, token `0x0600136b`, has the same lookup at `IL_0009`; failure branches directly to `ret` at `IL_0054`. An empty list skips `OnEvent(float,int)` at `IL_003d`.

B9 assembly:

`Aetherfront-RC25-Work/P3e9e2e69/GameData/b9partswitch/Plugins/B9PartSwitch.dll`

SHA256: `625d3b10edd5b8f76bdaffdb808c965229e68b8edc57e8abdc68efe11938bd85`

- `ModuleMatcher.IsMatch`, token `0x06000074`, reads the runtime field at `IL_00c6` and compares it with the parsed identifier using `Object.Equals` at `IL_00cb`.
- `ModuleMatcher.FindModule`, token `0x06000072`, contains `Found more than one matching module` at `IL_0029` and `Could not find matching module` at `IL_0057`.
- `ModuleMatcher.NodeMatchesModule`, token `0x06000075`, also compares identifier and prefab values; its typed equality call is `IL_0103`.
- `B9PartSwitch.PartSwitch.PartModifiers.EffectDeactivator.Activate/Deactivate` only forward the original/new effect name to `Part.Effect(name, 0f, -1)`. Empty groups are safe for those calls too.

## Confidence and limits

Confidence is high for the audited binary's empty-group semantics and for retaining effect identities instead of stripping B9 predicates. Dictionary lookups and empty-list checks remain; this is not literally zero CPU work.

`EffectList.ConstructEffectList` also discovers already attached `EffectBehaviour` components through `hostPart.GetComponents<EffectBehaviour>()`. Empty config groups do not destroy components already attached to a prefab or added later by another plugin. The precise claim is that these empty config groups create no particle/audio behaviours and invoke no behaviours when their lists are empty. No graphical lifecycle or flight test was performed here.

The main-owned proof should distinguish renderer modifiers from retained physics modifiers, require exactly one target for every retained discriminator, and check that all final effect groups have zero children and values while their original names and module field values remain unchanged. Do not require every originally logged B9 warning to correspond to a Waterfall identifier.
