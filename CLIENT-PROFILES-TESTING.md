# Testing client profiles

The Release build is in `build/Release`. Close the previous RAM instance before
starting `RBX Alt Manager.exe` there. Use your usual account database and settings
with this build, or add one test main and one test alt. RAM stores its settings,
FastFlags and AltClient folders in its working directory.

## First setup

1. Close Roblox. Open **Settings → Miscellaneous → Client Profiles**.
2. If an older build left alt flags in Bloxstrap, click **Clean Bloxstrap flags**.
   Review the keys before confirming. Keep `FastFlags/bloxstrap-flags.bak.json`.
3. Put the main account in **Main**, and alts in **Alts**. Number prefixes such as
   `01 Main` and `02 Alts` work. You can change the Main group in Client Profiles.
4. In Miscellaneous, click **Create Low Gfx Preset**. Leave **Group-Based
   FastFlags** on. In Client Profiles, enable the dedicated alt client and
   efficiency mode. Optional window size: width **800**, height **600**.
5. Enable **Multi Roblox**. Use the regular join path; leave **UseOldJoin** off.
6. Launch the main **through RAM**. Set the main's desired graphics, FPS cap and
   volume in Roblox, then close it. Wait for settings to save, then click
   **Capture main's in-game settings now**. Check the displayed values.
7. Refresh Client Profiles. **Direct arguments** should show **URI** or
   **Switches**. This observation is required once per RAM session. If it stays
   unverified, alts use protocol fallback and the log explains why.

## Main smoke test

1. After cleanup, record the hash of Bloxstrap's flags file, if it exists:

   ```powershell
   $flags = "$env:LOCALAPPDATA\Bloxstrap\Modifications\ClientSettings\ClientAppSettings.json"
   Get-FileHash -LiteralPath $flags
   ```

2. Launch one alt. Expect an `AltClient/version-...` folder beside
   `RAMSettings.ini`. Check the log for the client build or fallback reason.
   Verify low textures, gray sky, muted sound and the configured FPS in-game.
3. Launch three alts, then the main. Repeat with **AsyncJoin** enabled.
   Each account should match its own process. The main should retain its own
   graphics, FPS cap and volume.
4. From an elevated PowerShell if necessary, inspect client paths and priority:

   ```powershell
   Get-Process RobloxPlayerBeta | Select-Object Id, Path, PriorityClass
   ```

   Dedicated alts should run from `AltClient`; the main should run from the
   protocol handler's client folder (Bloxstrap Versions, or stock Roblox).
   Background alts should become BelowNormal with efficiency mode enabled.
   Unknown, protected helper and focused processes are excluded.
5. Recheck the Bloxstrap flags hash. With all alts using the dedicated path, it
   should match. Shared fallback temporarily patches and restores that file.
6. Close the alts. Relaunch the main through RAM. Its captured settings should
   still apply. Create `FastFlags/Default.json` and repeat: the main should still
   receive no profile.
7. Verify a client file's hard links (substitute the actual built version):

   ```powershell
   fsutil hardlink list '.\AltClient\version-...\RobloxPlayerBeta.exe'
   ```

   Both source and alt paths should appear. A different disk/filesystem can use
   copies instead; the log reports this. `AppSettings.xml` is always copied, and
   `ClientSettings` is always independent.

## Fallback and recovery

- Turn **Use dedicated low-graphics alt client** off and launch an alt. Confirm a
  fallback reason in the log and restoration of the shared flags after settling.
- In a debugger, set `AccountManager.CurrentVersion` to `version-uninstalled` and
  launch an alt with the dedicated setting on. Expect a version mismatch fallback.
  Bloxstrap can update the installed client for a later launch.
- While a fallback launch has `FastFlags/.backup.json`, end RAM, then restart it.
  Expect the original shared flags and main snapshot to be restored. Use a test
  session for this interruption test.
- Test stock Roblox without Bloxstrap on a separate installation. Main should
  keep using the registered protocol handler, and alts should find the matching
  stock version. Unreadable process paths conservatively prevent stale cleanup.

If a dedicated launch fails to produce a matched client in ten seconds, RAM
reports the failure and keeps subsequent alt launches on protocol fallback for
that session. Check the log and client error before trying direct launches again.

## Repeatable fixture checks

From the repository root, in Windows PowerShell:

```powershell
dotnet msbuild 'RBX Alt Manager.sln' /p:Configuration=Release '/p:Platform=Any CPU' /restore
powershell -NoProfile -ExecutionPolicy Bypass -File tests/client-profiles.ps1
```

The script tests the built assembly with temporary files. It does not start
Roblox or change installed client settings. It covers role/profile precedence,
tracker formats, XML values, main snapshot capture and alt-value recognition,
fallback merge/recovery, hard links, clone isolation, cleanup boundaries, and
argument adaptation without retaining the main's ticket or join URL.

## Implementation verification report

- Release build: passed with zero errors. Six existing warnings remain: two
  unawaited calls in AccountManager and four unassigned AccountBrowser fields.
- Fixture checks: **35 passed**. Generated FastFlags README matches the checked-in
  README.
- Live matrix scenarios 2–9: **pending in-game testing**. Fixture tests cover
  portions of clone isolation, profile exclusion and fallback recovery; they do
  not establish game acceptance, graphics, process matching or crash recovery
  during a real launch.
- Bloxstrap command-line format: **not yet verified on a main game launch**.
  Inspection found a stock Roblox process without a raw protocol URI or the
  expected `--app` shape. The implementation observes a main launched through RAM
  and mirrors a raw URI or the supported `--app -t ... -j ... -b ...
  --launchtime=...` shape. Unknown shapes remain on fallback.
- Whether Roblox saves XML on exit versus settings change: **not measured**.
  Main snapshots and delayed exit capture handle the planned writeback cases.
- FramerateCap 15 and 20: **not measured**. The preset retains 30; no new FastFlags
  were added.
- `UseOldJoin` remains broken as documented in the plan: it prepares arguments but
  never calls `Process.Start`. It was not repaired.
- Existing unrelated working files were preserved. No commit was made.
