# APFDiscordBridge

Server-only LMP plugin. No client mod required.

At startup it creates:

- `APFDiscordBridge/ready.json`
- `APFDiscordBridge/inbox/`
- `APFDiscordBridge/events/`

The bot atomically writes `{ "author": "...", "text": "...", "expiresAt": "..." }` into `inbox`. The plugin validates size, expiry, and content; calls LMP's existing `say` command; logs the relay; then deletes the item. Incoming LMP chat is emitted as event JSON for reliable bot ingestion. Malformed bot-side event files are quarantined instead of retried indefinitely.

Install only during planned maintenance: copy `APFDiscordBridge.dll` beside the server's `Plugins` directory, start the server, verify `Bridge ready`, then test with a non-destructive chat message. The project defaults to the matching local LMP install under the Administrator profile; CI/build scripts can override it with `-p:LmpServerRoot=<LMP root>`.
