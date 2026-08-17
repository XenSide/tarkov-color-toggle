using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace TarkovColor
{
    /// <summary>Profile editor. Edits a copy; the caller keeps the original unless OK is pressed.</summary>
    public class ConfigForm : Form
    {
        public Config ResultConfig { get { return _config; } }

        private Config _config;
        private bool _loading;

        /// <summary>True when the watched-application list changed and WMI needs re-registering.</summary>
        public bool RulesChanged { get; private set; }

        private readonly string _originalRules;

        private ListBox _list;
        private ListView _rules;
        private Button _btnAdd, _btnDup, _btnDel, _btnNeutral, _btnClearHotkey, _btnClearReset, _btnBrowse;
        private Button _btnRuleAdd, _btnRuleEdit, _btnRuleDel;
        private TextBox _txtName, _txtHotkey, _txtResetHotkey;
        private ComboBox _cmbIcc, _cmbAudio;
        private Label _lblAudioNote;
        private TrackBar _tbGamma, _tbContrast, _tbBrightness, _tbSaturation, _tbVibrance;
        private Label _lblGamma, _lblContrast, _lblBrightness, _lblSaturation, _lblVibrance, _lblVibranceNote;

        public ConfigForm(Config source)
        {
            _config = Clone(source);
            _originalRules = DescribeRules(_config);
            BuildUi();
            ReloadList(0);
            ReloadRules();
        }

        private static Config Clone(Config src)
        {
            Config c = new Config();
            c.ResetHotkeyModifiers = src.ResetHotkeyModifiers;
            c.ResetHotkeyKey = src.ResetHotkeyKey;
            foreach (Profile p in src.Profiles) c.Profiles.Add(p.Clone());
            foreach (AppRule r in src.Rules) c.Rules.Add(r.Clone());
            return c;
        }

        /// <summary>Canonical form used to detect whether the watch list needs re-registering.</summary>
        private static string DescribeRules(Config c)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (AppRule r in c.Rules)
            {
                sb.Append(r.ProcessName == null ? "" : r.ProcessName.ToLowerInvariant());
                sb.Append('|');
            }
            return sb.ToString();
        }

        private Profile Current
        {
            get
            {
                int i = _list.SelectedIndex;
                if (i < 0 || i >= _config.Profiles.Count) return null;
                return _config.Profiles[i];
            }
        }

        // ---------- UI construction ----------

        private void BuildUi()
        {
            Text = "Display Profile Switcher - Settings";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(660, 604);
            Font = SystemFonts.MessageBoxFont;

            // --- left: profile list ---
            _list = new ListBox();
            _list.SetBounds(12, 12, 180, 300);
            _list.SelectedIndexChanged += delegate { LoadCurrentIntoControls(); Preview(); };
            Controls.Add(_list);

            _btnAdd = MakeButton("Add", 12, 318, 58);
            _btnAdd.Click += delegate { AddProfile(); };
            _btnDup = MakeButton("Copy", 73, 318, 58);
            _btnDup.Click += delegate { DuplicateProfile(); };
            _btnDel = MakeButton("Delete", 134, 318, 58);
            _btnDel.Click += delegate { DeleteProfile(); };

            // --- right: editor ---
            int x = 210, y = 14;

            Controls.Add(MakeLabel("Name", x, y + 3, 70));
            _txtName = new TextBox();
            _txtName.SetBounds(x + 78, y, 250, 22);
            _txtName.TextChanged += delegate
            {
                if (_loading || Current == null) return;

                // Model only. Rewriting the ListBox item here would make the list steal
                // focus on every keystroke, so the visible lists refresh on Leave instead.
                string oldName = Current.Name;
                Current.Name = _txtName.Text;
                foreach (AppRule r in _config.Rules)
                {
                    if (string.Equals(r.ProfileName, oldName, StringComparison.OrdinalIgnoreCase))
                        r.ProfileName = _txtName.Text;
                }
            };
            _txtName.Leave += delegate { RefreshNameInLists(); };
            Controls.Add(_txtName);
            y += 32;

            Controls.Add(MakeLabel("ICC base", x, y + 3, 70));
            _cmbIcc = new ComboBox();
            _cmbIcc.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbIcc.SetBounds(x + 78, y, 250, 22);
            _cmbIcc.SelectedIndexChanged += delegate
            {
                if (_loading || Current == null) return;
                Current.IccFile = _cmbIcc.SelectedIndex <= 0 ? null : (string)_cmbIcc.SelectedItem;
                Preview();
            };
            Controls.Add(_cmbIcc);

            _btnBrowse = MakeButton("Add .icc...", x + 334, y - 1, 90);
            _btnBrowse.Click += delegate { BrowseIcc(); };
            y += 32;

            Controls.Add(MakeLabel("Audio EQ", x, y + 3, 70));
            _cmbAudio = new ComboBox();
            _cmbAudio.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbAudio.SetBounds(x + 78, y, 250, 22);
            _cmbAudio.SelectedIndexChanged += delegate
            {
                if (_loading || Current == null) return;
                Current.AudioFile = _cmbAudio.SelectedIndex <= 0 ? null : (string)_cmbAudio.SelectedItem;
                try { AudioProfile.Apply(Current); }
                catch (Exception ex) { Config.Log("Audio preview failed: " + ex.Message); }
            };
            Controls.Add(_cmbAudio);

            _lblAudioNote = MakeLabel("", x + 334, y + 3, 200);
            _lblAudioNote.ForeColor = SystemColors.GrayText;
            y += 38;

            _tbGamma = MakeSlider(x, ref y, "Gamma", 30, 300, out _lblGamma);
            _tbContrast = MakeSlider(x, ref y, "Contrast", 50, 300, out _lblContrast);
            _tbBrightness = MakeSlider(x, ref y, "Brightness", -50, 50, out _lblBrightness);
            _tbSaturation = MakeSlider(x, ref y, "Saturation", 0, 300, out _lblSaturation);
            _tbVibrance = MakeSlider(x, ref y, "Vibrance", Vibrance.MinLevel, Vibrance.MaxLevel, out _lblVibrance);

            _lblVibranceNote = MakeLabel("", x + 76, y, 380);
            _lblVibranceNote.ForeColor = SystemColors.GrayText;
            _lblVibranceNote.Text = Vibrance.IsAvailable
                ? "NVIDIA vibrance - adds saturation with no presentation cost."
                : "NVIDIA vibrance unavailable on this system.";
            _tbVibrance.Enabled = Vibrance.IsAvailable;
            y += 22;

            y += 10;
            Controls.Add(MakeLabel("Hotkey", x, y + 3, 70));
            _txtHotkey = new TextBox();
            _txtHotkey.SetBounds(x + 78, y, 160, 22);
            _txtHotkey.ReadOnly = true;
            _txtHotkey.BackColor = SystemColors.Window;
            _txtHotkey.KeyDown += OnHotkeyKeyDown;
            Controls.Add(_txtHotkey);

            _btnClearHotkey = MakeButton("Clear", x + 244, y - 1, 60);
            _btnClearHotkey.Click += delegate
            {
                if (Current == null) return;
                Current.HotkeyModifiers = 0;
                Current.HotkeyKey = 0;
                _txtHotkey.Text = "";
            };

            _btnNeutral = MakeButton("Reset sliders", x + 310, y - 1, 100);
            _btnNeutral.Click += delegate
            {
                if (Current == null) return;
                Current.Gamma = 1.0; Current.Contrast = 1.0;
                Current.Brightness = 0.0; Current.Saturation = 1.0; Current.Vibrance = 0;
                LoadCurrentIntoControls();
                Preview();
            };
            y += 40;

            // --- bottom: global settings ---
            Panel divider = new Panel();
            divider.SetBounds(12, 370, 636, 1);
            divider.BackColor = SystemColors.ControlDark;
            Controls.Add(divider);

            Label rulesCaption = MakeLabel("Apply a profile automatically while these applications run", 12, 378, 400);
            rulesCaption.Font = new Font(Font, FontStyle.Bold);

            _rules = new ListView();
            _rules.View = View.Details;
            _rules.FullRowSelect = true;
            _rules.MultiSelect = false;
            _rules.HideSelection = false;
            _rules.SetBounds(12, 400, 476, 104);
            _rules.Columns.Add("Application", 230);
            _rules.Columns.Add("Profile", 222);
            _rules.DoubleClick += delegate { EditRule(); };
            Controls.Add(_rules);

            _btnRuleAdd = MakeButton("Add...", 496, 400, 90);
            _btnRuleAdd.Click += delegate { AddRule(); };
            _btnRuleEdit = MakeButton("Edit...", 496, 432, 90);
            _btnRuleEdit.Click += delegate { EditRule(); };
            _btnRuleDel = MakeButton("Remove", 496, 464, 90);
            _btnRuleDel.Click += delegate { RemoveRule(); };

            Panel divider2 = new Panel();
            divider2.SetBounds(12, 518, 636, 1);
            divider2.BackColor = SystemColors.ControlDark;
            Controls.Add(divider2);

            Controls.Add(MakeLabel("Reset hotkey", 12, 532, 80));
            _txtResetHotkey = new TextBox();
            _txtResetHotkey.SetBounds(94, 529, 110, 22);
            _txtResetHotkey.ReadOnly = true;
            _txtResetHotkey.BackColor = SystemColors.Window;
            _txtResetHotkey.KeyDown += OnResetHotkeyKeyDown;
            Controls.Add(_txtResetHotkey);

            _btnClearReset = MakeButton("X", 210, 528, 28);
            _btnClearReset.Click += delegate
            {
                _config.ResetHotkeyModifiers = 0;
                _config.ResetHotkeyKey = 0;
                _txtResetHotkey.Text = "";
            };

            Label hint = MakeLabel(
                "Changes preview live on the primary monitor. Saturation needs the tray running,\n"
                + "and costs hardware presentation in games. Leave it at 1.00 unless you want it.",
                12, 556, 440);
            hint.Height = 42;
            hint.ForeColor = SystemColors.GrayText;
            Controls.Add(hint);

            Button ok = MakeButton("OK", 466, 560, 88);
            ok.Click += delegate { OnOk(); };
            Button cancel = MakeButton("Cancel", 560, 560, 88);
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            AcceptButton = ok;
            CancelButton = cancel;
        }

        private void OnOk()
        {
            RulesChanged = DescribeRules(_config) != _originalRules;
            DialogResult = DialogResult.OK;
            Close();
        }

        private Button MakeButton(string text, int x, int y, int w)
        {
            Button b = new Button();
            b.Text = text;
            b.SetBounds(x, y, w, 26);
            Controls.Add(b);
            return b;
        }

        private Label MakeLabel(string text, int x, int y, int w)
        {
            Label l = new Label();
            l.Text = text;
            l.SetBounds(x, y, w, 18);
            l.AutoSize = false;
            Controls.Add(l);
            return l;
        }

        private TrackBar MakeSlider(int x, ref int y, string caption, int min, int max, out Label valueLabel)
        {
            Controls.Add(MakeLabel(caption, x, y + 4, 70));

            TrackBar tb = new TrackBar();
            // TrackBar auto-sizes to a taller default and would overlap the row below.
            tb.AutoSize = false;
            tb.Minimum = min;
            tb.Maximum = max;
            tb.TickStyle = TickStyle.None;
            tb.SetBounds(x + 76, y, 290, 28);
            Controls.Add(tb);

            valueLabel = MakeLabel("", x + 372, y + 6, 60);
            tb.Scroll += delegate { OnSliderChanged(); };

            y += 34;
            return tb;
        }

        // ---------- data binding ----------

        private void ReloadList(int selectIndex)
        {
            _loading = true;
            _list.Items.Clear();
            foreach (Profile p in _config.Profiles) _list.Items.Add(p.Name);
            _loading = false;

            if (_config.Profiles.Count > 0)
                _list.SelectedIndex = Math.Max(0, Math.Min(selectIndex, _config.Profiles.Count - 1));
            else
                LoadCurrentIntoControls();

            ReloadRules();
        }

        /// <summary>Pushes a finished rename into the profile list and the rules list.</summary>
        private void RefreshNameInLists()
        {
            if (_loading || Current == null) return;
            int i = _list.SelectedIndex;
            if (i >= 0 && i < _list.Items.Count && !Equals(_list.Items[i], Current.Name))
            {
                _loading = true;
                _list.Items[i] = Current.Name;
                _list.SelectedIndex = i;
                _loading = false;
            }
            ReloadRules();
        }

        private void ReloadRules()
        {
            if (_rules == null) return;
            _rules.Items.Clear();
            foreach (AppRule r in _config.Rules)
            {
                ListViewItem item = new ListViewItem(r.ProcessName);
                bool missing = _config.Find(r.ProfileName) == null;
                item.SubItems.Add(missing ? r.ProfileName + "  (missing)" : r.ProfileName);
                if (missing) item.ForeColor = Color.Firebrick;
                _rules.Items.Add(item);
            }
        }

        private AppRule SelectedRule
        {
            get
            {
                if (_rules == null || _rules.SelectedIndices.Count == 0) return null;
                int i = _rules.SelectedIndices[0];
                if (i < 0 || i >= _config.Rules.Count) return null;
                return _config.Rules[i];
            }
        }

        private void AddRule()
        {
            if (_config.Profiles.Count == 0)
            {
                MessageBox.Show("Create a profile first.", "Display Profile Switcher",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (RuleDialog dlg = new RuleDialog(_config, null))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                if (RejectDuplicate(dlg.Result.ProcessName, null)) return;
                _config.Rules.Add(dlg.Result);
                ReloadRules();
                SelectRuleRow(_config.Rules.Count - 1);
            }
        }

        private void EditRule()
        {
            AppRule r = SelectedRule;
            if (r == null) return;
            int idx = _config.Rules.IndexOf(r);

            using (RuleDialog dlg = new RuleDialog(_config, r))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                if (RejectDuplicate(dlg.Result.ProcessName, r)) return;
                _config.Rules[idx] = dlg.Result;
                ReloadRules();
                SelectRuleRow(idx);
            }
        }

        private void RemoveRule()
        {
            AppRule r = SelectedRule;
            if (r == null) return;

            // Remove this exact entry, not "the one at that index", so the list and the
            // model cannot get out of step.
            int idx = _config.Rules.IndexOf(r);
            if (idx < 0) return;
            _config.Rules.RemoveAt(idx);
            ReloadRules();
            SelectRuleRow(Math.Min(idx, _config.Rules.Count - 1));
        }

        /// <summary>
        /// One executable can only map to one profile: two rules for the same process would
        /// be ambiguous, and the watcher would only ever honour the first.
        /// </summary>
        private bool RejectDuplicate(string processName, AppRule ignore)
        {
            foreach (AppRule other in _config.Rules)
            {
                if (other == ignore) continue;
                if (string.Equals(other.ProcessName, processName, StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show(
                        processName + " is already mapped to \"" + other.ProfileName + "\".\n\n"
                        + "Edit that entry instead, or remove it first.",
                        "Display Profile Switcher", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return true;
                }
            }
            return false;
        }

        private void SelectRuleRow(int index)
        {
            if (index < 0 || index >= _rules.Items.Count) return;
            _rules.Items[index].Selected = true;
            _rules.Items[index].Focused = true;
        }

        private void RefreshIccCombo(Profile p)
        {
            _cmbIcc.Items.Clear();
            _cmbIcc.Items.Add("(none - linear)");

            List<string> files = new List<string>();
            try
            {
                foreach (string f in Directory.GetFiles(Config.AppDir, "*.icc")) files.Add(Path.GetFileName(f));
                foreach (string f in Directory.GetFiles(Config.AppDir, "*.icm")) files.Add(Path.GetFileName(f));
            }
            catch { }

            if (p != null && !string.IsNullOrEmpty(p.IccFile) && !files.Contains(p.IccFile)) files.Add(p.IccFile);
            foreach (string f in files) _cmbIcc.Items.Add(f);

            int sel = 0;
            if (p != null && !string.IsNullOrEmpty(p.IccFile))
            {
                for (int i = 1; i < _cmbIcc.Items.Count; i++)
                {
                    if (string.Equals((string)_cmbIcc.Items[i], p.IccFile, StringComparison.OrdinalIgnoreCase))
                    {
                        sel = i;
                        break;
                    }
                }
            }
            _cmbIcc.SelectedIndex = sel;
        }

        private void RefreshAudioCombo(Profile p)
        {
            _cmbAudio.Items.Clear();
            _cmbAudio.Items.Add("(none)");

            string[] presets = AudioProfile.ListPresets();
            foreach (string f in presets) _cmbAudio.Items.Add(f);

            if (p != null && !string.IsNullOrEmpty(p.AudioFile))
            {
                bool known = false;
                foreach (string f in presets)
                {
                    if (string.Equals(f, p.AudioFile, StringComparison.OrdinalIgnoreCase)) { known = true; break; }
                }
                if (!known) _cmbAudio.Items.Add(p.AudioFile);
            }

            int sel = 0;
            if (p != null && !string.IsNullOrEmpty(p.AudioFile))
            {
                for (int i = 1; i < _cmbAudio.Items.Count; i++)
                {
                    if (string.Equals((string)_cmbAudio.Items[i], p.AudioFile, StringComparison.OrdinalIgnoreCase))
                    {
                        sel = i;
                        break;
                    }
                }
            }
            _cmbAudio.SelectedIndex = sel;

            bool apo = AudioProfile.IsAvailable;
            _cmbAudio.Enabled = apo && p != null;
            _lblAudioNote.Text = apo ? "" : "Equalizer APO not installed";
        }

        private void LoadCurrentIntoControls()
        {
            _loading = true;
            Profile p = Current;
            bool has = p != null;

            _txtName.Enabled = has;
            _cmbIcc.Enabled = has;
            _cmbAudio.Enabled = has && AudioProfile.IsAvailable;
            _btnBrowse.Enabled = has;
            _tbGamma.Enabled = has;
            _tbContrast.Enabled = has;
            _tbBrightness.Enabled = has;
            _tbSaturation.Enabled = has;
            _tbVibrance.Enabled = has && Vibrance.IsAvailable;
            _txtHotkey.Enabled = has;
            _btnClearHotkey.Enabled = has;
            _btnNeutral.Enabled = has;
            _btnDup.Enabled = has;
            _btnDel.Enabled = has;

            if (!has)
            {
                _txtName.Text = "";
                _txtHotkey.Text = "";
                _cmbIcc.Items.Clear();
                _cmbAudio.Items.Clear();
                _loading = false;
                return;
            }

            _txtName.Text = p.Name;
            RefreshIccCombo(p);
            RefreshAudioCombo(p);
            _tbGamma.Value = ClampInt((int)Math.Round(p.Gamma * 100), _tbGamma.Minimum, _tbGamma.Maximum);
            _tbContrast.Value = ClampInt((int)Math.Round(p.Contrast * 100), _tbContrast.Minimum, _tbContrast.Maximum);
            _tbBrightness.Value = ClampInt((int)Math.Round(p.Brightness * 100), _tbBrightness.Minimum, _tbBrightness.Maximum);
            _tbSaturation.Value = ClampInt((int)Math.Round(p.Saturation * 100), _tbSaturation.Minimum, _tbSaturation.Maximum);
            _tbVibrance.Value = ClampInt(p.Vibrance, _tbVibrance.Minimum, _tbVibrance.Maximum);
            _txtHotkey.Text = HotkeyText.Describe(p.HotkeyModifiers, p.HotkeyKey);
            _txtResetHotkey.Text = HotkeyText.Describe(_config.ResetHotkeyModifiers, _config.ResetHotkeyKey);

            _loading = false;
            UpdateSliderLabels();
        }

        private static int ClampInt(int v, int lo, int hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }

        private void OnSliderChanged()
        {
            if (_loading || Current == null) return;
            Profile p = Current;
            p.Gamma = _tbGamma.Value / 100.0;
            p.Contrast = _tbContrast.Value / 100.0;
            p.Brightness = _tbBrightness.Value / 100.0;
            p.Saturation = _tbSaturation.Value / 100.0;
            p.Vibrance = _tbVibrance.Value;
            UpdateSliderLabels();
            Preview();
        }

        private void UpdateSliderLabels()
        {
            _lblGamma.Text = (_tbGamma.Value / 100.0).ToString("0.00");
            _lblContrast.Text = (_tbContrast.Value / 100.0).ToString("0.00");
            _lblBrightness.Text = (_tbBrightness.Value / 100.0).ToString("+0.00;-0.00;0.00");
            _lblSaturation.Text = (_tbSaturation.Value / 100.0).ToString("0.00");
            _lblVibrance.Text = _tbVibrance.Value.ToString();
        }

        private void Preview()
        {
            Profile p = Current;
            if (p == null) return;
            try
            {
                Ramp.Preview(p);
                Saturation.Apply(p.Saturation);
                Vibrance.SetLevel(p.Vibrance);
            }
            catch (Exception ex)
            {
                Config.Log("Preview failed: " + ex.Message);
            }
        }

        // ---------- actions ----------

        private void AddProfile()
        {
            Profile p = new Profile();
            p.Name = UniqueName("New profile");
            p.AudioFile = DefaultAudioPreset();
            _config.Profiles.Add(p);
            ReloadList(_config.Profiles.Count - 1);
        }

        /// <summary>
        /// New profiles start with an EQ rather than none: leaving the field empty is the
        /// easiest way to end up with a profile that silently does nothing to the sound.
        /// </summary>
        private static string DefaultAudioPreset()
        {
            string[] presets = AudioProfile.ListPresets();
            if (presets.Length == 0) return null;

            foreach (string f in presets)
            {
                if (string.Equals(f, "tarkov-full.txt", StringComparison.OrdinalIgnoreCase)) return f;
            }
            return presets[0];
        }

        private void DuplicateProfile()
        {
            Profile p = Current;
            if (p == null) return;
            Profile copy = p.Clone();
            copy.Name = UniqueName(p.Name + " copy");
            copy.HotkeyModifiers = 0;
            copy.HotkeyKey = 0;
            _config.Profiles.Add(copy);
            ReloadList(_config.Profiles.Count - 1);
        }

        private void DeleteProfile()
        {
            Profile p = Current;
            if (p == null) return;
            if (MessageBox.Show("Delete profile \"" + p.Name + "\"?", "Confirm",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

            int idx = _list.SelectedIndex;
            _config.Profiles.RemoveAt(idx);
            ReloadList(Math.Max(0, idx - 1));
        }

        private string UniqueName(string basis)
        {
            string candidate = basis;
            int n = 2;
            while (_config.Find(candidate) != null)
            {
                candidate = basis + " " + n;
                n++;
            }
            return candidate;
        }

        private void BrowseIcc()
        {
            if (Current == null) return;
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Filter = "Colour profiles (*.icc;*.icm)|*.icc;*.icm|All files (*.*)|*.*";
                dlg.Title = "Pick a colour profile";
                string sysColor = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "spool\\drivers\\color");
                if (Directory.Exists(sysColor)) dlg.InitialDirectory = sysColor;

                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                string fileName = Path.GetFileName(dlg.FileName);
                string dest = Path.Combine(Config.AppDir, fileName);
                try
                {
                    // Copy next to the exe so profiles.json only needs the file name and the
                    // whole folder stays portable.
                    if (!string.Equals(Path.GetFullPath(dlg.FileName), Path.GetFullPath(dest),
                            StringComparison.OrdinalIgnoreCase))
                        File.Copy(dlg.FileName, dest, true);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Could not copy the profile next to the app:\n" + ex.Message,
                        "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                Current.IccFile = fileName;
                _loading = true;
                RefreshIccCombo(Current);
                _loading = false;
                Preview();
            }
        }

        private void OnHotkeyKeyDown(object sender, KeyEventArgs e)
        {
            e.SuppressKeyPress = true;
            e.Handled = true;
            if (Current == null) return;

            if (e.KeyCode == Keys.Back || e.KeyCode == Keys.Delete)
            {
                Current.HotkeyModifiers = 0;
                Current.HotkeyKey = 0;
                _txtHotkey.Text = "";
                return;
            }

            uint mods, key;
            HotkeyText.FromKeyEvent(e, out mods, out key);
            if (key == 0) return;

            Current.HotkeyModifiers = mods;
            Current.HotkeyKey = key;
            _txtHotkey.Text = HotkeyText.Describe(mods, key);
        }

        private void OnResetHotkeyKeyDown(object sender, KeyEventArgs e)
        {
            e.SuppressKeyPress = true;
            e.Handled = true;

            if (e.KeyCode == Keys.Back || e.KeyCode == Keys.Delete)
            {
                _config.ResetHotkeyModifiers = 0;
                _config.ResetHotkeyKey = 0;
                _txtResetHotkey.Text = "";
                return;
            }

            uint mods, key;
            HotkeyText.FromKeyEvent(e, out mods, out key);
            if (key == 0) return;

            _config.ResetHotkeyModifiers = mods;
            _config.ResetHotkeyKey = key;
            _txtResetHotkey.Text = HotkeyText.Describe(mods, key);
        }
    }
}
