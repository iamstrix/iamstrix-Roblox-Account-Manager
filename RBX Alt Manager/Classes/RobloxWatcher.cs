using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Timers;
using System.Threading.Tasks;

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
        private static readonly HashSet<int> EfficiencyHandled = new HashSet<int>();
        private static readonly HashSet<int> MainExitHandled = new HashSet<int>();
        private static readonly object ProcessLock = new object();

        [StructLayout(LayoutKind.Sequential)]
        private struct PowerThrottlingState { public uint Version, ControlMask, StateMask; }
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessInformation(IntPtr process, int informationClass, ref PowerThrottlingState information, uint size);

        private static void ApplyEfficiency(Process process)
        {
            lock (ProcessLock) if (!EfficiencyHandled.Add(process.Id)) return;
            try
            {
                process.PriorityClass = ProcessPriorityClass.BelowNormal;
                var state = new PowerThrottlingState { Version = 1, ControlMask = 1, StateMask = 1 };
                if (!SetProcessInformation(process.Handle, 4, ref state, (uint)Marshal.SizeOf(state)))
                    Program.Logger.Info("[FastFlags] EcoQoS unavailable for PID " + process.Id + ": " + Marshal.GetLastWin32Error());
            }
            catch (Exception ex) { Program.Logger.Info("[FastFlags] Efficiency mode unavailable for PID " + process.Id + ": " + ex.Message); }
        }

        private static void WatchMainExit(Process process)
        {
            lock (ProcessLock) if (!MainExitHandled.Add(process.Id)) return;
            int pid = process.Id;
            try
            {
                var watched = Process.GetProcessById(pid);
                watched.Exited += async (s, e) =>
                {
                    try
                    {
                        await Task.Delay(2000);
                        await ClientProfiles.LaunchGate.WaitAsync();
                        try { ClientProfiles.CaptureMainSnapshot(true); }
                        finally { ClientProfiles.LaunchGate.Release(); }
                    }
                    catch (Exception ex) { Program.Logger.Error("[FastFlags] Main exit snapshot: " + ex.Message); }
                    finally { lock (ProcessLock) MainExitHandled.Remove(pid); watched.Dispose(); }
                };
                watched.EnableRaisingEvents = true;
            }
            catch { lock (ProcessLock) MainExitHandled.Remove(pid); }
        }
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
            var processes = Process.GetProcessesByName("RobloxPlayerBeta");
            var liveIds = new HashSet<int>(processes.Select(p => p.Id));
            lock (ProcessLock) EfficiencyHandled.RemoveWhere(pid => !liveIds.Contains(pid));

            foreach (var process in processes)
            {
                void Kill(string Reason) { Program.Logger.Info($"Attempting to kill process {process.Id}, reason: {Reason}"); try { process.Kill(); } catch { } }

                string CommandLine = process.GetCommandLine();

                // This ignores the second roblox process which would cause 268 (Unexpected client behavior) kicks if it were closed.
                if (string.IsNullOrEmpty(CommandLine)) continue; // Roblox's second process
                if (CommandLine.StartsWith("\\??\\")) continue; // Roblox's second process
                if (CommandLine.Contains("--launch-to-tray")) continue; // Roblox background helper

                string TrackerID = Utilities.GetBrowserTrackerId(CommandLine);

                Account account = null;
                if (!string.IsNullOrEmpty(TrackerID))
                    account = AccountManager.AccountsList.FirstOrDefault(a => a.BrowserTrackerID == TrackerID);
                if (account == null)
                    account = AccountManager.AccountsList.FirstOrDefault(a => a.ProcessId == process.Id);

                ClientRole role = ClientProfiles.GetRole(account);
                if (role == ClientRole.Main) WatchMainExit(process);
                if (process.MainWindowHandle == Focused) continue; // Never trim or change priority on the focused window.
                if (role == ClientRole.Alt && AccountManager.General.Get<bool>("AltEfficiencyMode")) ApplyEfficiency(process);

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
                            if (account != null && role == ClientRole.Alt)
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
