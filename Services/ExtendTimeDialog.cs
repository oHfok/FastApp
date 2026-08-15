using CommunityToolkit.Mvvm.Messaging;
using FastApp.ViewModels;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace FastApp.Services
{
    // A small native dialog for extending an app's daily limit, reachable from
    // the tray icon menu without needing a browser at all. The web dashboard has
    // the same "Extend Today" flow, but relying on it exclusively has an obvious
    // hole: if browser access is restricted (or the limited app IS the browser),
    // a browser-only unlock is useless exactly when it'd actually be needed.
    // Built as a plain WinForms Form (like the rest of the tray/notification
    // infrastructure already is) rather than WPF/XAML.
    public class ExtendTimeDialog : Form
    {
        private readonly MainViewModel _viewModel;
        private ComboBox _appCombo;
        private ComboBox _minutesCombo;
        private TextBox _pinBox;

        public ExtendTimeDialog(MainViewModel viewModel)
        {
            _viewModel = viewModel;

            Text = "Extend App Time";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            TopMost = true;

            var limitedApps = _viewModel.ManagedApps
                .Where(a => a.DailyLimitMinutes > 0)
                .Select(a => a.Name)
                .ToList();

            if (limitedApps.Count == 0)
            {
                ClientSize = new Size(320, 140);

                var noAppsLabel = new Label
                {
                    Text = "No apps currently have a daily limit set.",
                    Location = new Point(16, 16),
                    Size = new Size(288, 50),
                    ForeColor = Color.Gray
                };
                var closeOnlyButton = new Button { Text = "Close", Location = new Point(16, 90), Width = 288 };
                closeOnlyButton.Click += (s, e) => Close();

                Controls.Add(noAppsLabel);
                Controls.Add(closeOnlyButton);
                AcceptButton = closeOnlyButton;
                return;
            }

            ClientSize = new Size(320, 264);

            var introLabel = new Label
            {
                Text = "Grant extra time for today only — tomorrow's limit is unaffected.",
                Location = new Point(16, 16),
                Size = new Size(288, 32),
                ForeColor = Color.Gray
            };

            var appLabel = new Label { Text = "App", Location = new Point(16, 56), AutoSize = true };
            _appCombo = new ComboBox { Location = new Point(16, 76), Width = 288, DropDownStyle = ComboBoxStyle.DropDownList };
            _appCombo.Items.AddRange(limitedApps.ToArray());
            _appCombo.SelectedIndex = 0;

            var minutesLabel = new Label { Text = "Extra minutes", Location = new Point(16, 112), AutoSize = true };
            _minutesCombo = new ComboBox { Location = new Point(16, 132), Width = 288, DropDownStyle = ComboBoxStyle.DropDownList };
            _minutesCombo.Items.AddRange(new object[] { "10", "15", "30", "60" });
            _minutesCombo.SelectedIndex = 1;

            var pinLabel = new Label { Text = "PIN", Location = new Point(16, 168), AutoSize = true };
            _pinBox = new TextBox { Location = new Point(16, 188), Width = 288, UseSystemPasswordChar = true };
            _pinBox.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) Confirm(); };

            var confirmButton = new Button { Text = "Extend", Location = new Point(16, 224), Width = 288 };
            confirmButton.Click += (s, e) => Confirm();

            Controls.Add(introLabel);
            Controls.Add(appLabel);
            Controls.Add(_appCombo);
            Controls.Add(minutesLabel);
            Controls.Add(_minutesCombo);
            Controls.Add(pinLabel);
            Controls.Add(_pinBox);
            Controls.Add(confirmButton);

            AcceptButton = confirmButton;
        }

        private void Confirm()
        {
            string appName = _appCombo.SelectedItem?.ToString();
            if (string.IsNullOrEmpty(appName)) return;

            int extraMinutes = int.Parse(_minutesCombo.SelectedItem.ToString());
            string pin = _pinBox.Text;

            if (string.IsNullOrEmpty(pin))
            {
                MessageBox.Show(this, "Enter your PIN.", "Extend App Time", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            using var db = new AppDbContext();
            var (hasPin, salt, hash) = PinService.GetPinInfo(db);

            if (!hasPin)
            {
                MessageBox.Show(this, "No PIN is set yet. Set one from the web dashboard's Settings first.", "Extend App Time", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (!PinService.VerifyPin(pin, salt, hash))
            {
                MessageBox.Show(this, "Incorrect PIN.", "Extend App Time", MessageBoxButtons.OK, MessageBoxIcon.Error);
                _pinBox.Clear();
                _pinBox.Focus();
                return;
            }

            // Same message the web dashboard's /api/extend-limit sends — one
            // handler in MainViewModel applies the grant either way.
            WeakReferenceMessenger.Default.Send(new GrantExtensionCommand(appName, extraMinutes));

            MessageBox.Show(this, $"{appName} now has {extraMinutes} extra minute(s) for today.", "Extend App Time", MessageBoxButtons.OK, MessageBoxIcon.Information);
            Close();
        }
    }
}
