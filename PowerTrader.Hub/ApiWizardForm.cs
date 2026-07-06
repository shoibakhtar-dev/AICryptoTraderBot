using System;
using System.IO;
using System.Text;
using System.Windows.Forms;
using PowerTrader.Core.Util;

namespace PowerTrader.Hub
{
    /// <summary>
    /// Robinhood API key wizard (port of the Tkinter setup wizard).
    /// Generates an Ed25519 keypair, shows the PUBLIC key to paste into Robinhood, and saves
    /// r_key.txt (the API key) + r_secret.txt (base64 of the 32-byte private seed).
    /// </summary>
    internal sealed class ApiWizardForm : Form
    {
        private readonly string _projectDir;
        private TextBox _pubBox, _apiKeyBox;
        private CheckBox _ack;
        private string _privSeedB64;

        public ApiWizardForm(string projectDir)
        {
            _projectDir = projectDir;
            Text = "Robinhood API Setup";
            Width = 720; Height = 560;
            StartPosition = FormStartPosition.CenterParent;

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(12), AutoScroll = true };

            layout.Controls.Add(new Label
            {
                AutoSize = true,
                MaximumSize = new System.Drawing.Size(660, 0),
                Text = "Step 1: Click 'Generate Keys'. This creates a new Ed25519 keypair.\n" +
                       "Copy the PUBLIC key below and add it as a new API key in your Robinhood account (allow trading).\n" +
                       "Robinhood will then give you an API key (often starts with 'rh')."
            });

            var gen = new Button { Text = "Generate Keys", AutoSize = true };
            gen.Click += (s, e) => GenerateKeys();
            layout.Controls.Add(gen);

            layout.Controls.Add(new Label { Text = "Public key (paste into Robinhood):", AutoSize = true });
            _pubBox = new TextBox { ReadOnly = true, Multiline = true, Width = 660, Height = 60, ScrollBars = ScrollBars.Vertical };
            layout.Controls.Add(_pubBox);
            var copyPub = new Button { Text = "Copy Public Key", AutoSize = true };
            copyPub.Click += (s, e) => { if (!string.IsNullOrEmpty(_pubBox.Text)) Clipboard.SetText(_pubBox.Text); };
            layout.Controls.Add(copyPub);

            layout.Controls.Add(new Label
            {
                AutoSize = true,
                MaximumSize = new System.Drawing.Size(660, 0),
                Text = "\nStep 2: Paste the API key Robinhood gave you, confirm you understand the private key is secret, then Save."
            });
            layout.Controls.Add(new Label { Text = "Robinhood API key:", AutoSize = true });
            _apiKeyBox = new TextBox { Width = 660 };
            layout.Controls.Add(_apiKeyBox);

            _ack = new CheckBox { Text = "I understand r_secret.txt is PRIVATE and I will not share it.", AutoSize = true };
            layout.Controls.Add(_ack);

            var save = new Button { Text = "Save", AutoSize = true };
            save.Click += (s, e) => SaveCreds();
            layout.Controls.Add(save);

            var openFolder = new Button { Text = "Open credentials folder", AutoSize = true };
            openFolder.Click += (s, e) => { try { System.Diagnostics.Process.Start("explorer.exe", _projectDir); } catch { } };
            layout.Controls.Add(openFolder);

            Controls.Add(layout);
            Theme.Apply(this);

            // If creds already exist, prefill the API key box.
            try
            {
                string kp = Path.Combine(_projectDir, "r_key.txt");
                if (File.Exists(kp)) _apiKeyBox.Text = (File.ReadAllText(kp) ?? "").Trim();
            }
            catch { }
        }

        private void GenerateKeys()
        {
            try
            {
                var (privB64, pubB64) = Ed25519Util.GenerateKeypair();
                _privSeedB64 = privB64;
                _pubBox.Text = pubB64;
                MessageBox.Show("Keys generated. Copy the public key into Robinhood, then paste the API key and Save.",
                    "Generated", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to generate keys:\n" + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void SaveCreds()
        {
            string apiKey = (_apiKeyBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(_privSeedB64))
            {
                MessageBox.Show("Click 'Generate Keys' first.", "Missing private key", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (string.IsNullOrEmpty(apiKey))
            {
                MessageBox.Show("Paste the Robinhood API key.", "Missing API key", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (!_ack.Checked)
            {
                MessageBox.Show("Please confirm you understand r_secret.txt is private.", "Confirm", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            try
            {
                var enc = new UTF8Encoding(false);
                File.WriteAllText(Path.Combine(_projectDir, "r_key.txt"), apiKey, enc);
                File.WriteAllText(Path.Combine(_projectDir, "r_secret.txt"), _privSeedB64, enc);
                MessageBox.Show("Saved r_key.txt and r_secret.txt.", "Saved", MessageBoxButtons.OK, MessageBoxIcon.Information);
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to save credentials:\n" + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
