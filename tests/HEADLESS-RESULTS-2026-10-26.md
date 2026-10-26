# Client Profiles headless verification — October 26, 2026

Tested the current uncommitted working tree on Windows, using .NET SDK
10.0.101 / MSBuild 18.0.6. No Roblox launch or installed client settings change
was performed.

## Results

| Check | Result |
| --- | --- |
| Release build with restore | Passed |
| Full Release rebuild | Passed: zero errors, six previously documented warnings |
| Fixture suite against the rebuilt executable | 35 passed |
| Embedded FastFlags README vs checked-in README | Matched after normalizing line endings |
| Four new source files registered in the project | Passed |

The rebuilt application is `build/Release/Roblox Account Manager.exe`.

The six warnings are CS4014 in `AccountManager.cs` at lines 1016 and 1030,
and CS0649 for four `BrowserConfig` fields in `AccountBrowser.cs`.

## What the fixture suite establishes

- Main group normalization and exclusion from group profiles, including
  `Default.json`; exact profile names take precedence over normalized names.
- Unknown accounts remain Normal, and disabling Group-Based FastFlags removes
  the Alt role.
- Tracker parsing supports switch, protocol URI, and URL forms.
- Unverified direct arguments keep the dedicated client on protocol fallback.
- XML settings preserve unrelated values, skip missing properties, and serialize
  numeric and boolean values consistently across cultures.
- Snapshot extraction includes configured extra properties and recognizes alt
  settings even when XML and JSON use different value types.
- Shared fallback merges flags, excludes reserved settings, restores original
  file contents, and retains unreadable recovery data.
- Clone fixtures exclude the source ClientSettings directory, copy
  AppSettings.xml independently, and hard-link ordinary files on this filesystem.
- Cleanup rejects paths outside the owned AltClient directory.
- Synthetic launch arguments support raw URI and recognized switch formats,
  replace launch-specific values, discard the main ticket and join URL from the
  template, and reject unknown formats.

## Not verified

Installed Bloxstrap/Roblox discovery could not be established in the sandbox:
AppData access was denied. The requested read-only check outside the sandbox
was interrupted before a result returned. Empty detection results from the
sandbox are not evidence that either application is absent.

Live game acceptance, the actual Bloxstrap launch argument format, account to
process matching during real joins, concurrent launches, graphics, FPS, volume,
BelowNormal/EcoQoS behavior on real alt processes, crash recovery during a real
launch, and settings writeback on game exit remain unverified. The fixture suite
does not substitute for those in-game scenarios.

## Repeat

Run from the repository root:

```powershell
dotnet msbuild 'RBX Alt Manager.sln' /p:Configuration=Release '/p:Platform=Any CPU' /restore /nologo /verbosity:minimal
dotnet msbuild 'RBX Alt Manager.sln' /t:Rebuild /p:Configuration=Release '/p:Platform=Any CPU' /nologo /verbosity:minimal
powershell -NoProfile -ExecutionPolicy Bypass -File tests/client-profiles.ps1
```

The fixture script uses temporary files under `tests` and removes its fixture
directory on completion. No implementation changes or commits were made during
this verification pass.
