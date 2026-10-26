using Microsoft.Win32;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Text.RegularExpressions;

namespace RBX_Alt_Manager.Classes
{
    public static class Bloxstrap
    {
        public static string Executable
        {
            get
            {
                try
                {
                    using (var key = Registry.ClassesRoot.OpenSubKey(@"roblox-player\shell\open\command"))
                    {
                        string command = Environment.ExpandEnvironmentVariables(key?.GetValue("") as string ?? "");
                        Match match = Regex.Match(command, "^\\s*\"([^\"]+)\"|^\\s*(\\S+)");
                        string path = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                        if (string.IsNullOrEmpty(path) || Path.GetFileName(path).StartsWith("RobloxPlayer", StringComparison.OrdinalIgnoreCase)) return null;
                        return System.IO.Directory.Exists(Path.Combine(Path.GetDirectoryName(path), "Modifications")) ? path : null;
                    }
                }
                catch { return null; }
            }
        }

        public static string Directory => Executable == null ? null : Path.GetDirectoryName(Executable);
        public static bool IsInstalled => Directory != null;
        public static string ModificationsClientSettings => Directory == null ? null : Path.Combine(Directory, "Modifications", "ClientSettings");

        public static string GetVersionGuid()
        {
            string directory = Directory;
            if (directory == null) return null;
            foreach (string name in new[] { "PlayerState.json", "State.json" })
            {
                try
                {
                    string path = Path.Combine(directory, name);
                    if (File.Exists(path) && File.ReadAllText(path).TryParseJson(out JObject state))
                    {
                        string guid = (string)(state["VersionGuid"] ?? state.SelectToken("Player.VersionGuid"));
                        if (ClientProfiles.IsVersionGuid(guid)) return guid;
                    }
                }
                catch { }
            }
            return null;
        }

        public static string GetClientDirectory()
        {
            string guid = GetVersionGuid();
            if (Directory == null || guid == null) return null;
            string path = Path.Combine(Directory, "Versions", guid);
            return File.Exists(Path.Combine(path, "RobloxPlayerBeta.exe")) ? path : null;
        }
    }
}
