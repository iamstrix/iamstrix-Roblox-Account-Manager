Roblox Account Manager - Client Profiles
=======================================
Put your main account in group Main (or 01 Main). Change the main group in
Settings > Miscellaneous > Client Profiles. The main never uses a group
profile, including Default.json, and always launches through your protocol handler.

Put alts in Alts and click Create Low Gfx Preset, which enables Group-Based
FastFlags. Profile matching for 01 Alts is: 01 Alts.json, Alts.json, Default.json.
Other accounts without a matching profile are Normal and retain legacy settings.

Enable Use dedicated low-graphics alt client in Client Profiles. Launch the main
through RAM once each session to verify its argument format. Until RAM observes
a raw roblox-player URI or the supported --app/-t/-j/-b switch shape, alts use
protocol fallback. Unsupported argument formats
also stay on fallback. Status and the log explain why.

RAM builds AltClient\<version-guid> beside RAMSettings.ini from an installed
client matching the live Roblox version. Files use hard links when possible,
otherwise copies. AppSettings.xml and ClientSettings are separate copies.
Dedicated alts receive only their profile flags; your Bloxstrap flags stay untouched.
When no matching client is installed, protocol fallback lets Bloxstrap update Roblox
and temporarily merges the profile into shared flags, with crash-safe backup/restore.

Before first use, close Roblox and click Clean Bloxstrap flags in Client Profiles
if older RAM builds left alt flags there. Review the listed keys. This saves
FastFlags\bloxstrap-flags.bak.json and removes only the confirmed alt keys.
Keep this backup safe; move it before performing another cleanup.

Set your main's desired graphics, FPS cap and volume in Roblox, close the main,
then click Capture main's in-game settings now. FastFlags\Main.settings.json
stores SavedQualityLevel, GraphicsQualityLevel, FramerateCap and MasterVolume.
Add other existing XML property names to this JSON to include them in captures.
RAM also captures after a known main exits, unless the values match an alt profile.
The first alt launch captures settings if no snapshot exists, so capture your main
manually first if the current shared settings were left by an alt.

_RobloxSettings in a profile sets shared GlobalBasicSettings XML values at launch.
Launches are serialized until the client has run for 8 seconds (30 second timeout).
Then RAM restores the main snapshot, including on startup and shutdown.
Roblox clients can still write this shared file when changing settings or exiting.

Optional alt efficiency mode uses BelowNormal priority and EcoQoS where supported.
The focused window and unknown processes are excluded. Optional window width and
height (both nonzero, e.g. 800 by 600) apply when no remembered position exists.

Only allowlisted FastFlags work; the preset is unchanged:
https://devforum.roblox.com/t/allowlist-for-local-client-configuration-via-fast-flags/3966569
DFIntTaskSchedulerTargetFps is ignored by Roblox. Use _RobloxSettings.FramerateCap
instead; the preset keeps 30 FPS until lower values are verified in-game.

Example Alts.json:
{
  "DFFlagTextureQualityOverrideEnabled": "True",
  "DFIntTextureQualityOverride": "0",
  "_RobloxSettings": { "SavedQualityLevel": 1, "GraphicsQualityLevel": 1, "FramerateCap": 30, "MasterVolume": 0 }
}
