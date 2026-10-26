using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RBX_Alt_Manager.Classes;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace RBX_Alt_Manager.Forms
{
    public class ClientProfilesForm : Form
    {
        private readonly Label Status = new Label { AutoSize = true, MaximumSize = new Size(640, 0) };

        public ClientProfilesForm(SettingsForm settings)
        {
            Text = "Client Profiles";
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(700, 620);
            MinimumSize = new Size(600, 450);
            var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(16) };
            Controls.Add(panel);
            panel.Controls.Add(new Label { Text = "Main group", AutoSize = true });
            var main = new TextBox { Text = ClientProfiles.MainGroup, Width = 300 };
            main.Validated += (s, e) =>
            {
                if (string.IsNullOrWhiteSpace(ClientProfiles.NormalizeGroup(main.Text))) { main.Text = ClientProfiles.MainGroup; return; }
                Save("MainGroup", main.Text.Trim());
            };
            panel.Controls.Add(main);
            AddCheck(panel, "Use dedicated low-graphics alt client", "UseAltClient");
            AddCheck(panel, "Alt efficiency mode", "AltEfficiencyMode");
            panel.Controls.Add(new Label { Text = "Alt window size (0 = off; suggested 800 × 600)", AutoSize = true });
            var dimensions = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            foreach (string key in new[] { "AltWindowWidth", "AltWindowHeight" })
            {
                dimensions.Controls.Add(new Label { Text = key.EndsWith("Width") ? "Width" : "Height", AutoSize = true, Padding = new Padding(0, 5, 0, 0) });
                var input = new NumericUpDown { Minimum = 0, Maximum = 16384, Width = 80, Value = Math.Max(0, Math.Min(16384, AccountManager.General.Get<int>(key))) };
                input.ValueChanged += (s, e) => Save(key, input.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                dimensions.Controls.Add(input);
            }
            panel.Controls.Add(dimensions);
            AddButton(panel, "Capture main's in-game settings now", () => WithLaunchGate(() =>
                MessageBox.Show(this, ClientProfiles.CaptureMainSnapshot().ToString(Formatting.Indented), "Main settings captured")));
            AddButton(panel, "Clean Bloxstrap flags", () => WithLaunchGate(CleanBloxstrapFlags));
            AddButton(panel, "Refresh status", RefreshStatus);
            panel.Controls.Add(Status);
            BackColor = ThemeEditor.FormsBackground;
            ForeColor = ThemeEditor.FormsForeground;
            settings.ApplyTheme(Controls);
            RefreshStatus();
        }

        private void Save(string key, string value)
        {
            AccountManager.General.Set(key, value);
            AccountManager.IniSettings.Save("RAMSettings.ini");
            RefreshStatus();
        }

        private void AddCheck(FlowLayoutPanel panel, string text, string key)
        {
            var check = new CheckBox { Text = text, AutoSize = true, Checked = AccountManager.General.Get<bool>(key) };
            check.CheckedChanged += (s, e) => Save(key, check.Checked ? "true" : "false");
            panel.Controls.Add(check);
        }

        private void AddButton(FlowLayoutPanel panel, string text, Action action)
        {
            var button = new Button { Text = text, AutoSize = true, Padding = new Padding(5) };
            button.Click += (s, e) =>
            {
                try { action(); RefreshStatus(); }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "Client Profiles", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            };
            panel.Controls.Add(button);
        }

        private void WithLaunchGate(Action action)
        {
            if (!ClientProfiles.LaunchGate.Wait(0)) throw new InvalidOperationException("Wait for the current launch to finish.");
            try { action(); }
            finally { ClientProfiles.LaunchGate.Release(); }
        }

        private static void RequireRobloxClosed()
        {
            var processes = Process.GetProcessesByName("RobloxPlayerBeta");
            bool running = processes.Length > 0;
            foreach (var process in processes) process.Dispose();
            if (running) throw new InvalidOperationException("Close all Roblox clients before cleaning Bloxstrap flags.");
        }

        private void CleanBloxstrapFlags()
        {
            RequireRobloxClosed();
            string directory = Bloxstrap.ModificationsClientSettings;
            if (directory == null) throw new InvalidOperationException("No supported Bloxstrap protocol handler detected.");
            string path = Path.Combine(directory, "ClientAppSettings.json");
            if (!File.Exists(path)) throw new FileNotFoundException("Bloxstrap has no flags file to clean.");
            string original = File.ReadAllText(path);
            if (!original.TryParseJson(out JObject flags) || flags == null) throw new InvalidDataException("Bloxstrap flags are not valid JSON.");
            var keys = new HashSet<string>(ClientSettingsPatcher.LowGraphicsPreset.Properties().Select(p => p.Name), StringComparer.Ordinal);
            keys.Add("DFIntTaskSchedulerTargetFps");
            foreach (var account in AccountManager.AccountsList.Where(a => !ClientProfiles.IsMain(a)))
            {
                JObject profile = ClientProfiles.ReadProfile(account);
                if (profile != null) foreach (var property in profile.Properties()) keys.Add(property.Name);
            }
            string temporaryProfile = Path.Combine(ClientSettingsPatcher.FastFlagsDirectory, "alt_temp.json");
            if (File.Exists(temporaryProfile) && File.ReadAllText(temporaryProfile).TryParseJson(out JObject temp) && temp != null)
                foreach (var property in temp.Properties()) keys.Add(property.Name);
            keys.Remove(ClientSettingsPatcher.RobloxSettingsKey);
            var removed = flags.Properties().Where(p => keys.Contains(p.Name)).Select(p => p.Name).ToArray();
            if (removed.Length == 0) { MessageBox.Show(this, "No alt profile keys were found in Bloxstrap's flags."); return; }
            string backup = Path.Combine(ClientSettingsPatcher.FastFlagsDirectory, "bloxstrap-flags.bak.json");
            if (File.Exists(backup)) throw new IOException("A cleanup backup already exists. Move it somewhere safe before cleaning again: " + backup);
            if (MessageBox.Show(this, "Remove these keys from Bloxstrap?\n\n" + string.Join("\n", removed) + "\n\nA backup will be saved to " + backup, "Clean Bloxstrap flags", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            RequireRobloxClosed();
            if (File.ReadAllText(path) != original) throw new IOException("Bloxstrap flags changed. Refresh and try again.");
            Directory.CreateDirectory(ClientSettingsPatcher.FastFlagsDirectory);
            using (var writer = new StreamWriter(new FileStream(backup, FileMode.CreateNew, FileAccess.Write))) writer.Write(original);
            foreach (string key in removed) flags.Remove(key);
            File.WriteAllText(path, flags.ToString(Formatting.Indented));
            MessageBox.Show(this, "Removed:\n" + string.Join("\n", removed), "Bloxstrap flags cleaned");
        }

        private void RefreshStatus()
        {
            try
            {
                string exe = Bloxstrap.Executable;
                string bootstrapper = exe == null ? "not detected" : exe + "\nVersion: " + FileVersionInfo.GetVersionInfo(exe).FileVersion + " / " + (Bloxstrap.GetVersionGuid() ?? "unknown client");
                string clients = Directory.Exists(AltClient.Root) ? string.Join("\n", Directory.GetDirectories(AltClient.Root, "version-*").Where(p => File.Exists(Path.Combine(p, ".ram-source")))) : "";
                string groups = string.Join("\n", AccountManager.AccountsList.GroupBy(a => a.Group).Select(group =>
                {
                    Account account = group.First();
                    ClientRole role = ClientProfiles.GetRole(account);
                    return (group.Key ?? "(no group)") + ": " + role + (role == ClientRole.Alt ? " (" + Path.GetFileName(ClientSettingsPatcher.FindGroupProfile(account)) + ")" : "");
                }));
                string arguments = ClientProfiles.DirectLaunchFailed ? "direct launch failed; protocol fallback for this session" : ClientProfiles.VerifiedArgumentFormat ?? "unverified; launch main through RAM first; alts use protocol fallback";
                Status.Text = "Bloxstrap / fork: " + bootstrapper + "\n\nLive Roblox: " + (AccountManager.CurrentVersion ?? "not fetched") + "\n\nAlt client: " + (clients.Length > 0 ? clients : "not built") + "\n\nDirect arguments: " + arguments + "\n\nGroups:\n" + groups;
            }
            catch (Exception ex) { Status.Text = "Status unavailable: " + ex.Message; }
        }
    }
}
