using Microsoft.Win32;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace RBX_Alt_Manager.Classes
{
    public static class ClientSettingsPatcher
    {
        public static string FastFlagsDirectory => Path.Combine(Environment.CurrentDirectory, "FastFlags");

        public static List<string> GetClientSettingsDirectories()
        {
            var directories = new List<string>();

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

            // 1. Bloxstrap Modifications ClientSettings
            string bloxstrapDir = Path.Combine(localAppData, "Bloxstrap");
            if (Directory.Exists(bloxstrapDir))
            {
                directories.Add(Path.Combine(bloxstrapDir, "Modifications", "ClientSettings"));

                // 2. Bloxstrap Versions ClientSettings
                string bloxstrapVersions = Path.Combine(bloxstrapDir, "Versions");
                if (Directory.Exists(bloxstrapVersions))
                {
                    try
                    {
                        foreach (string vDir in Directory.GetDirectories(bloxstrapVersions, "version-*"))
                        {
                            directories.Add(Path.Combine(vDir, "ClientSettings"));
                        }
                    }
                    catch { }
                }
            }

            // 3. Roblox AppData Versions
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

            // 4. Roblox Program Files Versions
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

            // 5. Registry (roblox\DefaultIcon) fallback
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

        public static void OpenFastFlagsFolder()
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
                    File.WriteAllText(readmePath,
                        "Roblox Account Manager - Group FastFlags\r\n" +
                        "========================================\r\n" +
                        "Place JSON files in this folder named after your Account Groups.\r\n" +
                        "When an account launches, RAM will automatically apply the matching FastFlags to Roblox & Bloxstrap!\r\n\r\n" +
                        "Matching order for an account in group '01 Alts':\r\n" +
                        "  1. 01 Alts.json   (Exact group name match)\r\n" +
                        "  2. Alts.json      (Name with leading numbers stripped)\r\n" +
                        "  3. Default.json   (Fallback for any account)\r\n\r\n" +
                        "Example JSON content (standard ClientAppSettings.json format):\r\n" +
                        "{\r\n" +
                        "  \"FFlagHandleAltEnterFullscreenManually\": \"False\",\r\n" +
                        "  \"DFIntTextureQualityOverride\": \"0\"\r\n" +
                        "}\r\n");
                }
                catch { }
            }

            try
            {
                Process.Start("explorer.exe", FastFlagsDirectory);
            }
            catch (Exception ex)
            {
                Program.Logger.Error($"Failed to open FastFlags folder: {ex.Message}");
            }
        }

        public static void PatchSettings(Account account = null)
        {
            var targetDirs = GetClientSettingsDirectories();
            if (targetDirs.Count == 0)
            {
                Program.Logger.Error("Can't patch ClientAppSettings, no Roblox or Bloxstrap ClientSettings directories found");
                return;
            }

            bool useGroupFastFlags = AccountManager.General.Get<bool>("GroupFastFlags");

            if (useGroupFastFlags && account != null)
            {
                if (!Directory.Exists(FastFlagsDirectory))
                {
                    try { Directory.CreateDirectory(FastFlagsDirectory); } catch { }
                }

                string group = account.Group?.Trim() ?? "Default";
                if (string.IsNullOrEmpty(group)) group = "Default";

                string exactPath = Path.Combine(FastFlagsDirectory, $"{group}.json");

                Match match = Regex.Match(group, @"^\d{1,3}\s?");
                string cleanedGroup = match.Success ? group.Substring(match.Length).Trim() : string.Empty;
                string cleanedPath = (!string.IsNullOrEmpty(cleanedGroup) && cleanedGroup != group)
                    ? Path.Combine(FastFlagsDirectory, $"{cleanedGroup}.json")
                    : null;

                string defaultPath = Path.Combine(FastFlagsDirectory, "Default.json");

                string selectedJsonPath = null;
                if (File.Exists(exactPath))
                    selectedJsonPath = exactPath;
                else if (cleanedPath != null && File.Exists(cleanedPath))
                    selectedJsonPath = cleanedPath;
                else if (File.Exists(defaultPath))
                    selectedJsonPath = defaultPath;

                if (!string.IsNullOrEmpty(selectedJsonPath) && File.Exists(selectedJsonPath))
                {
                    try
                    {
                        string content = File.ReadAllText(selectedJsonPath);
                        if (content.TryParseJson(out JObject settings))
                        {
                            if (AccountManager.General.Get<bool>("UnlockFPS") && settings["DFIntTaskSchedulerTargetFps"] == null)
                            {
                                int maxFps = AccountManager.General.Exists("MaxFPSValue") ? AccountManager.General.Get<int>("MaxFPSValue") : 240;
                                settings["DFIntTaskSchedulerTargetFps"] = maxFps;
                            }

                            content = settings.ToString(Newtonsoft.Json.Formatting.None);
                        }

                        foreach (string dir in targetDirs)
                        {
                            try
                            {
                                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                                string targetFile = Path.Combine(dir, "ClientAppSettings.json");
                                File.WriteAllText(targetFile, content);
                                Program.Logger.Info($"[FastFlags] Applied '{Path.GetFileName(selectedJsonPath)}' for account {account.Username} ({account.Group}) to {targetFile}");
                            }
                            catch (Exception ex)
                            {
                                Program.Logger.Error($"[FastFlags] Failed writing to {dir}: {ex.Message}");
                            }
                        }

                        return;
                    }
                    catch (Exception ex)
                    {
                        Program.Logger.Error($"[FastFlags] Error loading {selectedJsonPath}: {ex.Message}");
                    }
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
                            File.WriteAllText(settingsFN, settings.ToString(Newtonsoft.Json.Formatting.None));
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
    }
}