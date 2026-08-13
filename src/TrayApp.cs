using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TarkovColor
{
    /// <summary>
    /// Resident tray process. Owns the global hotkeys and the saturation effect
    /// (which only survives while its owning process is alive).
    /// </summary>
    public class TrayApp : Form
    {
        private const int WM_HOTKEY = 0x0312;
        private const int WM_COPYDATA = 0x004A;

        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const uint MOD_WIN = 0x0008;
        private const uint MOD_NOREPEAT = 0x4000;

        private const int ResetHotkeyId = 9000;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private NotifyIcon _icon;
        private Config _config;
        private readonly List<int> _registered = new List<int>();
        private ConfigForm _openConfig;
        private System.IO.FileSystemWatcher _apoWatcher;
        private System.Windows.Forms.Timer _apoDebounce;

        public TrayApp()
        {
            Text = Applier.IpcWindowTitle;
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-32000, -32000);
            Size = new Size(1, 1);

            // Force the handle to exist so FindWindow / RegisterHotKey can target it.
            IntPtr forceHandle = Handle;

            _config = Config.Load();
            if (!System.IO.File.Exists(Config.ConfigPath))
            {
                try { _config.Save(); }
                catch (Exception ex) { Config.Log("Could not write initial config: " + ex.Message); }
            }

            _icon = new NotifyIcon
            {
                Icon = BuildIcon(),
                Text = "Display Profile Switcher",
                Visible = true
            };
            _icon.MouseUp += OnIconMouseUp;

            Saturation.Initialize();
            RegisterHotkeys();
            ReapplyActive();
            WatchApoConfig();
        }

        /// <summary>
        /// APO's Editor rewrites config.txt in full when it saves, which drops our include
        /// line and silently freezes the EQ until the next profile switch. This restores it
        /// as soon as that happens.
        ///
        /// FileSystemWatcher wraps ReadDirectoryChangesW, so this is a kernel-pushed
        /// notification rather than a polling loop.
        /// </summary>
        private void WatchApoConfig()
        {
            string dir = AudioProfile.ConfigDir;
            if (dir == null) return;

            try
            {
                // A save arrives as several write events; coalesce them and let the Editor
                // finish writing before reading the file back.
                _apoDebounce = new System.Windows.Forms.Timer();
                _apoDebounce.Interval = 700;
                _apoDebounce.Tick += delegate
                {
                    _apoDebounce.Stop();
                    try { AudioProfile.EnsureIncludeNow(); }
                    catch (Exception ex) { Config.Log("Include restore failed: " + ex.Message); }
                };

                _apoWatcher = new System.IO.FileSystemWatcher(dir, "config.txt");
                _apoWatcher.NotifyFilter = System.IO.NotifyFilters.LastWrite | System.IO.NotifyFilters.Size;
                System.IO.FileSystemEventHandler onChange = delegate
                {
                    // Marshal onto the UI thread; the watcher raises events on a pool thread.
                    if (IsDisposed || !IsHandleCreated) return;
                    try { BeginInvoke((MethodInvoker)delegate { _apoDebounce.Stop(); _apoDebounce.Start(); }); }
                    catch { }
                };
                _apoWatcher.Changed += onChange;
                _apoWatcher.Created += onChange;
                _apoWatcher.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                Config.Log("Could not watch the Equalizer APO config: " + ex.Message);
            }
        }

        protected override void SetVisibleCore(bool value)
        {
            base.SetVisibleCore(false);
        }

        private static Icon BuildIcon()
        {
            using (Bitmap bmp = new Bitmap(16, 16))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    using (var brush = new System.Drawing.Drawing2D.LinearGradientBrush(
                        new Rectangle(0, 0, 16, 16), Color.FromArgb(255, 90, 60), Color.FromArgb(60, 140, 255), 45f))
                    {
                        g.FillEllipse(brush, 1, 1, 14, 14);
                    }
                    g.DrawEllipse(Pens.Black, 1, 1, 14, 14);
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        // ---------- hotkeys ----------

        private void RegisterHotkeys()
        {
            UnregisterHotkeys();

            for (int i = 0; i < _config.Profiles.Count; i++)
            {
                Profile p = _config.Profiles[i];
                if (!p.HasHotkey) continue;
                if (RegisterHotKey(Handle, i, p.HotkeyModifiers | MOD_NOREPEAT, p.HotkeyKey))
                    _registered.Add(i);
                else
                    Config.Log("Hotkey registration failed for profile '" + p.Name + "' (probably already taken by another app)");
            }

            if (_config.ResetHotkeyKey != 0)
            {
                if (RegisterHotKey(Handle, ResetHotkeyId, _config.ResetHotkeyModifiers | MOD_NOREPEAT, _config.ResetHotkeyKey))
                    _registered.Add(ResetHotkeyId);
                else
                    Config.Log("Reset hotkey registration failed (probably already taken by another app)");
            }
        }

        private void UnregisterHotkeys()
        {
            foreach (int id in _registered) UnregisterHotKey(Handle, id);
            _registered.Clear();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY)
            {
                int id = m.WParam.ToInt32();
                if (id == ResetHotkeyId) ApplyAndNotify(null);
                else if (id >= 0 && id < _config.Profiles.Count) ApplyAndNotify(_config.Profiles[id]);
                return;
            }

            if (m.Msg == WM_COPYDATA)
            {
                string name = Applier.DecodeCopyData(m.LParam);
                Profile p = string.IsNullOrEmpty(name) ? null : _config.Find(name);
                try { Applier.ApplyLocal(p, true); }
                catch (Exception ex) { Config.Log("ERROR applying '" + name + "': " + ex.Message); }
                m.Result = (IntPtr)1;
                return;
            }

            base.WndProc(ref m);
        }

        // ---------- applying ----------

        private void ApplyAndNotify(Profile p)
        {
            try
            {
                Applier.ApplyLocal(p, true);
                _icon.Text = p == null ? "Display Profile Switcher" : "Display Profile Switcher - " + p.Name;
            }
            catch (Exception ex)
            {
                Config.Log("ERROR: " + ex.Message);
                _icon.ShowBalloonTip(3000, "Display Profile Switcher", ex.Message, ToolTipIcon.Error);
            }
        }

        /// <summary>Re-assert whatever profile was last active, so saturation returns after a restart.</summary>
        private void ReapplyActive()
        {
            string active = Config.ReadActiveProfileName();
            if (string.IsNullOrEmpty(active)) return;
            Profile p = _config.Find(active);
            if (p == null) return;
            try { Applier.ApplyLocal(p, true); }
            catch (Exception ex) { Config.Log("ERROR reapplying '" + active + "': " + ex.Message); }
        }

        // ---------- menu ----------

        private void OnIconMouseUp(object sender, MouseEventArgs e)
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            string active = Config.ReadActiveProfileName();

            foreach (Profile p in _config.Profiles)
            {
                Profile captured = p;
                string hk = HotkeyText.Describe(p.HotkeyModifiers, p.HotkeyKey);
                ToolStripMenuItem item = new ToolStripMenuItem(hk.Length > 0 ? p.Name + "   (" + hk + ")" : p.Name);
                item.Checked = string.Equals(active, p.Name, StringComparison.OrdinalIgnoreCase);
                item.Click += delegate { ApplyAndNotify(captured); };
                menu.Items.Add(item);
            }

            if (_config.Profiles.Count > 0) menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem reset = new ToolStripMenuItem("Reset to default");
            reset.Click += delegate { ApplyAndNotify(null); };
            menu.Items.Add(reset);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem settings = new ToolStripMenuItem("Settings...");
            settings.Click += delegate { OpenSettings(); };
            menu.Items.Add(settings);

            // Reachable after the fact: Equalizer APO needs a reboot, by which point the
            // installer has long finished, so the audio side often has to be resumed later.
            ToolStripMenuItem audio = new ToolStripMenuItem("Audio setup...");
            audio.Click += delegate { RunAudioSetup(); };
            menu.Items.Add(audio);

            ToolStripMenuItem exit = new ToolStripMenuItem("Exit");
            exit.Click += delegate { ExitApp(); };
            menu.Items.Add(exit);

            // Tray menus need their owner in the foreground, otherwise focus handling
            // (and dismissing the menu by clicking away) misbehaves.
            SetForegroundWindow(Handle);
            menu.Show(Cursor.Position);
        }

        private void OpenSettings()
        {
            if (_openConfig != null && !_openConfig.IsDisposed)
            {
                if (_openConfig.WindowState == FormWindowState.Minimized)
                    _openConfig.WindowState = FormWindowState.Normal;
                _openConfig.Activate();
                _openConfig.BringToFront();
                SetForegroundWindow(_openConfig.Handle);
                return;
            }

            _openConfig = new ConfigForm(_config);
            _openConfig.FormClosed += delegate
            {
                if (_openConfig.DialogResult == DialogResult.OK)
                {
                    bool rulesChanged = _openConfig.RulesChanged;
                    List<AppRule> previousRules = new List<AppRule>();
                    foreach (AppRule r in _config.Rules) previousRules.Add(r.Clone());

                    _config = _openConfig.ResultConfig;
                    _config.Save();
                    RegisterHotkeys();

                    // The WMI watch list lives outside this process and needs admin to rebuild.
                    // Roll the rules back if that does not actually happen, so what the UI shows
                    // and what the system watches can never drift apart.
                    if (rulesChanged && !RewatchElevated())
                    {
                        _config.Rules = previousRules;
                        _config.Save();
                        MessageBox.Show(
                            "The watched application list was left unchanged because administrator "
                            + "rights were not granted.\n\nYour profile changes were saved.",
                            "Display Profile Switcher", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
                else
                {
                    // Discard preview changes by restoring the previously active profile.
                    ReapplyActive();
                }
                _openConfig = null;
            };
            _openConfig.Show();
            _openConfig.Activate();
            _openConfig.BringToFront();
            SetForegroundWindow(_openConfig.Handle);
        }

        private void RunAudioSetup()
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(
                    System.Reflection.Assembly.GetExecutingAssembly().Location, "-AudioSetup");
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                Process.Start(psi);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                Config.Log("Audio setup elevation declined.");
            }
            catch (Exception ex)
            {
                Config.Log("Audio setup could not start: " + ex.Message);
            }
        }

        /// <summary>Rebuilds the WMI watch list elevated. Returns false if that did not succeed.</summary>
        private bool RewatchElevated()
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(
                    System.Reflection.Assembly.GetExecutingAssembly().Location, "-Rewatch");
                psi.UseShellExecute = true;
                psi.Verb = "runas";

                using (Process p = Process.Start(psi))
                {
                    p.WaitForExit();
                    if (p.ExitCode != 0) Config.Log("Rewatch failed with exit code " + p.ExitCode);
                    return p.ExitCode == 0;
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                Config.Log("Rewatch elevation declined by the user.");
                return false;
            }
            catch (Exception ex)
            {
                Config.Log("Rewatch could not start: " + ex.Message);
                return false;
            }
        }

        private void ExitApp()
        {
            UnregisterHotkeys();
            Saturation.Shutdown();
            _icon.Visible = false;
            Application.Exit();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                UnregisterHotkeys();
                if (_apoWatcher != null) { _apoWatcher.EnableRaisingEvents = false; _apoWatcher.Dispose(); }
                if (_apoDebounce != null) _apoDebounce.Dispose();
                if (_icon != null) { _icon.Visible = false; _icon.Dispose(); }
            }
            base.Dispose(disposing);
        }
    }
}
