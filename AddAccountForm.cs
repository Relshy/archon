// dialog for saving one account's cookie.

using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace Archon
{
    class AddAccountForm : Form
    {
        private FieldBox _cookieBox;
        private CheckBox _showBox;
        private Label _status;
        private FlatBtn _saveButton;
        private FlatBtn _cancelButton;
        private bool _working;

        // only set if the dialog closed with OK
        public RobloxAccount Result;

        public AddAccountForm()
        {
            Text = "Add account";
            ClientSize = new Size(520, 412);
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            BackColor = Theme.Bg;
            ForeColor = Theme.TextHi;
            Font = Theme.Body;

            Build();
        }

        private void Build()
        {
            Label heading = new Label();
            heading.Text = "Save a Roblox session";
            heading.Font = Theme.H2;
            heading.ForeColor = Theme.TextHi;
            heading.Dock = DockStyle.Fill;
            heading.Margin = new Padding(0);

            Label how = new Label();
            how.Text =
                "In a browser signed in to the account you want to add:\r\n" +
                "   1.  Press F12 to open developer tools\r\n" +
                "   2.  Application  ▸  Cookies  ▸  https://www.roblox.com\r\n" +
                "   3.  Copy the whole value of  .ROBLOSECURITY";
            how.Font = Theme.Body;
            how.ForeColor = Theme.TextMid;
            how.Dock = DockStyle.Fill;
            how.Margin = new Padding(0, 6, 0, 0);

            _cookieBox = new FieldBox();
            _cookieBox.UseSystemPasswordChar = true;
            _cookieBox.Dock = DockStyle.Fill;
            _cookieBox.Margin = new Padding(0, 6, 0, 0);
            _cookieBox.TextChanged += delegate { UpdateEnabled(); };

            _showBox = new CheckBox();
            _showBox.Text = "Show cookie";
            _showBox.Font = Theme.Small;
            _showBox.ForeColor = Theme.TextMid;
            _showBox.FlatStyle = FlatStyle.Flat;
            _showBox.Dock = DockStyle.Fill;
            _showBox.Margin = new Padding(0, 2, 0, 0);
            _showBox.CheckedChanged += delegate
            {
                _cookieBox.UseSystemPasswordChar = !_showBox.Checked;
            };

            Card warning = new Card();
            warning.Fill = Theme.CardHover;
            warning.Stroke = Color.FromArgb(0x3A, 0x3A, 0x3A);
            warning.Dock = DockStyle.Fill;
            warning.Margin = new Padding(0, 8, 0, 0);
            warning.Padding = new Padding(12, 8, 12, 8);

            Label warningText = new Label();
            warningText.Text =
                "This cookie signs in as you with no password and no 2FA.\r\n" +
                "It is stored encrypted with Windows DPAPI, readable only by your\r\n" +
                "Windows user on this PC. Never paste it anywhere else.";
            warningText.Font = Theme.BodyBold;
            warningText.ForeColor = Theme.TextHi;
            warningText.Dock = DockStyle.Fill;
            warning.Controls.Add(warningText);

            _status = new Label();
            _status.Text = "";
            _status.Font = Theme.Small;
            _status.ForeColor = Theme.TextMid;
            _status.Dock = DockStyle.Fill;
            _status.Margin = new Padding(0, 6, 0, 0);

            _cancelButton = new FlatBtn();
            _cancelButton.Text = "Cancel";
            _cancelButton.Kind = ButtonKind.Ghost;
            _cancelButton.Dock = DockStyle.Fill;
            _cancelButton.Margin = new Padding(0, 0, 8, 0);
            _cancelButton.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };

            _saveButton = new FlatBtn();
            _saveButton.Text = "Verify and save";
            _saveButton.Kind = ButtonKind.Primary;
            _saveButton.Dock = DockStyle.Fill;
            _saveButton.Margin = new Padding(0);
            _saveButton.Enabled = false;
            _saveButton.Click += delegate { Verify(); };

            TableLayoutPanel buttons = new TableLayoutPanel();
            buttons.Dock = DockStyle.Fill;
            buttons.Margin = new Padding(0, 8, 0, 0);
            buttons.ColumnCount = 3;
            buttons.RowCount = 1;
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 108f));
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150f));
            buttons.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            buttons.Controls.Add(new Panel(), 0, 0);
            buttons.Controls.Add(_cancelButton, 1, 0);
            buttons.Controls.Add(_saveButton, 2, 0);

            TableLayoutPanel grid = new TableLayoutPanel();
            grid.Dock = DockStyle.Fill;
            grid.Padding = new Padding(20, 18, 20, 18);
            grid.BackColor = Theme.Bg;
            grid.ColumnCount = 1;
            grid.RowCount = 8;
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 28f));   // heading
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 78f));   // how-to
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 36f));   // cookie box
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 24f));   // show checkbox
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 72f));   // warning
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 24f));   // status
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));   // filler
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 46f));   // buttons

            grid.Controls.Add(heading, 0, 0);
            grid.Controls.Add(how, 0, 1);
            grid.Controls.Add(_cookieBox, 0, 2);
            grid.Controls.Add(_showBox, 0, 3);
            grid.Controls.Add(warning, 0, 4);
            grid.Controls.Add(_status, 0, 5);
            grid.Controls.Add(new Panel(), 0, 6);
            grid.Controls.Add(buttons, 0, 7);

            Controls.Add(grid);
            CancelButton = _cancelButton;
        }

        private void UpdateEnabled()
        {
            _saveButton.Enabled = !_working && _cookieBox.Text.Trim().Length > 0;
        }

        private void Verify()
        {
            string cookie = _cookieBox.Text.Trim();
            if (cookie.Length == 0) return;

            // copying from devtools sometimes drags the name in too
            const string prefix = ".ROBLOSECURITY=";
            if (cookie.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                cookie = cookie.Substring(prefix.Length).Trim();
            cookie = cookie.TrimEnd(';');

            _working = true;
            _saveButton.Enabled = false;
            _cancelButton.Enabled = false;
            SetStatus("Checking with Roblox…", Theme.TextMid);

            ThreadPool.QueueUserWorkItem(delegate
            {
                string error;
                RobloxIdentity identity = RobloxWeb.WhoAmI(cookie, out error);

                try
                {
                    if (IsHandleCreated && !IsDisposed)
                        BeginInvoke(new Action<RobloxIdentity, string, string>(OnVerified),
                                    new object[] { identity, error, cookie });
                }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
            });
        }

        private void OnVerified(RobloxIdentity identity, string error, string cookie)
        {
            _working = false;
            _cancelButton.Enabled = true;

            if (identity == null)
            {
                SetStatus(error, Theme.Danger);
                UpdateEnabled();
                return;
            }

            RobloxAccount account = new RobloxAccount();
            account.Username = identity.Username;
            account.DisplayName = identity.DisplayName;
            account.UserId = identity.UserId;
            account.AddedUtc = DateTime.UtcNow;

            try
            {
                account.ProtectedCookie = AccountStore.Protect(cookie);
            }
            catch (Exception ex)
            {
                SetStatus("Could not encrypt the cookie: " + ex.Message, Theme.Danger);
                UpdateEnabled();
                return;
            }

            Result = account;
            DialogResult = DialogResult.OK;
            Close();
        }

        private void SetStatus(string text, Color color)
        {
            _status.Text = text;
            _status.ForeColor = color;
        }
    }
}
