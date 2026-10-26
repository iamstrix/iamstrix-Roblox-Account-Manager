using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml.Linq;

namespace RBX_Alt_Manager.Classes
{
    public static class ClientSettingsPatcher
    {
        public static string FastFlagsDirectory => Path.Combine(Environment.CurrentDirectory, "FastFlags");

        // Holds the user's original ClientAppSettings.json / GlobalBasicSettings files while a group profile is applied
        private static string BackupPath => Path.Combine(FastFlagsDirectory, ".backup.json");

        // Reserved key inside a group JSON for GlobalBasicSettings values, stripped before writing FastFlags
        public const string RobloxSettingsKey = "_RobloxSettings";

        private const int RestoreAfterLaunchMs = 15000;
        private const int RestoreFallbackMs = 90000;

        private static readonly object PatchLock = new object();
        private static Timer RestoreTimer;

        // Roblox only honors allowlisted FastFlags (https://devforum.roblox.com/t/3966569), so the preset sticks to those
        internal static readonly JObject LowGraphicsPreset = new JObject
        {
            ["DFFlagTextureQualityOverrideEnabled"] = "True",
            ["DFIntTextureQualityOverride"] = "0",
            ["DFIntDebugFRMQualityLevelOverride"] = "1",
            ["FIntDebugForceMSAASamples"] = "0",
            ["FIntFRMMinGrassDistance"] = "0",
            ["FIntFRMMaxGrassDistance"] = "0",
            ["FIntGrassMovementReducedMotionFactor"] = "0",
            ["DFIntCSGLevelOfDetailSwitchingDistance"] = "0",
            ["DFIntCSGLevelOfDetailSwitchingDistanceL12"] = "0",
            ["DFIntCSGLevelOfDetailSwitchingDistanceL23"] = "0",
            ["DFIntCSGLevelOfDetailSwitchingDistanceL34"] = "0",
            ["DFFlagDebugPauseVoxelizer"] = "True",
            ["FFlagDebugSkyGray"] = "True",
            ["DFFlagDisableDPIScale"] = "True",
            [RobloxSettingsKey] = new JObject
            {
                ["SavedQualityLevel"] = 1,
                ["GraphicsQualityLevel"] = 1,
                ["FramerateCap"] = 30,
                ["MasterVolume"] = 0,
            }
        };

        /// <summary>
        /// Returns the folder of the bootstrapper (Bloxstrap, Fishstrap, Voidstrap, ...) registered as the roblox-player handler, or null if Roblox handles it directly
        /// </summary>
        public static string GetBootstrapperDirectory() => Bloxstrap.Directory;

        public static List<string> GetClientSettingsDirectories()
        {
            // Bootstrappers copy Modifications into the version folder on every launch, so that's the only place worth writing
            string bootstrapper = GetBootstrapperDirectory();
            if (bootstrapper != null)
                return new List<string> { Path.Combine(bootstrapper, "Modifications", "ClientSettings") };

            var directories = new List<string>();

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

            // 1. Roblox AppData Versions
            string robloxDir = Path.Combine(localAppData, "Roblox", "Versions");
            if (Directory.Exists(robloxDir))
            {
                try
                {
                    foreach (string vDir in Directory.GetDirectories(robloxDir, "version-*"))
                    {
                        if (File.Exists(Path.Combine(vDir, "RobloxPlayerBeta.exe")) || File.Exists(Path.Combine(vDir, "RobloxPlayerLauncher.exe")))
                        {
                            directories.Add(Path.Combine(vDir, "ClientSettings"));
                        }
                    }
                }
                catch { }
            }

            // 2. Roblox Program Files Versions
            foreach (string pf in new[] { programFilesX86, programFiles })
            {
                if (string.IsNullOrEmpty(pf)) continue;
                string pfRobloxVersions = Path.Combine(pf, "Roblox", "Versions");
                if (Directory.Exists(pfRobloxVersions))
                {
                    try
                    {
                        foreach (string vDir in Directory.GetDirectories(pfRobloxVersions, "version-*"))
                        {
                            if (File.Exists(Path.Combine(vDir, "RobloxPlayerBeta.exe")) || File.Exists(Path.Combine(vDir, "RobloxPlayerLauncher.exe")))
                            {
                                directories.Add(Path.Combine(vDir, "ClientSettings"));
                            }
                        }
                    }
                    catch { }
                }
            }

            // 3. Registry (roblox\DefaultIcon) fallback
            try
            {
                object registryValue = Registry.ClassesRoot.OpenSubKey(@"roblox\DefaultIcon")?.GetValue("");
                if (registryValue is string robloxPath && !string.IsNullOrEmpty(robloxPath))
                {
                    var parent = Directory.GetParent(robloxPath);
                    if (parent != null && parent.Exists && parent.Name.StartsWith("version-"))
                    {
                        directories.Add(Path.Combine(parent.FullName, "ClientSettings"));
                    }
                }
            }
            catch { }

            return directories.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static string GetGlobalBasicSettingsPath()
        {
            try
            {
                string robloxDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox");
                if (!Directory.Exists(robloxDir)) return null;

                return Directory.GetFiles(robloxDir, "GlobalBasicSettings_*.xml")
                    .Where(f => !Path.GetFileNameWithoutExtension(f).EndsWith("_Studio", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(f => int.TryParse(Regex.Match(Path.GetFileName(f), @"_(\d+)").Groups[1].Value, out int v) ? v : 0)
                    .FirstOrDefault();
            }
            catch { return null; }
        }

        public static void OpenFastFlagsFolder()
        {
            EnsureFastFlagsDirectory();

            try
            {
                Process.Start("explorer.exe", FastFlagsDirectory);
            }
            catch (Exception ex)
            {
                Program.Logger.Error($"Failed to open FastFlags folder: {ex.Message}");
            }
        }

        /// <summary>
        /// Writes the low graphics preset to FastFlags\{name}.json. Returns the file path.
        /// </summary>
        public static string CreateLowGraphicsPreset(string name = "Alts")
        {
            EnsureFastFlagsDirectory();

            string path = Path.Combine(FastFlagsDirectory, $"{name}.json");
            File.WriteAllText(path, LowGraphicsPreset.ToString(Formatting.Indented));

            return path;
        }

        private static void EnsureFastFlagsDirectory()
        {
            if (!Directory.Exists(FastFlagsDirectory))
            {
                try { Directory.CreateDirectory(FastFlagsDirectory); } catch { }
            }

            string readmePath = Path.Combine(FastFlagsDirectory, "README.txt");
            if (!File.Exists(readmePath))
            {
                try
                {
                    File.WriteAllText(readmePath, @"Roblox Account Manager - Client Profiles
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
  ""DFFlagTextureQualityOverrideEnabled"": ""True"",
  ""DFIntTextureQualityOverride"": ""0"",
  ""_RobloxSettings"": { ""SavedQualityLevel"": 1, ""GraphicsQualityLevel"": 1, ""FramerateCap"": 30, ""MasterVolume"": 0 }
}
");
                }
                catch { }
            }
        }

        internal static string FindGroupProfile(Account account)
        {
            if (account == null || ClientProfiles.IsMain(account)) return null;
            string group = account.Group?.Trim() ?? "Default";
            if (string.IsNullOrEmpty(group)) group = "Default";

            string exactPath = Path.Combine(FastFlagsDirectory, $"{group}.json");

            Match match = Regex.Match(group, @"^\d{1,3}\s?");
            string cleanedGroup = match.Success ? group.Substring(match.Length).Trim() : string.Empty;
            string cleanedPath = (!string.IsNullOrEmpty(cleanedGroup) && cleanedGroup != group)
                ? Path.Combine(FastFlagsDirectory, $"{cleanedGroup}.json")
                : null;

            string defaultPath = Path.Combine(FastFlagsDirectory, "Default.json");

            if (File.Exists(exactPath)) return exactPath;
            if (cleanedPath != null && File.Exists(cleanedPath)) return cleanedPath;
            if (File.Exists(defaultPath)) return defaultPath;

            return null;
        }

        public static void PatchSettings(Account account = null)
        {
            if (ClientProfiles.IsMain(account)) { RestoreOriginals(); return; }
            var targetDirs = GetClientSettingsDirectories();
            if (targetDirs.Count == 0)
            {
                Program.Logger.Error("Can't patch ClientAppSettings, no Roblox or Bloxstrap ClientSettings directories found");
                return;
            }

            if (AccountManager.General.Get<bool>("GroupFastFlags") && account != null)
            {
                EnsureFastFlagsDirectory();

                string profilePath = FindGroupProfile(account);

                if (profilePath != null && File.ReadAllText(profilePath).TryParseJson(out JObject profile))
                {
                    lock (PatchLock)
                    {
                        try
                        {
                            ApplyProfile(account, profilePath, profile, targetDirs);
                            ScheduleRestore(RestoreFallbackMs);
                            return;
                        }
                        catch (Exception ex)
                        {
                            Program.Logger.Error($"[FastFlags] Error applying {profilePath}: {ex.Message}");
                        }
                    }
                }
                else
                {
                    if (profilePath != null)
                        Program.Logger.Error($"[FastFlags] {profilePath} is not valid JSON, skipping");

                    // No profile for this account (e.g. the main), make sure it gets the user's own flags
                    RestoreOriginals();
                }
            }

            // Fallback: Legacy CustomClientSettings or UnlockFPS
            string CustomFN = AccountManager.General.Exists("CustomClientSettings") ? AccountManager.General.Get<string>("CustomClientSettings") : string.Empty;
            if (!string.IsNullOrEmpty(CustomFN) && File.Exists(CustomFN))
            {
                foreach (string dir in targetDirs)
                {
                    try
                    {
                        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                        File.Copy(CustomFN, Path.Combine(dir, "ClientAppSettings.json"), true);
                    }
                    catch (Exception ex)
                    {
                        Program.Logger.Error($"Failed to copy CustomClientSettings to {dir}: {ex.Message}");
                    }
                }
            }
            else if (AccountManager.General.Get<bool>("UnlockFPS"))
            {
                int maxFps = AccountManager.General.Exists("MaxFPSValue") ? AccountManager.General.Get<int>("MaxFPSValue") : 240;
                foreach (string dir in targetDirs)
                {
                    try
                    {
                        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                        string settingsFN = Path.Combine(dir, "ClientAppSettings.json");
                        if (File.Exists(settingsFN) && File.ReadAllText(settingsFN).TryParseJson(out JObject settings))
                        {
                            settings["DFIntTaskSchedulerTargetFps"] = maxFps;
                            File.WriteAllText(settingsFN, settings.ToString(Formatting.None));
                        }
                        else
                        {
                            File.WriteAllText(settingsFN, $"{{\"DFIntTaskSchedulerTargetFps\":{maxFps}}}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Program.Logger.Error($"Failed to update FPS cap in {dir}: {ex.Message}");
                    }
                }
            }
        }

        private static void ApplyProfile(Account account, string profilePath, JObject profile, List<string> targetDirs)
        {
            var backup = LoadBackup();

            profile.Remove(RobloxSettingsKey);

            if (AccountManager.General.Get<bool>("UnlockFPS") && profile["DFIntTaskSchedulerTargetFps"] == null)
                profile["DFIntTaskSchedulerTargetFps"] = AccountManager.General.Exists("MaxFPSValue") ? AccountManager.General.Get<int>("MaxFPSValue") : 240;

            foreach (string dir in targetDirs)
            {
                string targetFile = Path.Combine(dir, "ClientAppSettings.json");

                try
                {
                    string original = BackupFile(backup, targetFile);
                    SaveBackup(backup); // Persist before touching the shared file, including on a crash.

                    // Merge on top of the user's own flags rather than replacing them
                    JObject merged = original != null && original.TryParseJson(out JObject userFlags) && userFlags != null ? userFlags : new JObject();
                    merged.Merge(profile, new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Replace });

                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllText(targetFile, merged.ToString(Formatting.Indented));

                    Program.Logger.Info($"[FastFlags] Applied '{Path.GetFileName(profilePath)}' for account {account.Username} ({account.Group}) to {targetFile}");
                }
                catch (Exception ex)
                {
                    Program.Logger.Error($"[FastFlags] Failed writing to {dir}: {ex.Message}");
                }
            }

            SaveBackup(backup);
        }

        internal static void ApplyGlobalBasicSettings(string path, JObject values)
        {
            XDocument doc = XDocument.Load(path);

            foreach (var pair in values)
            {
                XElement element = doc.Descendants().FirstOrDefault(x => x.Parent?.Name.LocalName == "Properties" && (string)x.Attribute("name") == pair.Key);

                if (element == null)
                {
                    Program.Logger.Error($"[FastFlags] '{pair.Key}' does not exist in {Path.GetFileName(path)}, skipping");
                    continue;
                }

                element.Value = pair.Value.Type == JTokenType.Float
                    ? ((double)pair.Value).ToString(CultureInfo.InvariantCulture)
                    : pair.Value.Type == JTokenType.Boolean ? ((bool)pair.Value ? "true" : "false") : pair.Value.ToString();
            }

            doc.Save(path);
        }

        /// <summary>
        /// Called once the Roblox launcher/bootstrapper has exited, giving the client time to read its settings before restoring the originals
        /// </summary>
        public static void NotifyLaunchFinished()
        {
            lock (PatchLock)
            {
                if (File.Exists(BackupPath))
                    ScheduleRestore(RestoreAfterLaunchMs);
            }
        }

        private static void ScheduleRestore(int delayMs)
        {
            if (RestoreTimer == null)
                RestoreTimer = new Timer(_ => RestoreWhenIdle(), null, delayMs, Timeout.Infinite);
            else
                RestoreTimer.Change(delayMs, Timeout.Infinite);
        }

        private static void RestoreWhenIdle()
        {
            if (!ClientProfiles.LaunchGate.Wait(0))
            {
                lock (PatchLock) if (File.Exists(BackupPath)) ScheduleRestore(1000);
                return;
            }
            try { RestoreOriginals(); }
            catch (Exception ex) { Program.Logger.Error("[FastFlags] Deferred restore failed: " + ex.Message); }
            finally { ClientProfiles.LaunchGate.Release(); }
        }

        /// <summary>
        /// Puts back the user's original ClientAppSettings.json / GlobalBasicSettings files if a group profile was applied
        /// </summary>
        public static void RestoreOriginals()
        {
            lock (PatchLock)
            {
                RestoreTimer?.Change(Timeout.Infinite, Timeout.Infinite);

                if (!File.Exists(BackupPath)) { RestoreSnapshotSafely(); return; }

                Dictionary<string, string> backup;
                try { backup = LoadBackup(); }
                catch { RestoreSnapshotSafely(); return; } // Keep unreadable recovery data intact.
                bool failed = false;

                foreach (var pair in backup)
                {
                    try
                    {
                        if (pair.Value == null)
                        {
                            if (File.Exists(pair.Key)) File.Delete(pair.Key);
                        }
                        else
                            File.WriteAllText(pair.Key, pair.Value);
                    }
                    catch (Exception ex)
                    {
                        failed = true;
                        Program.Logger.Error($"[FastFlags] Failed to restore {pair.Key}: {ex.Message}");
                    }
                }

                if (failed) return; // keep the backup around so the next restore can try again

                try { File.Delete(BackupPath); } catch { }

                RestoreSnapshotSafely();
                Program.Logger.Info($"[FastFlags] Restored {backup.Count} original settings file(s)");
            }
        }

        private static void RestoreSnapshotSafely()
        {
            try { ClientProfiles.RestoreMainSnapshot(); }
            catch (Exception ex) { Program.Logger.Error("[FastFlags] Main snapshot restore failed: " + ex.Message); }
        }

        // Records a file's content the first time it is touched; later patches keep the first (user's) copy
        private static string BackupFile(Dictionary<string, string> backup, string path)
        {
            if (!backup.ContainsKey(path))
                backup[path] = File.Exists(path) ? File.ReadAllText(path) : null;

            return backup[path];
        }

        private static Dictionary<string, string> LoadBackup()
        {
            try
            {
                if (File.Exists(BackupPath))
                    return JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(BackupPath)) ?? throw new InvalidDataException("Recovery backup is empty");
            }
            catch (Exception ex)
            {
                Program.Logger.Error($"[FastFlags] Failed to read {BackupPath}: {ex.Message}");
                throw new InvalidDataException("Recovery backup could not be read; it has been preserved", ex);
            }

            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        private static void SaveBackup(Dictionary<string, string> backup)
        {
            if (backup.Count == 0) return;

            string temporary = BackupPath + ".tmp";
            File.WriteAllText(temporary, JsonConvert.SerializeObject(backup, Formatting.Indented));
            if (File.Exists(BackupPath)) File.Replace(temporary, BackupPath, null);
            else File.Move(temporary, BackupPath);
        }
    }
}
