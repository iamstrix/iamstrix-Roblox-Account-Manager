using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Timers;

namespace RBX_Alt_Manager.Classes
{
    internal class RobloxWatcher
    {
        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("psapi.dll", SetLastError = true)]
        public static extern bool EmptyWorkingSet(IntPtr hProcess);

        public static readonly string HandlePath = Path.Combine(Environment.CurrentDirectory, "handle.bin");

        public static HashSet<int> Seen = new HashSet<int>();
        public static List<RobloxProcess> Instances = new List<RobloxProcess>();
        public static bool VerifyDataModel = true;
        public static bool IgnoreExistingProcesses = true;
        public static bool CloseIfMemoryLow = false;
        public static bool CloseIfWindowTitle = false;
        public static bool RememberWindowPositions = false;
        public static bool AutoTrimAltsMemory = false;
        public static int MemoryLowValue = 200;
        public static string ExpectedWindowTitle = "Roblox";

        public static Timer ReadTimer
        {
            get
            {
                if (readTimer == null)
                {
                    readTimer = new Timer(250);
                    readTimer.Elapsed += (s, e) => LogFileRead?.Invoke(null, new EventArgs());
                }

                return readTimer;
            }
        }
        private static Timer readTimer;

        public static event EventHandler<EventArgs> LogFileRead;

        public static void CheckProcesses()
        {
            IntPtr Focused = GetForegroundWindow();

            foreach (var process in Process.GetProcessesByName("RobloxPlayerBeta"))
            {
                if (process.MainWindowHandle == Focused) continue; // Entirely ignore focused windows

                void Kill(string Reason) { Program.Logger.Info($"Attempting to kill process {process.Id}, reason: {Reason}"); try { process.Kill(); } catch { } }

                string CommandLine = process.GetCommandLine();

                // This ignores the second roblox process which would cause 268 (Unexpected client behavior) kicks if it were closed.
                if (string.IsNullOrEmpty(CommandLine)) continue; // Roblox's second process
                if (CommandLine.StartsWith("\\??\\")) continue; // Roblox's second process
                if (CommandLine.Contains("--launch-to-tray")) continue; // Roblox background helper

                var TrackerMatch = Regex.Match(CommandLine, @"\-b (\d+)");
                string TrackerID = TrackerMatch.Success ? TrackerMatch.Groups[1].Value : string.Empty;

                Account account = null;
                if (!string.IsNullOrEmpty(TrackerID))
                    account = AccountManager.AccountsList.FirstOrDefault(a => a.BrowserTrackerID == TrackerID);
                if (account == null)
                    account = AccountManager.AccountsList.FirstOrDefault(a => a.ProcessId == process.Id);

                // If not ran with -t/-j, only proceed if this process is known to RAM or has a main window
                if (!CommandLine.Contains("-t ") && !CommandLine.Contains("-j ") && account == null && process.MainWindowHandle == IntPtr.Zero)
                    continue;

                try
                {
                    if ((DateTime.Now - process.StartTime).TotalSeconds > 30) // Roblox shouldn't take that long to startup, right? Surely nobody will be using a potato with these settings.
                    {
                        if (CloseIfMemoryLow && process.WorkingSet64 / 1024 / 1024 < MemoryLowValue)
                            Kill($"Low Memory ({process.WorkingSet64 / 1024 / 1024} < {MemoryLowValue})");

                        if (CloseIfWindowTitle && process.MainWindowTitle != ExpectedWindowTitle)
                            Kill($"Window Title isn't {ExpectedWindowTitle}, got {process.MainWindowTitle}");

                        if (AutoTrimAltsMemory)
                        {
                            bool isMain = account != null && !string.IsNullOrEmpty(account.Group) && account.Group.Equals("Main", StringComparison.OrdinalIgnoreCase);
                            if (!isMain)
                            {
                                try
                                {
                                    EmptyWorkingSet(process.Handle);
                                }
                                catch (Exception ex)
                                {
                                    Program.Logger.Error($"Error trimming working set for PID {process.Id}: {ex.Message}");
                                }
                            }
                        }
                    }
                }
                catch (Exception x) { Program.Logger.Error($"Error with checking for Memory & Window Title: {x.Message}\n{x.StackTrace}"); }

                if (RememberWindowPositions && (DateTime.Now - process.StartTime).TotalSeconds > 30 && account != null)
                {
                    try
                    {
                        if (process.MainWindowHandle != IntPtr.Zero)
                        {
                            GetWindowRect(process.MainWindowHandle, out RECT rect);

                            account.SetField("Window_Position_X", $"{rect.Left:0}");
                            account.SetField("Window_Position_Y", $"{rect.Top:0}");
                            account.SetField("Window_Width", $"{rect.Right - rect.Left:0}");
                            account.SetField("Window_Height", $"{rect.Bottom - rect.Top:0}");
                        }
                    }
                    catch { }
                }

                if (Seen.Contains(process.Id)) continue;

                try
                {
                    if (process.HasExited) continue; // Will throw an exception if we have no access, wrapped in a try-catch to ignore Roblox's second process which is ran with elevated permissions

                    Instances.Add(new RobloxProcess(process));
                    Seen.Add(process.Id);
                }
                catch (Exception x) { Program.Logger.Error($"Access to Process {process.Id} denied! This may be due to roblox being ran as admin or roblox's second process(This message can be ignored): {x.Message}"); }
            }
        }

        public static bool IsHandleEulaAccepted()
        {
            RegistryKey AcceptedHEULA = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Sysinternals\Handle");
            object EulaObject = AcceptedHEULA?.GetValue("EulaAccepted");

            return AcceptedHEULA != null && EulaObject != null && int.TryParse(EulaObject.ToString(), out int EULA) && EULA == 1;
        }
    }
}