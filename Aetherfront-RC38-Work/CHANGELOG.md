# Aetherfront RC38 — changes since RC37

- Replaced LMP toolbar and flag artwork with APF branding, including the Windows window/taskbar icons and Aetherfront window title.
- Fixed WASD editor-camera wheel handling: scrolling over UI such as the parts palette no longer feeds camera scrolling. Viewport wheel input and mouse look retain their existing behavior.
- Added monitor-sized borderless windowed mode so switching applications does not request exclusive-fullscreen minimization. The tested monitor runs at 1920×1080. The desktop preference can opt out.
- Added ZTheme 1.2.2 and HUDReplacer 1.4.2 to both Regular and Potato. These are shared UI packages; Regular retains its visual-effects profile and Potato retains its lighter graphics.
- Disabled Parallax scatter collisions through the final compatibility configuration (`collisionLevel = -1`).
- Hardened personal-clock transitions when leaving warp or recovering from a loading stall. A new own-subspace proposal cannot trail the client's last stable offset; the server normalizes bounded timing skew before acknowledging it. Client acknowledgement aligns forward. Existing durable rollback guards remain strict. Added diagnostics for remaining disconnect reports.
- Added automatic DLC repair before stock expansion loading. Signed canonical payloads repair changed, missing and obsolete files in the same startup, with verified staging, atomic replacement, rollback and interrupted-transaction recovery. Users no longer need to delete `SquadExpansion` manually for a mismatched installation.
- Fresh starters defer missing DLC payloads to normal signed pack preparation rather than showing a false failure popup. DLC repair adds no extra restart after the normal initial install/apply restart.
- Added versioned DLC receipts. Unchanged launches reuse metadata receipts without another full DLC extraction or content hash pass. Existing mod downloads remain reusable; only changed/new packages need downloading.
- Updated client, catalog, pack and recovery version markers to RC38 while preserving the existing settings protocols.

Validation: 821 automated assertions, native bundle/inventory verification, both profile catalogs under CLR and KSP Mono, disposable-key signing/tamper checks, actual startup and editor scene checks, deliberate real-DLC corruption repair, and unchanged-DLC receipt reuse.

Scope: this does not establish that every reported disconnect has the same cause. Exact affected-player warp/load reproduction, physical wheel/Alt-Tab interactions, packet-loss/USI testing and KAL UI interaction were not part of the completed local acceptance. ZTheme recommends HUDReplacer 1.4.3; the maintained public release available for this package is 1.4.2. The existing KSPCF policy continues to disable Making History mission-editor/extra-launch-site features while retaining its parts.
