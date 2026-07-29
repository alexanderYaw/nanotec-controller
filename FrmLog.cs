using System;
using System.Text;
using System.Windows.Forms;

namespace NanotecController
{
    /// <summary>
    /// On-demand full log window. The main form keeps the log in a capped ring buffer and shows
    /// only the latest line on its status strip; this window (opened from the strip) shows the
    /// whole buffer and appends new lines live. Modeless and owned by the main form, so it stays
    /// above it and closes with it. A singleton in practice (FrmMain reuses one instance), but
    /// self-contained — it unsubscribes from the host's events on close so a stale window can't
    /// leak or fire after disposal.
    /// </summary>
    public sealed class FrmLog : Form
    {
        private readonly FrmMain _owner;
        private readonly TextBox _box = new()
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            BackColor = System.Drawing.Color.White,
            Font = new System.Drawing.Font("Consolas", 9F),
            WordWrap = false,
        };

        public FrmLog(FrmMain owner)
        {
            _owner = owner;
            Text = "Log";
            StartPosition = FormStartPosition.CenterParent;
            Font = new System.Drawing.Font("Segoe UI", 9F);
            ClientSize = new System.Drawing.Size(720, 480);
            MinimumSize = new System.Drawing.Size(360, 240);
            Controls.Add(_box);

            // Snapshot what's already buffered, then follow new lines / clears live.
            var sb = new StringBuilder();
            foreach (string line in _owner.LogSnapshot) sb.AppendLine(line);
            _box.Text = sb.ToString();
            ScrollToEnd();

            _owner.LogLineAdded += OnLineAdded;
            _owner.LogCleared += OnCleared;
        }

        // Events are raised on the UI thread (AppendLog runs there), same thread as this window,
        // so no marshalling is needed — just guard against a late fire during teardown.
        private void OnLineAdded(string line)
        {
            if (IsDisposed) return;
            _box.AppendText(line + Environment.NewLine);
        }

        private void OnCleared()
        {
            if (IsDisposed) return;
            _box.Clear();
        }

        private void ScrollToEnd()
        {
            _box.SelectionStart = _box.TextLength;
            _box.ScrollToCaret();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _owner.LogLineAdded -= OnLineAdded;
            _owner.LogCleared -= OnCleared;
            base.OnFormClosing(e);
        }
    }
}
