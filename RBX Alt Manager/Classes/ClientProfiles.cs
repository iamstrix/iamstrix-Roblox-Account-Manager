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
using System.Threading.Tasks;
using System.Web;
using System.Xml.Linq;

namespace RBX_Alt_Manager.Classes
{
    public enum ClientRole { Main, Alt, Normal }

    public static class ClientProfiles
    {
        public static readonly SemaphoreSlim LaunchGate = new SemaphoreSlim(1, 1);
        private static readonly object SnapshotLock = new object();
        private static readonly string[] MainKeys = { "SavedQualityLevel", "GraphicsQualityLevel", "FramerateCap", "MasterVolume" };
        public static string MainGroup => AccountManager.General.Exists("MainGroup") ? AccountManager.General.Get<string>("MainGroup") : "Main";
        public static string SnapshotPath => Path.Combine(ClientSettingsPatcher.FastFlagsDirectory, "Main.settings.json");
        public static string NormalizeGroup(string group) => Regex.Replace((group ?? "").Trim(), @"^\d{1,3}\s?", "").Trim();
        public static bool IsMain(Account account) => account != null && string.Equals(NormalizeGroup(account.Group), NormalizeGroup(MainGroup), StringComparison.OrdinalIgnoreCase);
        public static ClientRole GetRole(Account account) => IsMain(account) ? ClientRole.Main : account != null && AccountManager.General.Get<bool>("GroupFastFlags") && ClientSettingsPatcher.FindGroupProfile(account) != null ? ClientRole.Alt : ClientRole.Normal;
        internal static bool IsVersionGuid(string version) => version != null && Regex.IsMatch(version, @"^version-[a-zA-Z0-9]+$");

        public static string FindClientSource(string wantedVersion)
        {
            wantedVersion = wantedVersion ?? Bloxstrap.GetVersionGuid();
            if (!IsVersionGuid(wantedVersion)) return null;
            var roots = new List<string>();
            if (Bloxstrap.Directory != null) roots.Add(Path.Combine(Bloxstrap.Directory, "Versions"));
            roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "Versions"));
            roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Roblox", "Versions"));
            return roots.Select(root => Path.Combine(root, wantedVersion)).FirstOrDefault(path => File.Exists(Path.Combine(path, "RobloxPlayerBeta.exe")));
        }

        public static JObject ReadProfile(Account account)
        {
            string path = ClientSettingsPatcher.FindGroupProfile(account);
            if (path == null) return null;
            if (!File.ReadAllText(path).TryParseJson(out JObject profile) || profile == null) throw new InvalidDataException("Invalid profile JSON: " + path);
            return profile;
        }

        public static JObject CaptureMainSnapshot(bool automatic = false)
        {
            lock (SnapshotLock)
            {
                string path = ClientSettingsPatcher.GetGlobalBasicSettingsPath();
                if (path == null) throw new FileNotFoundException("Launch Roblox and save its in-game settings first.");
                JObject previous = LoadSnapshot();
                JObject values = ReadMainSettings(path, previous);
                if (!values.HasValues) throw new InvalidDataException("No main settings found in " + path);
                if (automatic && MatchesAltSettings(values))
                {
                    Program.Logger.Info("[FastFlags] Skipped automatic main snapshot: alt settings were written last");
                    return previous;
                }
                Directory.CreateDirectory(ClientSettingsPatcher.FastFlagsDirectory);
                string temporary = SnapshotPath + ".tmp";
                File.WriteAllText(temporary, values.ToString(Formatting.Indented));
                if (File.Exists(SnapshotPath)) File.Replace(temporary, SnapshotPath, null);
                else File.Move(temporary, SnapshotPath);
                Program.Logger.Info("[FastFlags] Captured main in-game settings");
                return values;
            }
        }

        internal static JObject ReadMainSettings(string path, JObject previous)
        {
            var keys = MainKeys.Concat(previous?.Properties().Select(p => p.Name) ?? Enumerable.Empty<string>()).Distinct();
            XDocument document = XDocument.Load(path);
            JObject values = new JObject();
            foreach (string key in keys)
            {
                XElement element = document.Descendants().FirstOrDefault(x => x.Parent?.Name.LocalName == "Properties" && (string)x.Attribute("name") == key);
                if (element != null) values[key] = element.Value;
            }
            return values;
        }

        private static bool MatchesAltSettings(JObject values)
        {
            foreach (var account in AccountManager.AccountsList.Where(a => GetRole(a) == ClientRole.Alt))
            {
                JObject settings = ReadProfile(account)?[ClientSettingsPatcher.RobloxSettingsKey] as JObject;
                if (settings != null && settings.HasValues && settings.Properties().All(p => values[p.Name] != null && Equivalent(values[p.Name], p.Value))) return true;
            }
            return false;
        }

        private static bool Equivalent(JToken a, JToken b)
        {
            if (decimal.TryParse(a.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal x) && decimal.TryParse(b.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal y)) return x == y;
            return string.Equals(a.ToString(), b.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        private static JObject LoadSnapshot()
        {
            if (!File.Exists(SnapshotPath)) return null;
            if (!File.ReadAllText(SnapshotPath).TryParseJson(out JObject values) || values == null) throw new InvalidDataException("Invalid main snapshot: " + SnapshotPath);
            return values;
        }

        public static void RestoreMainSnapshot()
        {
            lock (SnapshotLock)
            {
                JObject values = LoadSnapshot();
                string path = ClientSettingsPatcher.GetGlobalBasicSettingsPath();
                if (values != null && path != null) ClientSettingsPatcher.ApplyGlobalBasicSettings(path, values);
            }
        }

        public static void ApplyAltSettings(Account account)
        {
            lock (SnapshotLock)
            {
                JObject values = ReadProfile(account)?[ClientSettingsPatcher.RobloxSettingsKey] as JObject;
                if (values == null || !values.HasValues) return;
                if (!File.Exists(SnapshotPath)) CaptureMainSnapshot();
                string path = ClientSettingsPatcher.GetGlobalBasicSettingsPath();
                if (path != null) ClientSettingsPatcher.ApplyGlobalBasicSettings(path, values);
            }
        }

        // Observe the actual main process before allowing direct launches; never persist tickets or URLs.
        public static string VerifiedArgumentFormat { get; private set; }
        public static bool DirectLaunchFailed { get; private set; }
        private static string ArgumentTemplate;
        public static void ObserveMainArguments(Process process)
        {
            ObserveMainCommandLine(process.GetCommandLine());
        }

        internal static void ObserveMainCommandLine(string command)
        {
            if (DirectLaunchFailed) return;
            if (command == null) return;
            string observedFormat = null;
            string template = null;
            if (command.IndexOf("roblox-player:1+", StringComparison.OrdinalIgnoreCase) >= 0) observedFormat = "URI";
            else
            {
                Match arguments = Regex.Match(command, "^\\s*(?:\"[^\"]+\"|\\S+)\\s+(?<args>.*)$");
                if (arguments.Success)
                {
                    template = arguments.Groups["args"].Value;
                    foreach (var item in new[] { new[] { "-t", "$RAM_TICKET" }, new[] { "-j", "$RAM_URL" }, new[] { "-b", "$RAM_TRACKER" } })
                    {
                        string pattern = "(?<prefix>(?:^|\\s)" + Regex.Escape(item[0]) + "\\s+)(?:\"[^\"]*\"|\\S+)";
                        template = Regex.Replace(template, pattern, match => match.Groups["prefix"].Value + "\"" + item[1] + "\"");
                    }
                    template = Regex.Replace(template, @"(?<prefix>(?:^|\s)--launchtime[= ])\d+", "${prefix}$RAM_TIME");
                    // Support the documented switch shape only; unfamiliar launchers keep protocol fallback.
                    string remainder = Regex.Replace(template, "(?:^|\\s)(?:--app|-t\\s+\"\\$RAM_TICKET\"|-j\\s+\"\\$RAM_URL\"|-b\\s+\"\\$RAM_TRACKER\"|--launchtime[= ]\\$RAM_TIME)(?=\\s|$)", "");
                    if (string.IsNullOrWhiteSpace(remainder) && template.Contains("$RAM_TICKET") && template.Contains("$RAM_URL") && template.Contains("$RAM_TRACKER")) observedFormat = "Switches";
                }
            }
            VerifiedArgumentFormat = observedFormat;
            ArgumentTemplate = observedFormat == "Switches" ? template : null;
            if (VerifiedArgumentFormat != null) Program.Logger.Info("[FastFlags] Verified main client arguments: " + VerifiedArgumentFormat);
            else Program.Logger.Info("[FastFlags] Main uses an unverified switch argument format; keeping protocol fallback");
        }

        internal static string GetDirectArguments(string launchUri)
        {
            if (VerifiedArgumentFormat == "URI") return launchUri;
            if (VerifiedArgumentFormat != "Switches" || ArgumentTemplate == null) throw new InvalidOperationException("Direct arguments have not been verified");
            var fields = launchUri.Split('+').Skip(1).Select(part => part.Split(new[] { ':' }, 2)).Where(pair => pair.Length == 2).ToDictionary(pair => pair[0], pair => pair[1], StringComparer.OrdinalIgnoreCase);
            string ticket = fields["gameinfo"];
            string url = HttpUtility.UrlDecode(fields["placelauncherurl"]);
            if (ticket.Contains("\"") || url.Contains("\"")) throw new InvalidDataException("Invalid direct launch argument");
            return ArgumentTemplate.Replace("$RAM_TICKET", ticket).Replace("$RAM_URL", url).Replace("$RAM_TRACKER", fields["browsertrackerid"]).Replace("$RAM_TIME", fields["launchtime"]);
        }

        public static async Task LaunchAsync(Account account, string launchUri)
        {
            await LaunchGate.WaitAsync();
            bool altSettings = false;
            Task settle = null;
            try
            {
                ClientRole role = GetRole(account);
                ClientSettingsPatcher.RestoreOriginals();
                if (File.Exists(Path.Combine(ClientSettingsPatcher.FastFlagsDirectory, ".backup.json"))) throw new IOException("Shared flags could not be restored. Resolve the logged restore error before launching.");
                if (role == ClientRole.Main) RestoreMainSnapshot();
                string executable = null;
                if (role == ClientRole.Alt)
                {
                    bool dedicated = await Task.Run(() =>
                    {
                        bool result = AltClient.TryPrepare(account, out string path, out string reason);
                        executable = path;
                        if (!result) Program.Logger.Info("[FastFlags] Protocol fallback: " + reason);
                        return result;
                    });
                    ApplyAltSettings(account);
                    altSettings = true;
                    if (!dedicated) ClientSettingsPatcher.PatchSettings(account);
                }
                else if (role == ClientRole.Normal) ClientSettingsPatcher.PatchSettings(account);

                account.LastLaunchTime = DateTime.Now;
                DateTime started = account.LastLaunchTime;
                // Mirror the URI or supported switches actually observed on the main process.
                var info = executable == null ? new ProcessStartInfo(launchUri) { UseShellExecute = true } : new ProcessStartInfo(executable, GetDirectArguments(launchUri)) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable) };
                Process launcher;
                try { launcher = Process.Start(info); }
                catch
                {
                    if (executable != null) DirectLaunchFailed = true;
                    throw;
                }
                Task<Process> found = FindLaunchedProcess(account, started, 10);
                settle = SettleLaunchAsync(found, started, role);
                try
                {
                    if (executable != null)
                    {
                        if (await found == null)
                        {
                            DirectLaunchFailed = true;
                            throw new InvalidOperationException("Dedicated client was not detected within 10 seconds. Direct launches are disabled for this session; the next alt launch will use protocol fallback.");
                        }
                    }
                    else if (launcher != null) await Task.WhenAny(Task.Run(() => { try { launcher.WaitForExit(); } catch { } }), settle);
                    ClientSettingsPatcher.NotifyLaunchFinished();
                    AccountManager.Instance.NextAccount();
                    account.AdjustWindowPosition();
                }
                finally
                {
                    launcher?.Dispose();
                }
            }
            finally
            {
                try
                {
                    if (settle != null) await settle;
                }
                finally
                {
                    try
                    {
                        ClientSettingsPatcher.RestoreOriginals();
                        if (altSettings) RestoreMainSnapshot();
                    }
                    finally { LaunchGate.Release(); }
                }
            }
        }

        private static async Task SettleLaunchAsync(Task<Process> found, DateTime started, ClientRole role)
        {
            using (Process process = await found)
            {
                double remaining = 30000 - (DateTime.Now - started).TotalMilliseconds;
                if (process != null)
                {
                    try
                    {
                        if (role == ClientRole.Main) ObserveMainArguments(process);
                        remaining = Math.Min(remaining, Math.Max(0, 8000 - (DateTime.Now - process.StartTime).TotalMilliseconds));
                    }
                    catch { }
                }
                if (remaining > 0) await Task.Delay((int)remaining);
            }
        }

        internal static async Task<Process> FindLaunchedProcess(Account account, DateTime started, int timeoutSeconds)
        {
            DateTime end = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            do
            {
                foreach (var process in Process.GetProcessesByName("RobloxPlayerBeta"))
                {
                    try
                    {
                        if (!process.HasExited && process.StartTime >= started.AddSeconds(-1) && Utilities.GetBrowserTrackerId(process.GetCommandLine()) == account.BrowserTrackerID)
                        {
                            account.ProcessId = process.Id;
                            return process;
                        }
                    }
                    catch { }
                    process.Dispose();
                }
                await Task.Delay(250);
            } while (DateTime.UtcNow < end);
            return null;
        }
    }
}
