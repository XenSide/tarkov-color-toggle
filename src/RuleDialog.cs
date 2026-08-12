using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace TarkovColor
{
    /// <summary>Small editor for a single "while this app runs, use this profile" rule.</summary>
    public class RuleDialog : Form
    {
        public AppRule Result { get; private set; }

        private readonly Config _config;
        private TextBox _txtProcess;
        private ComboBox _cmbProfile;

        public RuleDialog(Config config, AppRule existing)
        {
            _config = config;

            Text = existing == null ? "Add application" : "Edit application";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            ClientSize = new Size(430, 172);
            Font = SystemFonts.MessageBoxFont;

            Label l1 = new Label();
            l1.Text = "Executable name";
            l1.SetBounds(14, 16, 120, 18);
            Controls.Add(l1);

            _txtProcess = new TextBox();
            _txtProcess.SetBounds(14, 36, 300, 22);
            Controls.Add(_txtProcess);

            Button browse = new Button();
            browse.Text = "Browse...";
            browse.SetBounds(322, 35, 92, 25);
            browse.Click += delegate { Browse(); };
            Controls.Add(browse);

            Label hint = new Label();
            hint.Text = "Just the file name, for example EscapeFromTarkov.exe";
            hint.SetBounds(14, 62, 400, 18);
            hint.ForeColor = SystemColors.GrayText;
            Controls.Add(hint);

            Label l2 = new Label();
            l2.Text = "Profile to apply";
            l2.SetBounds(14, 90, 120, 18);
            Controls.Add(l2);

            _cmbProfile = new ComboBox();
            _cmbProfile.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbProfile.SetBounds(14, 110, 300, 22);
            foreach (Profile p in _config.Profiles) _cmbProfile.Items.Add(p.Name);
            Controls.Add(_cmbProfile);

            Button ok = new Button();
            ok.Text = "OK";
            ok.SetBounds(236, 138, 88, 26);
            ok.Click += delegate { OnOk(); };
            Controls.Add(ok);

            Button cancel = new Button();
            cancel.Text = "Cancel";
            cancel.SetBounds(330, 138, 88, 26);
            cancel.DialogResult = DialogResult.Cancel;
            Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;

            if (existing != null)
            {
                _txtProcess.Text = existing.ProcessName;
                int idx = _cmbProfile.Items.IndexOf(existing.ProfileName);
                _cmbProfile.SelectedIndex = idx >= 0 ? idx : (_cmbProfile.Items.Count > 0 ? 0 : -1);
            }
            else if (_cmbProfile.Items.Count > 0)
            {
                _cmbProfile.SelectedIndex = 0;
            }
        }

        private void Browse()
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Filter = "Programs (*.exe)|*.exe|All files (*.*)|*.*";
                dlg.Title = "Pick the game or application";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                _txtProcess.Text = Path.GetFileName(dlg.FileName);
            }
        }

        private void OnOk()
        {
            string process = (_txtProcess.Text ?? "").Trim();
            if (process.Length == 0)
            {
                MessageBox.Show("Enter the executable name.", "Display Profile Switcher",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (process.IndexOf('\'') >= 0 || process.IndexOf('"') >= 0 || process.IndexOf('\\') >= 0)
            {
                MessageBox.Show("Use only the file name, without quotes or a path.", "Display Profile Switcher",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (!process.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) process += ".exe";

            if (_cmbProfile.SelectedIndex < 0)
            {
                MessageBox.Show("Pick a profile.", "Display Profile Switcher",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Result = new AppRule(process, (string)_cmbProfile.SelectedItem);
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
