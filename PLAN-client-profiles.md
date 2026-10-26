# Plan: Low-graphics alt client + normal main client (RAM x Bloxstrap)

Executor: Codex. Planner notes are based on the repo at `b7c7055` plus the **uncommitted** FastFlags work in the
working tree (do not revert it — it is the base for this plan).

## Goal

- Accounts in "alt" groups launch on an **extremely low-graphics Roblox client**.
- Only the **main** account launches on a **normal-graphics client** (the user's own Bloxstrap setup, untouched).
- RAM and Bloxstrap cooperate: RAM detects Bloxstrap, reads its version/paths, never pollutes the user's Bloxstrap
  flags, and falls back to Bloxstrap's launch path whenever it must.

## Facts verified on the user's machine (2026-10-24)

| Fact | Consequence |
|---|---|
| `HKCR\roblox-player\shell\open\command` = `"%LocalAppData%\Bloxstrap\Bloxstrap.exe" -player "%1"`, Bloxstrap **2.11.4** | RAM's protocol launch goes through Bloxstrap. |
| Bloxstrap log: every launch logs `ClientSettings\ClientAppSettings.json has been copied to the version folder` | Whatever is in `Bloxstrap\Modifications\ClientSettings\ClientAppSettings.json` at launch time is what that client gets. |
| Bloxstrap bootstrapper exits ~5 s after `Started Roblox`; activity tracking runs in a **separate** `Bloxstrap.exe` watcher process | `Launcher.WaitForExit()` in `Account.JoinServer` returns shortly after the client starts. OK to keep. |
| `%LocalAppData%\Bloxstrap\PlayerState.json` → `"VersionGuid": "version-cec3ad5889b447cf"`; client at `Bloxstrap\Versions\<guid>\RobloxPlayerBeta.exe` | Use this to find Bloxstrap's current client. |
| Stock Roblox is **also** installed: `%LocalAppData%\Roblox\Versions\version-2366ba214ec740ca` (different version from Bloxstrap's) | Version resolution must look in both places. |
| `Bloxstrap\Modifications\ClientSettings\ClientAppSettings.json` currently contains **alt flags** (`DFIntTextureQualityOverride=1`, `DFIntTaskSchedulerTargetFps=15`, ...) left by older RAM builds | The main is currently running with alt flags. Needs a one-time cleanup (Phase 6). |
| In-game settings live in shared `%LocalAppData%\Roblox\GlobalBasicSettings_13.xml` | One file for all clients. Roblox reads it at startup and writes it back when it saves settings or exits. |
| Roblox only honors **allowlisted** FastFlags (devforum t/3966569). `DFIntTaskSchedulerTargetFps` is ignored | FPS has to be capped through `GlobalBasicSettings` `FramerateCap`, plus OS-level throttling (Phase 5). |

## Architecture decision

**Primary: dedicated alt client.** RAM keeps its own client folder `AltClient\<version-guid>\` (next to
`RAMSettings.ini`). It is **hard-linked** from the matching Roblox/Bloxstrap version folder, so it costs almost no
disk space. Only its `ClientSettings\` folder is a real, separate directory, holding the low-graphics flags
permanently. Alts are started by running `AltClient\<guid>\RobloxPlayerBeta.exe` directly. The main is always
launched through Bloxstrap (protocol handler) with the user's own flags, and RAM never touches those flags.

This avoids swapping one shared `ClientAppSettings.json` back and forth, which is what causes races today: an alt
launch and a main launch close together, Bloxstrap copying at the wrong moment, or a crash leaving alt flags on the
main.

**Fallback: the existing shared-client swap.** This is the merge-on-top + backup/restore code already in
`ClientSettingsPatcher`. RAM uses it for an alt launch when the dedicated client can't be prepared, for example when
no installed version folder matches the live Roblox version. Launching through Bloxstrap also makes Bloxstrap
update Roblox, so the next alt launch can use the dedicated client again.

**In-game settings (`GlobalBasicSettings`) stay shared.** RAM coordinates them with a launch gate and a saved "main
snapshot" (Phase 4).

---

## Phase 0: Ground rules

- Target: .NET Framework 4.7.2, C# 8 (`LangVersion 8.0`). Old-style csproj: **every new `.cs` file must be added as
  `<Compile Include=...>` in `RBX Alt Manager/RBX Alt Manager.csproj`**.
- Match surrounding style: `Program.Logger.Info/Error`, `AccountManager.General.Get<T>/Set/Exists`,
  `AccountManager.IniSettings.Save("RAMSettings.ini")`, `[FastFlags]`-style log prefixes, terse comments.
- Do **not** stage or modify the unrelated untracked files: `index.html` (unrelated page), `base.config`,
  `base_account.cs` (UTF-16 copies), `g-intention.txt`, `log.txt`.
- `g-intention.txt` (login browser switch / anti-detection) is **out of scope** for this plan.
- Build: find MSBuild with `"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe`,
  then `MSBuild "RBX Alt Manager.sln" /p:Configuration=Release /p:Platform="Any CPU" /restore`. Output goes to
  `build\Release\`. If MSBuild isn't found, try `dotnet msbuild` with the same arguments, and report it if that
  fails too.

## Phase 1: Account roles (Main vs Alt), one source of truth

New file `RBX Alt Manager/Classes/ClientProfiles.cs` (static class):

- `enum ClientRole { Main, Alt, Normal }`
- `static string MainGroup => General.Exists("MainGroup") ? General.Get<string>("MainGroup") : "Main";`
  Add the default `if (!General.Exists("MainGroup")) General.Set("MainGroup", "Main");` next to the other defaults
  in `AccountManager` (around line 122).
- `static string NormalizeGroup(string group)`: trim, strip a leading `^\d{1,3}\s?` prefix (same regex as
  `FindGroupProfile`).
- `static ClientRole GetRole(Account a)`:
  - Main if `NormalizeGroup(a.Group)` equals `NormalizeGroup(MainGroup)` (case-insensitive).
  - Alt if `GroupFastFlags` is enabled and `FindGroupProfile(a)` returns a file.
  - Normal otherwise.
- **The main never gets a profile, even if `Default.json` exists.** Change `ClientSettingsPatcher.FindGroupProfile`
  to return `null` for the main.
- Move `FindGroupProfile` into here or make it `internal`. Keep its matching order unchanged.
- `RobloxWatcher.cs:105`: replace the hard-coded `"Main"` check with `ClientProfiles.GetRole(account) != ClientRole.Alt`.
  Trim only accounts that are **known alts**: an unknown process (`account == null`) must not be trimmed.

Acceptance: an account in group `Main` (or `01 Main`) is always Main, even with `Default.json` present. An account
in `Alts` with `FastFlags\Alts.json` present is Alt.

## Phase 2: Bloxstrap integration

New file `RBX Alt Manager/Classes/Bloxstrap.cs` (static class `Bloxstrap`). It must work for Bloxstrap and for forks
with a `Modifications` folder (Fishstrap, Voidstrap). Move the existing `ClientSettingsPatcher.GetBootstrapperDirectory()`
here and keep a forwarding call so nothing breaks.

- `string Directory`: the existing registry detection.
- `bool IsInstalled => Directory != null`.
- `string ModificationsClientSettings => Path.Combine(Directory, "Modifications", "ClientSettings")`.
- `string GetVersionGuid()`: read `PlayerState.json` → `VersionGuid`. Fall back to `State.json` → `VersionGuid`
  (older builds) and then `State.json` → `Player.VersionGuid`. Parse defensively with `TryParseJson`; return null
  on any failure.
- `string GetClientDirectory()`: `Versions\<guid>` when it contains `RobloxPlayerBeta.exe`, else null.

Add `ClientProfiles.FindClientSource(string wantedVersion)`. It returns the first folder named `wantedVersion` that
contains `RobloxPlayerBeta.exe`, checking in this order:
1. `Bloxstrap\Versions`
2. `%LocalAppData%\Roblox\Versions`
3. `%ProgramFiles(x86)%\Roblox\Versions`

`wantedVersion` is `AccountManager.CurrentVersion`, which `AccountManager` already fetches from
`clientsettings.roblox.com/v1/client-version/WindowsPlayer` → `clientVersionUpload`. If `CurrentVersion` is still
null (fetch not finished or failed), fall back to `Bloxstrap.GetVersionGuid()`.

Acceptance: on this machine, `Bloxstrap.GetVersionGuid()` returns `version-cec3ad5889b447cf` and `FindClientSource`
picks whichever installed folder matches the live version.

## Phase 3: Dedicated low-graphics alt client

New file `RBX Alt Manager/Classes/AltClient.cs` (static class, all work done under a `lock`).

`static bool TryPrepare(Account account, out string exePath, out string reason)`:
1. Return false if setting `UseAltClient` is off, or the role isn't Alt.
2. `source = ClientProfiles.FindClientSource(...)`. If null, return false with reason
   `"no installed client matches <version>"`. The caller falls back to the Bloxstrap launch, which updates Roblox.
3. `target = Path.Combine(Environment.CurrentDirectory, "AltClient", Path.GetFileName(source))`.
4. If `target\.ram-source` is missing or its content isn't `source`, (re)build the folder:
   - Walk `source` recursively. **Skip the `ClientSettings` directory entirely.** For every other file, call
     `CreateHardLink(targetFile, sourceFile, IntPtr.Zero)` (P/Invoke `kernel32`, `CharSet.Unicode`, `SetLastError`).
     If hard-linking fails (different volume, error 17), fall back to `File.Copy` and log it once.
   - **Copy** `AppSettings.xml` instead of linking it, so writes to either copy never reach the other.
   - Build into `target + ".tmp"` and rename it to `target` when done, so a half-built folder is never used.
     Write `.ram-source` last.
5. Every time, write `target\ClientSettings\ClientAppSettings.json` from the account's group profile JSON:
   - Remove the `_RobloxSettings` key first.
   - Do **not** merge the user's Bloxstrap flags. The alt client gets only the alt profile.
   - Keep the existing `UnlockFPS` injection behavior consistent with `ApplyProfile`.
   - If two alts in different groups launch back to back, the lock plus the launch gate (Phase 4) keep them in order.
6. Clean up stale clients: delete other `AltClient\version-*` folders when no running `RobloxPlayerBeta` process has
   its exe under them. Check with `process.MainModule.FileName` in try/catch, because the elevated second process
   throws. Deleting hard links never touches the source files.
7. `exePath = target\RobloxPlayerBeta.exe`.

Also refresh the alt client from `CreateLowGraphicsPreset` (or just let the next launch rewrite it, since step 5 runs
on every launch).

### Launch path changes (`Classes/Account.cs`, `JoinServer`)

- Refactor first: the three `roblox-player:1+...` strings (lines ~655–659) differ only in the `placelauncherurl`.
  Build that URL into a local and assemble the URI once in a local `launchUri`. Keep the behavior identical.
- Replace the unconditional `ClientSettingsPatcher.PatchSettings(this)` near the top with a role-aware step:
  - **Main**: no FastFlag patch at all. Call `RestoreOriginals()` (a no-op when there is no backup) so a left-over
    swap can't leak into the main. Apply the main in-game snapshot (Phase 4). Launch through the protocol (Bloxstrap)
    exactly as today.
  - **Alt**: if `AltClient.TryPrepare(...)` succeeds, apply the alt `_RobloxSettings` (Phase 4), then
    `Process.Start(new ProcessStartInfo(exePath, launchUri) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exePath) })`.
    There is no launcher to wait for, so call `AccountManager.Instance.NextAccount()` after the client process is
    found (or after 10 s), then `AdjustWindowPosition`.
    If `TryPrepare` fails, log the reason and use the existing `PatchSettings(this)` + protocol launch (fallback).
  - **Normal**: today's behavior (legacy `CustomClientSettings` / `UnlockFPS` through `PatchSettings`).
- **Mandatory verification before finalizing the argument format.** Launch the main once through Bloxstrap, then run
  `Get-CimInstance Win32_Process -Filter "name='RobloxPlayerBeta.exe'" | select CommandLine`.
  Make the direct alt launch pass **exactly the same shape** of arguments that Bloxstrap passed. Bloxstrap 2.x is
  expected to pass the raw `roblox-player:1+...` URI as the single argument. If it passes
  `--app -t ... -j ... -b ... --launchtime=...` instead, mirror that. Write down the observed format in a comment.
- Tracker ID parsing: add `Utilities.GetBrowserTrackerId(string commandLine)`. It matches `-b (\d+)` **or**
  `browsertrackerid[:=](\d+)` (case-insensitive). Use it in `Account.cs:532`, `Account.cs:712`, and
  `RobloxWatcher.cs:80`. Without this, URI-launched clients aren't matched to their account.
- Leave the broken `UseOldJoin` branch alone. It never calls `Process.Start`. Mention it in the final report; fixing
  it is optional and not part of this plan.

Acceptance: launching 3 alts plus the main produces 3 `RobloxPlayerBeta.exe` processes whose exe is under
`AltClient\` and 1 under `Bloxstrap\Versions\`. `Bloxstrap\Modifications\ClientSettings\ClientAppSettings.json` is
byte-identical before and after.

## Phase 4: In-game settings coordination (`GlobalBasicSettings_13.xml`)

The flags force low render quality on alts (`DFIntDebugFRMQualityLevelOverride`, texture override) no matter what
the shared XML says. The XML still controls **FramerateCap** and **MasterVolume** for alts, and the **main's**
graphics. An alt that exits writes its low values back into the XML, and the main would read them on its next launch.

- **Launch gate**: add `static SemaphoreSlim LaunchGate = new SemaphoreSlim(1, 1)` in `ClientProfiles`.
  `JoinServer` waits on it (`await WaitAsync`) before any settings write. It releases the gate when one of these
  happens first:
  - the new client process has been found and 8 s have passed since its `StartTime` (Roblox has read the XML by then)
  - a 30 s timeout
  - any exception
  Use try/finally so a failed launch never leaves the gate held.
- **Main snapshot**: file `FastFlags\Main.settings.json` holds a subset of XML properties: `SavedQualityLevel`,
  `GraphicsQualityLevel`, `FramerateCap`, `MasterVolume`, plus any other keys the user lists in the file.
  - `CaptureMainSnapshot()`: read those properties from the XML and save them. Values come from
    `<Properties><token|int|float|bool name="X">`; reuse the existing `ApplyGlobalBasicSettings` lookup logic in
    reverse.
  - Capture **manually** with a button (Phase 6).
  - Capture **automatically** when RAM sees the main's `RobloxPlayerBeta` process exit: wait 2 s, then capture.
    Skip the capture if the captured values equal the alt profile's `_RobloxSettings` values, because that means an
    alt wrote last.
  - If no snapshot exists yet, create one from the XML the first time RAM is about to apply alt settings.
- **Per launch**:
  - Main → write the snapshot into the XML (if one exists).
  - Alt → write the profile's `_RobloxSettings` into the XML. After the gate's settle point, write the main snapshot
    back.
  - This replaces the backup/restore of the XML for the main. Keep the existing `.backup.json` mechanism for
    `ClientAppSettings.json` in the fallback path only.
- Keep `RestoreOriginals()` on startup and on close (already wired in `AccountManager.cs`). Also have it write the
  main snapshot back to the XML.

Acceptance: launch alt, then main, then close the alt, then relaunch the main. The main still opens with its own
graphics level, FPS cap and volume.

## Phase 5: Make alts cheaper (OS level, optional per setting)

Do this in `RobloxWatcher.CheckProcesses`, only for processes matched to an **Alt** account, and only once per
process (track handled PIDs in a `HashSet<int>`):
- `AltEfficiencyMode` (default on when `UseAltClient` is on):
  - `process.PriorityClass = ProcessPriorityClass.BelowNormal`.
  - Enable EcoQoS with `SetProcessInformation(handle, ProcessPowerThrottling, ref PROCESS_POWER_THROTTLING_STATE { Version = 1, ControlMask = 1, StateMask = 1 }, size)`
    (P/Invoke, Windows 10 1709+). Wrap it in try/catch and log failures once.
- `AltWindowWidth` / `AltWindowHeight` (default `0` = off, suggested `800x600`): in `AdjustWindowPosition`, when the
  account is Alt and has no remembered position, `MoveWindow` to that size. A smaller window renders far fewer pixels.
- The existing `TrimAltsMemory` now uses the role helper from Phase 1.
- Never touch the focused window's process priority. The watcher already skips focused windows; keep that.

## Phase 6: Settings UI and one-time migration

Add a **"Client Profiles..."** button to Settings → Miscellaneous (`SettingsForm.Designer.cs`, same pattern as
`CreatePresetButton`). It opens a new `Forms/ClientProfilesForm.cs`. Build the controls in code (no Designer file)
to keep Designer churn low, and call the same `ApplyTheme` pattern `SettingsForm` uses. Controls:

- **Main group** (TextBox) → `General.MainGroup`
- **Use dedicated low-graphics alt client** (CheckBox) → `General.UseAltClient`. Default: on when Bloxstrap or Roblox
  is detected.
- **Alt efficiency mode** (CheckBox) → `General.AltEfficiencyMode`
- **Alt window size** (two NumericUpDown, 0 = off) → `General.AltWindowWidth/Height`
- **Capture main's in-game settings now** (Button) → `CaptureMainSnapshot()`, then show the captured values.
- **Clean Bloxstrap flags** (Button):
  - Load `Bloxstrap.ModificationsClientSettings\ClientAppSettings.json`.
  - Remove every key that appears in the current alt profile JSON(s), `alt_temp.json`, or `LowGraphicsPreset`, plus
    `DFIntTaskSchedulerTargetFps`.
  - Save a backup to `FastFlags\bloxstrap-flags.bak.json` first.
  - Show a confirmation listing the removed keys. Refuse while any Roblox process is running.
- Read-only status labels:
  - Bloxstrap path and version (`FileVersionInfo` of `Bloxstrap.exe`), plus its `VersionGuid`
  - Live Roblox version (`CurrentVersion`)
  - Alt client folder and version, or "not built"
  - Detected groups and their role: list `AccountManager.AccountsList` groups, each with Main / Alt (profile file
    name) / Normal

Save on change with `IniSettings.Save("RAMSettings.ini")`, like the other settings.

Fix while in the Designer: `TrimAltsMemoryCB` and `OpenFastFlagsButton` both use `TabIndex = 14`. Renumber them.

## Phase 7: Docs

- Update the README text generated in `ClientSettingsPatcher.EnsureFastFlagsDirectory()` **and**
  `FastFlags/README.txt` (they must match). Describe the main group, the dedicated alt client, the main snapshot,
  and that the main never uses `Default.json`.
- Add a "Dedicated alt client" section to `FastFlags-Changelog.html` in the same style. Update its Setup Tutorial:
  1. Clean Bloxstrap flags (button).
  2. Put the main in group `Main`.
  3. Put alts in group `Alts`.
  4. Create Low Gfx Preset.
  5. Capture main settings.
  6. Make sure Multi Roblox is on.

## Phase 8: Test matrix (manual, report results)

| # | Scenario | Expected |
|---|---|---|
| 1 | Build Release | 0 errors, no new warnings in touched files |
| 2 | Fresh start, `UseAltClient` on, launch 1 alt | `AltClient\<guid>\` created via hard links (folder size on disk ≈ few MB, check with `fsutil hardlink list`); alt runs with low textures/grey sky; Bloxstrap flags file unchanged |
| 3 | Launch 3 alts in a row (default 8 s delay) and with AsyncJoin on | All 3 from `AltClient\`, each matched to its account (tracker ID), BelowNormal priority |
| 4 | Launch main while alts run | Main from `Bloxstrap\Versions\`, full graphics, own FPS cap/volume |
| 5 | Close alts, relaunch main | Main still on its own in-game settings (snapshot works) |
| 6 | Rename `AltClient\<guid>`'s source away / simulate version mismatch (set `CurrentVersion` to a fake value in debugger) | Falls back to Bloxstrap launch + shared swap; originals restored ≤ 15 s after launch; log explains why |
| 7 | Kill RAM while an alt is launching (fallback path), restart RAM | `.backup.json` restored on startup; main snapshot written back |
| 8 | `Default.json` present, launch main | Main gets no profile |
| 9 | Bloxstrap not installed (rename registry handler value temporarily, or test on stock Roblox) | Alt client built from `Roblox\Versions`; main launches through stock Roblox |

## Known unknowns (verify, don't assume)

1. **Direct-launch argument format.** See the mandatory verification in Phase 3. If Roblox refuses the direct launch
   (error dialog, or it re-launches the installer), stop and report. Keep the fallback path as the default for alts
   until it's resolved.
2. **Whether Roblox writes `GlobalBasicSettings` on exit or only on setting change.** Phase 4 is safe either way.
   Note the observed behavior in the final report.
3. **Allowlisted flags.** Don't add flags to `LowGraphicsPreset` unless they're on the current Roblox allowlist. If
   unsure, leave the preset as is.
4. **Whether `FramerateCap` accepts values below 30.** Test 15 and 20. If they're ignored, keep 30 and rely on EcoQoS.

## Deliverables

- New files: `Classes/ClientProfiles.cs`, `Classes/Bloxstrap.cs`, `Classes/AltClient.cs`, `Forms/ClientProfilesForm.cs`
  (all registered in the csproj).
- Modified files: `Classes/Account.cs`, `Classes/ClientSettingsPatcher.cs`, `Classes/RobloxWatcher.cs`,
  `Classes/Utilities.cs`, `AccountManager.cs`, `Forms/SettingsForm.cs`, `Forms/SettingsForm.Designer.cs`,
  `FastFlags/README.txt`, `FastFlags-Changelog.html`.
- A short final report covering: test matrix results, the observed Bloxstrap command-line format, the answers to the
  known unknowns, and anything skipped.
- Commit only if the user asks. Suggested split: (1) the existing uncommitted FastFlags work as is, (2) Phases 1–2,
  (3) Phase 3, (4) Phases 4–5, (5) Phases 6–7.
