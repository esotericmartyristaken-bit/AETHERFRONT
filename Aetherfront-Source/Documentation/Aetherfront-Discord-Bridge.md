# Aetherfront Discord bridge

The isolated fork ships `Server/Plugins/APFDiscordBridge.dll`. It does not load Discord.NET inside LMP. The server and bot exchange bounded atomic JSON files below the fork server directory:

```text
Server/APFDiscordBridge/
  ready.json
  inbox/*.json
  events/*.json
  results/*.json
```

The bridge supports the existing allow-listed server command path and LMP chat relay. It also publishes public Aetherfront Chronicle records as `type: "aetherfront-event"` files. Each record carries the event ID, monotonic state revision, public event type, faction/entity IDs, timestamp, and public event fields. Private Chronicle records are never exported.

`chronicle-cursor.json` is an atomic durable cursor. It prevents replay after a bridge restart while allowing files already written to remain available while the bot is offline. The bridge caps pending event files and event payload size; it never executes downloaded content.

The Discord renderer must consume `aetherfront-event` records only after validating `serverPid` against `ready.json` and the live server process, exactly as it validates `lmp-chat`. Suggested presentation: faction name/flag lookup from the APF API, concise event text, and a link to the public Chronicle feed. Do not expose `actorAccountId` unless the account has opted into a public profile.

For the current LMP bot, the bridge also writes one compatibility `lmp-chat` event per public Chronicle event (`from: "Aetherfront"`). This makes the public event visible immediately through the existing chat-forwarding path; a future rich renderer can consume the structured event instead.

This bridge is installed only in `Aetherfront-Fork`. The main live APF LMP server remains unchanged.
