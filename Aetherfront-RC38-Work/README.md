# RC38 local update — not published

The complete native bundle is assembled at
`build/rc38-release/Aetherfront-Release-kesas-rc38-UNPUBLISHED.zip` beneath the
controller directory. It contains the versioned client template, matching server
patch, pack manifest, media references and SHA256 inventories. Both profiles and
the server manifest pass the shipped validator under CLR and KSP Mono. Native
release verification and a disposable-key signing/verification smoke test are
required; production keys are never used locally. The two new content-addressed
media archives are in `build/rc38-themes/Media`; the existing ten references reuse
the existing immutable downloads.

`Build-Release.py <controller>` checks the 821-assertion candidate and exact
native-method/version audits against the packaged inputs. `tests/rc38-live`
contains separate automated game-run evidence using the existing disposable
`work/apf-test`. The user's DLLs, saves and identity are untouched. Original
fixture logs and its prior test driver are preserved. Commands are PID-scoped;
no automatic quit command is queued. Final results distinguish verified passes,
fixture limitations and unperformed physical-input/multiplayer checks.

This local candidate replaces LMP-branded toolbar and flag textures with the
user-supplied APF artwork. Legacy texture paths remain stable so existing craft
flags and the stock launcher button keep their references. APF.ico includes
16/24/32/48/64/128/256-pixel images. The new client plugin assigns the process's
window/taskbar icons and Aetherfront title without editing KSP_x64.exe or Steam.
The original input is 50×46; conversion preserves its proportions and artwork.

WASD Editor Camera 0.7.3.3 reads the wheel four times in LateUpdate without a UI
hover check. A method-scoped Harmony transpiler substitutes only those reads:
the camera gets zero wheel input over Unity UI or a camera control lock, while
the part list still receives the original input. Mouse look, keys, viewport
wheel movement and the existing right-button lock cleanup remain unchanged.
The known WASD module fingerprint and exactly four reads are required.

On Windows, the default is a native borderless window covering the current
monitor, with Unity explicitly in Windowed mode. Unity FullScreenWindow alone
can still minimize when the shipped player's visibleInBackground flag is false.
No focus stealing, always-on-top behavior or foreground-window/input hooks are
used. Explicit minimization is respected. Native operations reject windows
belonging to another process. KSP's own executable and boot settings are unchanged.
`APF/DesktopPresentation.cfg` preserves the optional `borderlessWindow = False`
override; defaults are created only if no preference file exists.

Run Build-Presentation.ps1 to assemble the small overlay; Test-Presentation.ps1
also runs tests through the installed KSP Mono runtime. These read the specified
game's reference assemblies but do not install files or manipulate its window.
Native tests create only their own invisible window and require foreground focus
and invisibility to remain unchanged. The test covers the actual shipped WASD
method and coexistence with RC37's existing input-lock patches.

The APFBeta client DLLs have not been altered or restarted. A later user-requested
ParallaxNoCollisions.cfg preference override was added locally as described below.
Physical mouse-wheel and Alt-Tab interactions require a manual reproduction; no
computer-use input is performed. No source commit, push, production-server change
or publication is performed. The presentation ZIP alone is an overlay input;
use the complete native release bundle for eventual guarded host publication.

## Personal clock transition candidate

The current server was reproduced entering its mission-time disconnect path
when a new own-subspace offset trails the prior offset by only 50 ms. The durable
store permits 1 ms conversion noise, while the existing client physics-timesync
window extends to 3.5 seconds. An Escape/warp-stop transition is a plausible
trigger; the exact player's Escape sequence has not been reproduced in Unity.
Stock PauseMenu calls FlightDriver.SetPause; LMP blocks true pause in multiplayer,
so assuming the whole simulation necessarily pauses would be incorrect.

PrepareNewSubspace now checkpoints the stable personal timeline and raises only
a new own-subspace's small trailing offset to the saved/current floor before
the native server broadcasts it. The published offset is canonical from its
first acknowledgement. Proposals more than 3.5 seconds behind are refused;
foreign existing-timeline rollback and the original 1 ms disk guard stay strict.
No existing clock record, universe, life-support history or shared subspace is
rewritten to forgive a backwards jump. The server patch changes one native method;
all 4,288 other method bodies are verified unchanged. Identity, universe and
native sender/finite-number/subspace-limit validation remain required.

The client queues its own acknowledgements off-thread, then aligns forward using
the existing vessel/UT method on the Unity thread. It neither rewinds time nor
fires a second warp-stopped event. Warp events record menu/pause/rate/subspace/UT
metadata for a future real reproduction; server failures include previous and
proposed UT. No new wire messages, database format or signing keys are introduced.

Build-Clock.ps1 builds/tests these separate local client/server candidates.
The focused test passes 72 authenticated boundary/security assertions; the
existing server suite passes 110 assertions; client clock tests pass 33 checks
in KSP Mono, and existing Mono continuity tests pass 401 checks across 42 timer
methods. Actual Escape during on-rails/physics warp, packet-loss/reordering and
live multiplayer/USI behavior remain release gates. No production update yet.

Further investigation found a 21.2-second forward clock correction during flight
initialization in the running local RC37 log, beyond the server's 3.5-second
boundary normalization window. The client now captures its own stable subspace
offset before entering warp and floors the newly generated packet offset at
that value. Its factory, identity and asynchronous send pipeline are retained.
This handles prolonged simulation/load stalls without increasing server rollback
tolerance. The per-system floor is advanced on an own acknowledgement and cleared
on disconnect. Tests cover 21.2/120-second stalls and floor lifecycle isolation.

See OTHER-DISCONNECTS.md for confirmed non-Escape paths, the separate stock
auto-warp rewind risk and the evidence still needed to attribute actual incidents.

## ZTheme and collision preference

ZTheme 1.2.2 and the maintained HUDReplacer 1.4.2 release are pinned by upstream
asset SHA256 in Theme-Dependencies.json. Build-Themes.py preserves upstream
licenses and bytes, normalizes safe archives, verifies PNGs and theme paths,
and adds the same two managed downloads to Regular and Potato. Only the server
manifest receives the client-only roots/DLL fingerprint exceptions; client
profiles have no overlapping exceptions. Existing ModuleManager and HarmonyKSP
dependencies are reused. ZUI is optional and was not requested or included.

ZTheme upstream recommends HUDReplacer >=1.4.3, while the maintained repository's
public releases and CKAN currently stop at 1.4.2. This candidate uses that published
stable release, not an unreviewed branch build. Metadata dependency resolution
loads all 67 HUDReplacer types successfully without executing the plugin. Both
profiles and the server manifest validate under the shipped CLR and KSP Mono
validators, including the retained injected-overlap rejection check. Actual HUD,
KAL and editor behavior is still a release gate.

The user's saved selection is `full` (Regular). Its existing live log reports
Parallax collisionLevel 4. The exact installed Parallax ConfigLoader enables
colliders when global collisionLevel > -1 and scatter tier >= that level: zero
would enable all tiers. Compatibility/ParallaxNoCollisions.cfg applies -1 in
ModuleManager's final pass. This small preference patch was copied into APFBeta's
existing AetherfrontCompatibility root and the candidate overlay. Original mod
archives, DLLs and other graphics settings were preserved. A fresh launch must
report collisionLevel -1 before runtime-off is claimed. Only separate disposable
test instances are launched/closed by this task.

## DLC repair in the same startup

The first automated run's popup was a fixture issue: its old temporary install
had no signed Making History/Breaking Ground payloads. APFBeta's existing log
reports both DLCs ready. The earlier remote-player failure was different:
the previous bootstrap explicitly refused any pre-existing DLC differing from
the pack and instructed users to preserve it rather than repairing it.

The RC38 bootstrap now validates the pinned original archives, stages verified
canonical DLCs, and swaps a mismatched expansion before stock expansion/database
loading. Missing, changed and obsolete files are repaired in that invocation.
Both expansion payload hashes remain unchanged. Ordinary mod packages still
cannot write stock roots; this narrowly scoped bootstrap owns only the two
named expansions. Saves, settings, identity, trust and unrelated GameData are
outside its repair paths. Original assets remain in a recovery directory until
the verified replacement and durable receipt commit; temporary backups are
then deleted. A durable journal recovers interrupted directory swaps on startup.
Read-sharing locks prevent replacing a payload after its signature hash check.
Paths, links, executables, entry counts and expanded bytes are checked.

Fresh starters lacking managed payloads defer to normal signed pack preparation
instead of showing a false DLC-failure popup. DLC repair needs no extra restart
after that normal initial install/apply restart. Invalid signed source payloads
or filesystem failures remain errors; trust checks are never bypassed.
Receipt version 38 forces one full check of older DLC installations. Subsequent
unchanged launches check metadata and reuse the receipt without hashing or
extracting the full DLC content again. 138 filesystem/recovery/security checks
run under the actual KSP Mono runtime, including exact crash-state snapshots.

The first live run also found that Mono's `Process.MainWindowHandle` returned
zero. Native own-process Unity-window enumeration now supplies the HWND, with
hidden-window discovery regression checks. The next live run reported both
deliberately damaged real DLCs repaired before stock loading and APF icons set.
The final run loaded the exact packaged DLLs, reused both DLC receipts, and
entered/exited both editor facilities. The preserved KSPCF policy intentionally
disables Making History mission-editor/launch-site features while retaining its
parts; its stock Installed flag therefore remains false. The raw probe's initial
assumption about that flag is retained and classified as a harness error, not a
passed check or a DLC-install failure. Physical scrolling, Alt-Tab, KAL and live
warp reproduction remain explicitly unperformed. Full results accompany the
unpublished review ZIP in the user's Music directory.
