using Newtonsoft.Json;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace RBX_Alt_Manager.Classes
{
    public static class AltClient
    {
        private static readonly object BuildLock = new object();
        public static string Root => Path.Combine(Environment.CurrentDirectory, "AltClient");
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateHardLink(string file, string existing, IntPtr security);

        public static bool TryPrepare(Account account, out string exePath, out string reason)
        {
            lock (BuildLock)
            {
                exePath = null;
                reason = null;
                if (!AccountManager.General.Get<bool>("UseAltClient") || ClientProfiles.GetRole(account) != ClientRole.Alt)
                {
                    reason = "dedicated alt client disabled";
                    return false;
                }
                if (ClientProfiles.DirectLaunchFailed)
                {
                    reason = "direct launch failed earlier this session; keeping protocol fallback";
                    return false;
                }
                if (ClientProfiles.VerifiedArgumentFormat == null)
                {
                    reason = "launch the main through RAM first to verify Roblox's argument format";
                    return false;
                }
                try
                {
                    if (Directory.Exists(Root) && (File.GetAttributes(Root) & FileAttributes.ReparsePoint) != 0) throw new IOException("AltClient root must be a real directory");
                    string version = AccountManager.CurrentVersion ?? Bloxstrap.GetVersionGuid();
                    string source = ClientProfiles.FindClientSource(version);
                    if (source == null)
                    {
                        reason = "no installed client matches " + (version ?? "the current version");
                        return false;
                    }
                    string target = Path.Combine(Root, Path.GetFileName(source));
                    string marker = Path.Combine(target, ".ram-source");
                    if (!File.Exists(marker) || !string.Equals(File.ReadAllText(marker), source, StringComparison.OrdinalIgnoreCase) || !File.Exists(Path.Combine(target, "RobloxPlayerBeta.exe")))
                    {
                        if (Directory.Exists(target) && !CanDelete(target)) throw new IOException("Alt client is running or its process path cannot be checked");
                        string temporary = target + ".tmp";
                        DeleteOwnedFolder(temporary);
                        Directory.CreateDirectory(temporary);
                        bool loggedCopy = false;
                        CopyTree(source, temporary, ref loggedCopy);
                        File.WriteAllText(Path.Combine(temporary, ".ram-source"), source);
                        DeleteOwnedFolder(target);
                        Directory.Move(temporary, target);
                        Program.Logger.Info("[FastFlags] Built dedicated alt client: " + target);
                    }
                    var profile = ClientProfiles.ReadProfile(account);
                    if (profile == null) throw new InvalidDataException("Alt profile missing");
                    profile.Remove(ClientSettingsPatcher.RobloxSettingsKey);
                    if (AccountManager.General.Get<bool>("UnlockFPS") && profile["DFIntTaskSchedulerTargetFps"] == null)
                        profile["DFIntTaskSchedulerTargetFps"] = AccountManager.General.Exists("MaxFPSValue") ? AccountManager.General.Get<int>("MaxFPSValue") : 240;
                    string settings = Path.Combine(target, "ClientSettings");
                    Directory.CreateDirectory(settings);
                    File.WriteAllText(Path.Combine(settings, "ClientAppSettings.json"), profile.ToString(Formatting.Indented));
                    foreach (string stale in Directory.GetDirectories(Root, "version-*"))
                    {
                        if (string.Equals(stale, target, StringComparison.OrdinalIgnoreCase) || !CanDelete(stale)) continue;
                        try { DeleteOwnedFolder(stale); }
                        catch (Exception ex) { Program.Logger.Info("[FastFlags] Keeping stale alt client: " + ex.Message); }
                    }
                    exePath = Path.Combine(target, "RobloxPlayerBeta.exe");
                    return true;
                }
                catch (Exception ex)
                {
                    reason = ex.Message;
                    return false;
                }
            }
        }

        private static void CopyTree(string source, string target, ref bool loggedCopy)
        {
            foreach (string directory in Directory.GetDirectories(source))
            {
                if (Path.GetFileName(directory).Equals("ClientSettings", StringComparison.OrdinalIgnoreCase)) continue;
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("Client source contains a directory link: " + directory);
                string destination = Path.Combine(target, Path.GetFileName(directory));
                Directory.CreateDirectory(destination);
                CopyTree(directory, destination, ref loggedCopy);
            }
            foreach (string file in Directory.GetFiles(source))
            {
                string destination = Path.Combine(target, Path.GetFileName(file));
                if (Path.GetFileName(file).Equals("AppSettings.xml", StringComparison.OrdinalIgnoreCase)) File.Copy(file, destination);
                else if (!CreateHardLink(destination, file, IntPtr.Zero))
                {
                    int error = Marshal.GetLastWin32Error();
                    File.Copy(file, destination);
                    if (!loggedCopy) Program.Logger.Info("[FastFlags] Hard links unavailable; copying client files (Windows error " + error + ")");
                    loggedCopy = true;
                }
            }
        }

        private static bool CanDelete(string directory)
        {
            foreach (var process in Process.GetProcessesByName("RobloxPlayerBeta"))
            {
                using (process)
                {
                    try
                    {
                        if (process.HasExited) continue;
                        string executable = process.MainModule.FileName;
                        if (executable.StartsWith(Path.GetFullPath(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
                    }
                    catch { return false; } // Unknown/elevated process: keep all old clients conservatively.
                }
            }
            return true;
        }

        private static void DeleteOwnedFolder(string path)
        {
            string resolved = Path.GetFullPath(path);
            string root = Path.GetFullPath(Root) + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase) || Path.GetDirectoryName(resolved) != Path.GetFullPath(Root)) throw new IOException("Invalid alt client cleanup path");
            if (Directory.Exists(Root) && (File.GetAttributes(Root) & FileAttributes.ReparsePoint) != 0) throw new IOException("Refusing cleanup through a linked AltClient root");
            if (Directory.Exists(resolved))
            {
                if ((File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0 || Directory.GetDirectories(resolved, "*", SearchOption.AllDirectories).Any(p => (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)) throw new IOException("Refusing cleanup of linked directories");
                Directory.Delete(resolved, true);
            }
        }
    }
}
