using System;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TarkovColor
{
    internal static class Program
    {
        private const string TaskSync = "TarkovColorSync";
        private const string TaskTray = "TarkovColorTray";

        // Retired in favour of TaskSync; still removed on uninstall so upgrades clean up.
        private const string LegacyTaskOn = "TarkovColorOn";
        private const string LegacyTaskOff = "TarkovColorOff";

        private const string FilterOn = "TarkovColorStartFilter";
        private const string FilterOff = "TarkovColorStopFilter";
        private const string ConsumerSync = "TarkovColorSyncConsumer";
        private const string LegacyConsumerOn = "TarkovColorStartConsumer";
        private const string LegacyConsumerOff = "TarkovColorStopConsumer";
        private const string WmiScope = @"root\subscription";

        private const string IcmKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ICM";
        private const string GammaRangeValue = "GdiIcmGammaRange";
        private const string OurKey = @"Software\TarkovColorToggle";

        private static string ExePath { get { return Assembly.GetExecutingAssembly().Location; } }
        private static string Schtasks
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe"); }
        }

        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            try
            {
                string cmd = args.Length > 0 ? args[0] : "";

                if (Eq(cmd, "-Sync") || Eq(cmd, "-On") || Eq(cmd, "-Off")) { Sync(); return 0; }
                if (Eq(cmd, "-Apply")) { Applier.Request(args.Length > 1 ? args[1] : null); return 0; }
                if (Eq(cmd, "-Tray")) return RunTray();

                if (Eq(cmd, "-Install"))
                {
                    string user = args.Length > 1 ? args[1] : CurrentUser();
                    if (!IsElevated()) { Relaunch("-Install", user); return 0; }
                    Install(user);
                    MessageBox.Show("Installed.\n\nThe tray icon is running. Profiles switch automatically for the "
                        + "applications listed under Settings.", "Display Profile Switcher");
                    return 0;
                }

                if (Eq(cmd, "-AudioSetup"))
                {
                    if (!IsElevated()) { Relaunch("-AudioSetup"); return 0; }
                    if (Setup.ApoInstalled && (Setup.LoudMaxInstalled || !Setup.AnyPresetNeedsLoudMax()))
                    {
                        MessageBox.Show("The audio side is already set up.\n\nEqualizer APO is installed"
                            + (Setup.LoudMaxInstalled ? " and LoudMax is present." : "."),
                            "Display Profile Switcher");
                        return 0;
                    }
                    SetUpAudioDependencies();
                    return 0;
                }

                if (Eq(cmd, "-Rewatch"))
                {
                    if (!IsElevated()) { Relaunch("-Rewatch"); return 0; }
                    return Rewatch();
                }

                if (Eq(cmd, "-Uninstall"))
                {
                    if (!IsElevated()) { Relaunch("-Uninstall"); return 0; }
                    Uninstall();
                    MessageBox.Show("Uninstalled.", "Display Profile Switcher");
                    return 0;
                }

                return NoArgs();
            }
            catch (Exception ex)
            {
                Config.Log("ERROR: " + ex);
                MessageBox.Show(ex.Message, "Display Profile Switcher - Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        /// <summary>Double-click behaviour: open settings if running, otherwise start or offer to install.</summary>
        private static int NoArgs()
        {
            if (Applier.IsTrayRunning())
            {
                MessageBox.Show("Display Profile Switcher is already running.\n\n"
                    + "Use the tray icon (bottom-right, near the clock) to switch profiles or open Settings.",
                    "Display Profile Switcher");
                return 0;
            }

            if (TaskExists(TaskTray)) return RunTray();

            DialogResult r = MessageBox.Show(
                "Set up automatic display profile switching?\n\n"
                + "This installs a tray app with global hotkeys, and switches profiles automatically "
                + "when the applications you configure start and stop.",
                "Display Profile Switcher - Setup", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) return 0;

            if (!IsElevated()) { Relaunch("-Install", CurrentUser()); return 0; }
            Install(CurrentUser());
            MessageBox.Show("Installed.", "Display Profile Switcher");
            return 0;
        }

        /// <summary>
        /// Fired by WMI whenever any watched process starts or stops. Rather than trusting the
        /// event itself, it re-reads what is actually running and applies the first matching
        /// rule - so overlapping apps and missed events both resolve correctly.
        /// </summary>
        private static void Sync()
        {
            Config cfg = Config.Load();
            Profile p = cfg.ResolveActiveRule();
            Config.Log("Sync: " + cfg.Rules.Count + " rule(s), " + cfg.Profiles.Count + " profile(s) -> "
                + (p == null ? "(default)" : p.Name));
            Applier.Request(p == null ? null : p.Name);
        }

        private static int RunTray()
        {
            bool created;
            using (Mutex mutex = new Mutex(true, "TarkovColorToggle_SingleInstance_a7f3", out created))
            {
                if (!created) return 0;
                Application.Run(new TrayApp());
            }
            return 0;
        }

        // ---------- install / uninstall ----------

        private static void Install(string user)
        {
            Uninstall();

            // Make sure a config exists so the tray has something to show.
            if (!File.Exists(Config.ConfigPath)) Config.CreateDefault().Save();

            // Record the untouched display state now, while nothing of ours is applied yet.
            if (!Ramp.HasBaseline) Ramp.CaptureBaseline();

            CreateTask(TaskSync, "-Sync", user, false);
            CreateTask(TaskTray, "-Tray", user, true);

            Config cfg = Config.Load();
            string startWhere = BuildStartWhere(cfg);
            if (startWhere != null)
            {
                CreateFilter(FilterOn, "SELECT * FROM Win32_ProcessStartTrace WHERE " + startWhere);
                CreateFilter(FilterOff, "SELECT * FROM Win32_ProcessStopTrace WHERE " + BuildStopWhere(cfg));
                CreateConsumer(ConsumerSync, "\"" + Schtasks + "\" /run /tn \"" + TaskSync + "\"");
                CreateBinding(FilterOn, ConsumerSync);
                CreateBinding(FilterOff, ConsumerSync);
            }
            else
            {
                Config.Log("No application rules configured; nothing to watch.");
            }

            RelaxGammaRange();
            SetUpAudioDependencies();

            // Start the tray through the scheduled task so it runs unelevated: an elevated
            // tray window would have messages from the (limited) game tasks blocked by UIPI.
            RunProcess(Schtasks, "/run /tn \"" + TaskTray + "\"", false);

            Config.Log("Installed for user=" + user);
        }

        /// <summary>
        /// Offers the optional audio pieces. Runs inside the elevated install so it can write
        /// into Equalizer APO's plugin folder, which is not user-writable. Everything here is
        /// optional: the display side works regardless.
        /// </summary>
        private static void SetUpAudioDependencies()
        {
            // OfferApo waits for its installer, so on success we carry straight on to the
            // plugin instead of sending the user back through the tray menu.
            if (!Setup.ApoInstalled && !Setup.OfferApo(null)) return;

            if (Setup.LoudMaxInstalled || !Setup.AnyPresetNeedsLoudMax()) return;

            DialogResult r = MessageBox.Show(
                "One of the included EQ presets uses the LoudMax limiter plugin, which is not installed.\n\n"
                + "Download it now from the author's site?\n\n"
                + "It is free, and it is fetched from loudmax.blogspot.com rather than bundled here, "
                + "because its author does not permit redistribution.\n\n"
                + "Presets that do not use it work either way.",
                "Display Profile Switcher - Audio setup",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);

            if (r != DialogResult.Yes) return;

            if (Setup.InstallLoudMax(null))
                MessageBox.Show("LoudMax installed.", "Display Profile Switcher");
        }

        /// <summary>
        /// Rebuilds only the WMI watch list, for when the user edits the application rules.
        /// Leaves the scheduled tasks and the running tray app alone.
        /// </summary>
        private static int Rewatch()
        {
            try
            {
                RemoveBindings();
                RemoveByName("__EventFilter", FilterOn);
                RemoveByName("__EventFilter", FilterOff);
                RemoveByName("CommandLineEventConsumer", ConsumerSync);

                Config cfg = Config.Load();
                string startWhere = BuildStartWhere(cfg);
                if (startWhere == null)
                {
                    Config.Log("Rewatch: no application rules configured; watcher cleared.");
                    return 0;
                }

                CreateFilter(FilterOn, "SELECT * FROM Win32_ProcessStartTrace WHERE " + startWhere);
                CreateFilter(FilterOff, "SELECT * FROM Win32_ProcessStopTrace WHERE " + BuildStopWhere(cfg));
                CreateConsumer(ConsumerSync, "\"" + Schtasks + "\" /run /tn \"" + TaskSync + "\"");
                CreateBinding(FilterOn, ConsumerSync);
                CreateBinding(FilterOff, ConsumerSync);

                Config.Log("Rewatch: now watching " + cfg.Rules.Count + " application(s).");
                return 0;
            }
            catch (Exception ex)
            {
                Config.Log("Rewatch ERROR: " + ex.Message);
                return 1;
            }
        }

        private static void Uninstall()
        {
            IntPtr tray = Applier.FindTrayWindow();
            if (tray != IntPtr.Zero) RunProcess(Schtasks, "/end /tn \"" + TaskTray + "\"", false);

            RemoveBindings();
            RemoveByName("__EventFilter", FilterOn);
            RemoveByName("__EventFilter", FilterOff);
            RemoveByName("CommandLineEventConsumer", ConsumerSync);
            RemoveByName("CommandLineEventConsumer", LegacyConsumerOn);
            RemoveByName("CommandLineEventConsumer", LegacyConsumerOff);

            DeleteTask(TaskSync);
            DeleteTask(TaskTray);
            DeleteTask(LegacyTaskOn);
            DeleteTask(LegacyTaskOff);

            RestoreGammaRange();
            AudioProfile.Remove();

            Config.Log("Uninstalled");
        }

        /// <summary>
        /// Windows silently ignores gamma ramps it considers too extreme (an anti-tampering
        /// heuristic): SetDeviceGammaRamp returns success but nothing changes. This opens the
        /// permitted range so strong contrast/gamma profiles actually apply. Restored on uninstall.
        /// </summary>
        private static void RelaxGammaRange()
        {
            try
            {
                using (RegistryKey icm = Registry.LocalMachine.CreateSubKey(IcmKey))
                {
                    if (icm == null) return;
                    object prior = icm.GetValue(GammaRangeValue);

                    using (RegistryKey ours = Registry.CurrentUser.CreateSubKey(OurKey))
                    {
                        if (ours != null)
                            ours.SetValue("PriorGammaRange", prior == null ? -1 : Convert.ToInt32(prior), RegistryValueKind.DWord);
                    }

                    icm.SetValue(GammaRangeValue, 256, RegistryValueKind.DWord);
                }
            }
            catch (Exception ex)
            {
                Config.Log("Could not relax gamma range: " + ex.Message);
            }
        }

        private static void RestoreGammaRange()
        {
            try
            {
                int prior = -1;
                using (RegistryKey ours = Registry.CurrentUser.OpenSubKey(OurKey, true))
                {
                    if (ours == null) return; // never installed by us; leave the machine alone
                    object v = ours.GetValue("PriorGammaRange");
                    if (v != null) prior = Convert.ToInt32(v);
                    ours.DeleteValue("PriorGammaRange", false);
                }

                using (RegistryKey icm = Registry.LocalMachine.OpenSubKey(IcmKey, true))
                {
                    if (icm == null) return;
                    if (prior < 0) icm.DeleteValue(GammaRangeValue, false);
                    else icm.SetValue(GammaRangeValue, prior, RegistryValueKind.DWord);
                }
            }
            catch (Exception ex)
            {
                Config.Log("Could not restore gamma range: " + ex.Message);
            }
        }

        /// <summary>Exact-match clause over every watched executable, or null if there are none.</summary>
        private static string BuildStartWhere(Config cfg)
        {
            StringBuilder sb = new StringBuilder();
            foreach (AppRule r in cfg.Rules)
            {
                string name = SanitizeProcess(r.ProcessName);
                if (name.Length == 0) continue;
                if (sb.Length > 0) sb.Append(" OR ");
                sb.Append("ProcessName = '").Append(name).Append("'");
            }
            return sb.Length == 0 ? null : sb.ToString();
        }

        /// <summary>
        /// Win32_ProcessStopTrace truncates ProcessName to 14 characters, so stop events
        /// have to be matched on a prefix rather than compared exactly.
        /// </summary>
        private static string BuildStopWhere(Config cfg)
        {
            StringBuilder sb = new StringBuilder();
            foreach (AppRule r in cfg.Rules)
            {
                string name = SanitizeProcess(r.ProcessName);
                if (name.Length == 0) continue;
                string prefix = name.Length > 14 ? name.Substring(0, 14) : name;
                if (sb.Length > 0) sb.Append(" OR ");
                sb.Append("ProcessName LIKE '").Append(prefix).Append("%'");
            }
            return sb.ToString();
        }

        /// <summary>Strips characters that would break out of the WQL string literal.</summary>
        private static string SanitizeProcess(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            return name.Trim().Replace("'", "").Replace("\"", "").Replace("\\", "");
        }

        // ---------- scheduled tasks ----------

        private static void CreateTask(string name, string arg, string user, bool atLogon)
        {
            string tr = "\\\"" + ExePath + "\\\" " + arg;
            StringBuilder sb = new StringBuilder();
            sb.Append("/create /tn \"").Append(name).Append("\" /tr \"").Append(tr).Append("\" ");
            sb.Append(atLogon ? "/sc ONLOGON " : "/sc ONCE /st 00:00 /sd 01/01/2020 ");
            sb.Append("/ru \"").Append(user).Append("\" /it /rl LIMITED /f");
            RunProcess(Schtasks, sb.ToString(), true);
        }

        private static void DeleteTask(string name)
        {
            RunProcess(Schtasks, "/delete /tn \"" + name + "\" /f", false);
        }

        private static bool TaskExists(string name)
        {
            return RunProcess(Schtasks, "/query /tn \"" + name + "\"", false) == 0;
        }

        private static int RunProcess(string exe, string args, bool throwOnError)
        {
            ProcessStartInfo psi = new ProcessStartInfo(exe, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;

            using (Process p = Process.Start(psi))
            {
                string so = p.StandardOutput.ReadToEnd();
                string se = p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (throwOnError && p.ExitCode != 0)
                    throw new Exception(Path.GetFileName(exe) + " " + args + " failed:\n" + so + se);
                return p.ExitCode;
            }
        }

        // ---------- WMI ----------

        private static void CreateFilter(string name, string query)
        {
            ManagementClass mc = new ManagementClass(new ManagementScope(WmiScope), new ManagementPath("__EventFilter"), null);
            ManagementObject o = mc.CreateInstance();
            o["Name"] = name;
            o["EventNamespace"] = "root\\cimv2";
            o["QueryLanguage"] = "WQL";
            o["Query"] = query;
            o.Put();
        }

        private static void CreateConsumer(string name, string commandLine)
        {
            ManagementClass mc = new ManagementClass(new ManagementScope(WmiScope), new ManagementPath("CommandLineEventConsumer"), null);
            ManagementObject o = mc.CreateInstance();
            o["Name"] = name;
            o["CommandLineTemplate"] = commandLine;
            o.Put();
        }

        private static void CreateBinding(string filter, string consumer)
        {
            ManagementClass mc = new ManagementClass(new ManagementScope(WmiScope), new ManagementPath("__FilterToConsumerBinding"), null);
            ManagementObject o = mc.CreateInstance();
            o["Filter"] = "__EventFilter.Name=\"" + filter + "\"";
            o["Consumer"] = "CommandLineEventConsumer.Name=\"" + consumer + "\"";
            o.Put();
        }

        private static void RemoveBindings()
        {
            try
            {
                ManagementObjectSearcher s = new ManagementObjectSearcher(
                    new ManagementScope(WmiScope), new ObjectQuery("SELECT * FROM __FilterToConsumerBinding"));
                foreach (ManagementObject o in s.Get())
                {
                    string f = Convert.ToString(o["Filter"]);
                    string c = Convert.ToString(o["Consumer"]);
                    if ((f != null && f.IndexOf("TarkovColor", StringComparison.OrdinalIgnoreCase) >= 0) ||
                        (c != null && c.IndexOf("TarkovColor", StringComparison.OrdinalIgnoreCase) >= 0))
                        o.Delete();
                }
            }
            catch (Exception ex) { Config.Log("RemoveBindings: " + ex.Message); }
        }

        private static void RemoveByName(string className, string name)
        {
            try
            {
                ManagementObjectSearcher s = new ManagementObjectSearcher(
                    new ManagementScope(WmiScope),
                    new ObjectQuery("SELECT * FROM " + className + " WHERE Name = '" + name + "'"));
                foreach (ManagementObject o in s.Get()) o.Delete();
            }
            catch (Exception ex) { Config.Log("RemoveByName(" + name + "): " + ex.Message); }
        }

        // ---------- helpers ----------

        private static bool Eq(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        private static string CurrentUser()
        {
            return Environment.UserDomainName + "\\" + Environment.UserName;
        }

        private static bool IsElevated()
        {
            using (WindowsIdentity id = WindowsIdentity.GetCurrent())
            {
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
        }

        private static void Relaunch(params string[] args)
        {
            StringBuilder sb = new StringBuilder();
            foreach (string a in args)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append('"').Append(a).Append('"');
            }
            ProcessStartInfo psi = new ProcessStartInfo(ExePath, sb.ToString());
            psi.UseShellExecute = true;
            psi.Verb = "runas";
            try { Process.Start(psi); }
            catch (System.ComponentModel.Win32Exception) { /* UAC declined */ }
        }
    }
}
