using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace MotorControlApp
{
    /// <summary>
    /// Separate window for defining each linear axis's digital travel limits and Home.
    /// This is pure UI: all motion, position reads, persistence, and timer coordination
    /// live in <see cref="FrmMain"/> — NanoLib is single-channel, so one owner must
    /// serialize access. This form only calls FrmMain's calibration actions and reflects
    /// the shared <see cref="CalibrationStore"/>. Theta is excluded (the rotary chuck has
    /// no home); only X, Y, Z appear.
    /// </summary>
    public sealed class FrmCalibration : Form
    {
        private static readonly AxisId[] CalibAxes = { AxisId.X, AxisId.Y, AxisId.Z };

        private readonly FrmMain _owner;
        private readonly System.Windows.Forms.Timer _refresh = new() { Interval = 300 };

        private sealed record Row(Label Readout, Button SetMin, Button ClearMin, Button SetMax, Button ClearMax, Button? SetHome, Button? Find, Button GoHome);
        private readonly Dictionary<AxisId, Row> _rows = new();

        public FrmCalibration(FrmMain owner)
        {
            _owner = owner;
            Text = "Calibration - travel limits & Home";
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9F);

            var hint = new Label
            {
                Location = new Point(12, 8),
                AutoSize = true,
                MaximumSize = new Size(700, 0),
                Text = "Jog an axis to a position in the main window, then Set Min / Set Max here "
                     + "(Clear Min / Clear Max removes a stored limit). Home = centre of the two limits "
                     + "for X/Y; Z's Home is set explicitly. Find Limits drives Y into its switches automatically.",
            };
            Controls.Add(hint);

            // Buttons auto-size to their text so they don't clip at higher display scaling.
            int y = 56;
            int maxRight = 480;
            foreach (AxisId id in CalibAxes) { maxRight = Math.Max(maxRight, BuildRow(id, y)); y += 84; }
            ClientSize = new Size(maxRight + 12, y + 8);

            _refresh.Tick += (s, e) => UpdateUi();
            _refresh.Start();
            UpdateUi();
        }

        // Builds one axis row; returns the x just past its rightmost control (for form sizing).
        private int BuildRow(AxisId id, int y)
        {
            var name = new Label
            {
                Text = id.ToString(),
                Location = new Point(12, y + 8),
                AutoSize = true,
                Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            };
            var readout = new Label
            {
                Location = new Point(40, y + 8),
                Size = new Size(360, 22),
                Font = new Font("Consolas", 9F),
            };
            Controls.Add(name);
            Controls.Add(readout);

            // Action buttons, each auto-sized to its caption. Set Min/Max sit on the top row;
            // Clear Min/Max sit directly BELOW their respective Set button (a second row). The
            // remaining single buttons (Set Home / Find / Go Home) stay on the top row.
            const int gap = 6;
            int row1 = y + 2;
            int row2 = y + 38;
            int x = 410;

            Button Make(string text, int bx, int by)
            {
                var b = new Button
                {
                    Text = text,
                    Location = new Point(bx, by),
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    Padding = new Padding(8, 3, 8, 3),
                };
                Controls.Add(b);
                return b;
            }

            // Min column: Set Min on top, Clear Min below; advance by the wider of the two.
            Button setMin = Make("Set Min", x, row1);
            Button clearMin = Make("Clear Min", x, row2);
            x += Math.Max(setMin.PreferredSize.Width, clearMin.PreferredSize.Width) + gap;

            // Max column: Set Max on top, Clear Max below.
            Button setMax = Make("Set Max", x, row1);
            Button clearMax = Make("Clear Max", x, row2);
            x += Math.Max(setMax.PreferredSize.Width, clearMax.PreferredSize.Width) + gap;

            // Remaining buttons run along the top row only.
            Button AddTop(string text)
            {
                Button b = Make(text, x, row1);
                x += b.PreferredSize.Width + gap;
                return b;
            }
            // Z defines Home explicitly (no two references to centre); Y can auto-find its limits.
            Button? setHome = id == AxisId.Z ? AddTop("Set Home") : null;
            Button? find = id == AxisId.Y ? AddTop("Find Limits") : null;
            Button goHome = AddTop("Go Home");

            setMin.Click += (s, e) => { _owner.SetCalibrationMin(id); UpdateUi(); };
            clearMin.Click += (s, e) => { _owner.ClearCalibrationMin(id); UpdateUi(); };
            setMax.Click += (s, e) => { _owner.SetCalibrationMax(id); UpdateUi(); };
            clearMax.Click += (s, e) => { _owner.ClearCalibrationMax(id); UpdateUi(); };
            if (setHome != null) setHome.Click += (s, e) => { _owner.SetCalibrationHome(id); UpdateUi(); };
            if (find != null) find.Click += async (s, e) => { await _owner.FindLimitsAsync(id); UpdateUi(); };
            goHome.Click += async (s, e) => { await _owner.GoHomeAsync(id); UpdateUi(); };

            _rows[id] = new Row(readout, setMin, clearMin, setMax, clearMax, setHome, find, goHome);
            return x;
        }

        /// <summary>Repaints readouts and button-enabled states from FrmMain's live state.</summary>
        private void UpdateUi()
        {
            bool canCapture = _owner.CanCaptureCalibration;
            bool canMove = _owner.CanMoveCalibration;
            foreach (AxisId id in CalibAxes)
            {
                AxisCalibration c = _owner.Calibration.For(id);
                Row r = _rows[id];
                string home = id == AxisId.Z ? Fmt(c.Home) : Fmt(c.Center);
                r.Readout.Text = $"Min:{Fmt(c.Min)}  Max:{Fmt(c.Max)}  Home:{home}";

                r.SetMin.Enabled = canCapture;
                r.SetMax.Enabled = canCapture;
                r.ClearMin.Enabled = c.Min.HasValue;   // clearing is a local edit; only needs a value to clear
                r.ClearMax.Enabled = c.Max.HasValue;
                if (r.SetHome != null) r.SetHome.Enabled = canCapture;
                if (r.Find != null) r.Find.Enabled = canMove;
                r.GoHome.Enabled = canMove && _owner.HomeTargetFor(id).HasValue;
            }
        }

        private static string Fmt(long? v) => v.HasValue ? v.Value.ToString("N0") : "—";

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _refresh.Stop();
            base.OnFormClosing(e);
        }
    }
}
