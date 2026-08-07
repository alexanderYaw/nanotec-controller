using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace NanotecController
{
    /// <summary>Window for defining each linear axis's digital travel limits and Home. Pure UI —
    /// all motion, persistence and timer coordination live in <see cref="FrmMain"/>, which serializes
    /// the single NanoLib channel. Θ is excluded; only X, Y, Z appear.</summary>
    public sealed class FrmCalibration : Form
    {
        private static readonly AxisId[] CalibAxes = [AxisId.X, AxisId.Y, AxisId.Z];

        private readonly IMotionHost _owner;
        private readonly System.Windows.Forms.Timer _refresh = new() { Interval = 300 };

        private sealed record Row(Label Readout, Button SetMin, Button ClearMin, Button SetMax, Button ClearMax, Button? SetHome, Button GoHome, TextBox StepsPerMm);
        private readonly Dictionary<AxisId, Row> _rows = new();

        /// <summary>The one auto-calibration button — X and Y find their limits together, so it is
        /// not per-axis. Z has no switches and stays manual.</summary>
        private Button _findXy = null!;

        public FrmCalibration(IMotionHost owner)
        {
            _owner = owner;
            Text = "Calibration - travel limits & Home";
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9F);

            // The hint wraps, so rows start below its MEASURED bottom — a hard-coded start-y let
            // the text run over the X row.
            const int HINT_WRAP_WIDTH = 880;
            var hint = new Label
            {
                Location = new Point(12, 8),
                AutoSize = true,
                MaximumSize = new Size(HINT_WRAP_WIDTH, 0),
                Text = "Jog an axis to a position in the main window, then Set Min / Set Max here "
                     + "(Clear Min / Clear Max removes a stored limit). Home = centre of the two limits "
                     + "for X/Y; Z's Home is set explicitly. Find X && Y Limits calibrates BOTH axes in one "
                     + "run, driving each into its own switches at the same time and then homing them, so the "
                     + "chuck ends up centred (Z has none, so its limits stay manual). "
                     + "Steps/mm (from the stage spec) converts mm moves and scales the crosshair mm ticks.",
            };
            Controls.Add(hint);   // add first: the label takes the form's Font, then measures itself

            int y = hint.Bottom + 16;
            int maxRight = Math.Max(480, hint.Right);
            foreach (AxisId id in CalibAxes) { maxRight = Math.Max(maxRight, BuildRow(id, y)); y += 84; }

            var saveMm = new Button
            {
                Text = "Save steps/mm",
                Location = new Point(12, y + 2),
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(8, 3, 8, 3),
            };
            saveMm.Click += (s, e) => SaveStepsPerMm();
            Controls.Add(saveMm);

            // PreferredSize, not .Right: an AutoSize button keeps its default Width until painted.
            int findX = 12 + saveMm.PreferredSize.Width + 12;
            _findXy = new Button
            {
                Text = "Find X && Y Limits (auto)",
                Location = new Point(findX, y + 2),
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(8, 3, 8, 3),
            };
            _findXy.Click += async (s, e) => { await _owner.FindXyLimitsAsync(); UpdateUi(); };
            Controls.Add(_findXy);
            maxRight = Math.Max(maxRight, findX + _findXy.PreferredSize.Width);
            y += 42;

            ClientSize = new Size(maxRight + 12, y + 8);

            _refresh.Tick += (s, e) => UpdateUi();
            _refresh.Start();
            UpdateUi();
        }

        /// <summary>Builds one axis row; returns the x just past its rightmost control.</summary>
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

            // Set Min/Max on the top row, Clear directly below each; Set Home / Go Home stay on top.
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

            Button setMin = Make("Set Min", x, row1);
            Button clearMin = Make("Clear Min", x, row2);
            x += Math.Max(setMin.PreferredSize.Width, clearMin.PreferredSize.Width) + gap;

            Button setMax = Make("Set Max", x, row1);
            Button clearMax = Make("Clear Max", x, row2);
            x += Math.Max(setMax.PreferredSize.Width, clearMax.PreferredSize.Width) + gap;

            Button AddTop(string text)
            {
                Button b = Make(text, x, row1);
                x += b.PreferredSize.Width + gap;
                return b;
            }
            // Z defines Home explicitly, having no two references to centre.
            Button? setHome = id == AxisId.Z ? AddTop("Set Home") : null;
            Button goHome = AddTop("Go Home");

            // Filled once from the store; the refresh timer must NOT touch it, or it would
            // overwrite the user mid-typing.
            var mmLabel = new Label { Text = "steps/mm:", Location = new Point(x, row2 + 6), AutoSize = true };
            Controls.Add(mmLabel);   // add first so it takes the form's Font before it is measured
            // Past the label's MEASURED width: a hard-coded offset only cleared it at 100% scaling.
            var mmBox = new TextBox { Location = new Point(x + mmLabel.PreferredSize.Width + gap, row2 + 2), Size = new Size(90, 24) };
            mmBox.Text = _owner.Calibration.For(id).StepsPerMm?.ToString("0.####") ?? "";
            Controls.Add(mmBox);
            x = Math.Max(x, mmBox.Right + gap);

            setMin.Click += (s, e) => { _owner.SetCalibrationMin(id); UpdateUi(); };
            clearMin.Click += (s, e) => { _owner.ClearCalibrationMin(id); UpdateUi(); };
            setMax.Click += (s, e) => { _owner.SetCalibrationMax(id); UpdateUi(); };
            clearMax.Click += (s, e) => { _owner.ClearCalibrationMax(id); UpdateUi(); };
            if (setHome != null) setHome.Click += (s, e) => { _owner.SetCalibrationHome(id); UpdateUi(); };
            goHome.Click += async (s, e) => { await _owner.GoHomeAsync(id); UpdateUi(); };

            _rows[id] = new Row(readout, setMin, clearMin, setMax, clearMax, setHome, goHome, mmBox);
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
                r.GoHome.Enabled = canMove && _owner.HomeTargetFor(id).HasValue;
            }
            _findXy.Enabled = canMove;
        }

        /// <summary>Parses and persists all three steps/mm entries (empty clears one). All-or-nothing:
        /// a bad entry aborts before anything is stored, so the file never holds a half-edit.</summary>
        private void SaveStepsPerMm()
        {
            var parsed = new Dictionary<AxisId, double?>();
            foreach (AxisId id in CalibAxes)
            {
                string t = _rows[id].StepsPerMm.Text.Trim();
                if (t.Length == 0) { parsed[id] = null; continue; }
                if (!double.TryParse(t, out double v) || v <= 0)
                {
                    MessageBox.Show(this, $"{id} steps/mm must be a positive number (or empty to clear it).",
                        "Steps per mm", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                parsed[id] = v;
            }
            foreach (AxisId id in CalibAxes) _owner.Calibration.For(id).StepsPerMm = parsed[id];
            try { _owner.Calibration.Save(); }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Save failed: " + ex.Message,
                    "Steps per mm", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
