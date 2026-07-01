using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace NanotecController
{
    /// <summary>
    /// Small modal dialog that lets the user pick which network bus to connect to.
    /// Returns the chosen index (aligned with the list from
    /// <see cref="MultiAxisConnection.ListBuses"/>), or -1 if cancelled.
    /// </summary>
    public static class BusPicker
    {
        public static int Choose(IWin32Window owner, IReadOnlyList<string> buses)
        {
            using var dlg = new Form
            {
                Text = "Select network bus",
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false,
                MaximizeBox = false,
                ClientSize = new Size(460, 110),
            };

            var label = new Label
            {
                Text = "Choose the bus to connect to:",
                Location = new Point(12, 14),
                AutoSize = true,
            };
            var combo = new ComboBox
            {
                Location = new Point(12, 40),
                Size = new Size(436, 28),
                DropDownStyle = ComboBoxStyle.DropDownList,
            };
            foreach (string b in buses) combo.Items.Add(b);
            if (combo.Items.Count > 0) combo.SelectedIndex = 0;

            var ok = new Button
            {
                Text = "Connect",
                DialogResult = DialogResult.OK,
                Location = new Point(268, 74),
                Size = new Size(88, 28),
            };
            var cancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Location = new Point(360, 74),
                Size = new Size(88, 28),
            };

            dlg.Controls.Add(label);
            dlg.Controls.Add(combo);
            dlg.Controls.Add(ok);
            dlg.Controls.Add(cancel);
            dlg.AcceptButton = ok;
            dlg.CancelButton = cancel;

            return dlg.ShowDialog(owner) == DialogResult.OK ? combo.SelectedIndex : -1;
        }
    }
}
