using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NanotecController
{
    // FrmMain — physical-unit RELATIVE moves (mm for X/Y/Z, degrees for Θ) plus one-touch
    // "go to stored centre" shortcuts. Built in code into a group under the jog cluster (house
    // style). Mode-aware, mirroring the jog cluster:
    //   • RAW  X/Y/Z → Δsteps = round(mm × StepsPerMm); target = current + Δ, via MoveToAsync
    //                  (which bounds-checks and handles the Y user↔raw flip).
    //   • RAW  Θ     → MoveThetaByDegreesAsync (Profile-Position to current + DegreesToChuckTicks).
    //   • VISION X/Y → the mm is along the SCREEN axis; mapped to a stage (ΔX,ΔY) through the
    //                  pixel→step affine and the per-axis steps/mm (so it tracks the crosshair
    //                  regardless of camera rotation). Z stays raw in VISION too.
    //   • VISION Θ   → RotateAboutCrosshairAsync (pins the crosshair point while Θ turns).
    // All motion routes through the same serialized drive ops as the rest of FrmMain. (Partial.)
    public partial class FrmMain
    {
        private static readonly AxisId[] _relAxes = { AxisId.X, AxisId.Y, AxisId.Z, AxisId.Theta };
        private readonly Dictionary<AxisId, NumericUpDown> _relNum = new();
        private readonly Dictionary<AxisId, Button> _relGo = new();
        private Button _chuckCentreBtn = null!;
        private Button _waferCentreBtn = null!;
        private bool _relBuilt;

        // Builds the "Relative move & centres" group into leftPanel, just below the jog cluster.
        private void BuildRelativeMovePanel()
        {
            var group = new GroupBox
            {
                Text = "Relative move (mm / °) — mode-aware · & go-to-centre",
                Location = new Point(18, 756),
                Size = new Size(662, 210),
                TabStop = false,
            };

            int y = 26;
            foreach (AxisId id in _relAxes)
            {
                bool theta = id == AxisId.Theta;
                var name = new Label
                {
                    Text = theta ? "Θ" : id.ToString(),
                    Location = new Point(14, y + 5),
                    AutoSize = true,
                    Font = new Font("Segoe UI", 10F, FontStyle.Bold),
                };
                var num = new NumericUpDown
                {
                    Location = new Point(56, y),
                    Size = new Size(96, 26),
                    DecimalPlaces = theta ? 1 : 3,
                    Increment = theta ? 1m : 0.1m,
                    Minimum = theta ? -360m : -1000m,
                    Maximum = theta ? 360m : 1000m,
                    Enabled = false,
                };
                var unit = new Label { Text = theta ? "°" : "mm", Location = new Point(158, y + 5), AutoSize = true };
                var go = new Button { Text = "Go", Location = new Point(196, y - 2), Size = new Size(72, 30), Enabled = false };
                AxisId captured = id;   // for the closure
                go.Click += (s, e) => GoRelative(captured);

                group.Controls.Add(name);
                group.Controls.Add(num);
                group.Controls.Add(unit);
                group.Controls.Add(go);
                _relNum[id] = num;
                _relGo[id] = go;
                y += 34;
            }

            // Go-to-stored-centre shortcuts (chuck / wafer). These drive X/Y to the persisted
            // user-frame centre; confirm first, since it's an unbounded table traverse.
            _chuckCentreBtn = new Button { Text = "Move to chuck centre", Location = new Point(14, 168), Size = new Size(206, 32), Enabled = false };
            _chuckCentreBtn.Click += (s, e) => GoToStoredCentre("chuck", _calib.ChuckCenterX, _calib.ChuckCenterY);
            _waferCentreBtn = new Button { Text = "Move to wafer centre", Location = new Point(230, 168), Size = new Size(206, 32), Enabled = false };
            _waferCentreBtn.Click += (s, e) => GoToStoredCentre("wafer", _calib.WaferCenterX, _calib.WaferCenterY);
            group.Controls.Add(_chuckCentreBtn);
            group.Controls.Add(_waferCentreBtn);

            leftPanel.Controls.Add(group);
            _relBuilt = true;
        }

        // Gating (called from RefreshButtons): the whole panel needs the drives enabled + idle
        // (CanMoveCalibration greys it during any op). Each Go additionally needs the calibration
        // its move depends on — raw linear needs that axis's steps/mm; vision X/Y also the affine;
        // vision Θ needs a full rotate calibration; centre buttons need the stored centre.
        private void RefreshRelativeMove()
        {
            if (!_relBuilt) return;
            bool canMove = CanMoveCalibration;
            bool vision = _jogMode == JogMode.Vision;
            bool mmPerPxOk = VisionViewControl.MmPerPixel(_calib) != null;

            foreach (AxisId id in _relAxes)
            {
                _relNum[id].Enabled = canMove;
                bool go;
                if (id == AxisId.Theta)
                    go = canMove && (vision ? CanRotate : true);
                else if (id == AxisId.Z || !vision)          // Z is always raw; so is X/Y in RAW mode
                    go = canMove && _calib.For(id).StepsPerMm is > 0;
                else                                          // VISION X/Y
                    go = canMove && _calib.PixelStep != null && mmPerPxOk;
                _relGo[id].Enabled = go;
            }

            _chuckCentreBtn.Enabled = canMove && _calib.ChuckCenterX.HasValue && _calib.ChuckCenterY.HasValue;
            _waferCentreBtn.Enabled = canMove && _calib.WaferCenterX.HasValue && _calib.WaferCenterY.HasValue;
        }

        // A Go was pressed: dispatch by axis + current jog mode. async void — it's a UI event
        // handler, and every path it awaits logs its own success/failure.
        private async void GoRelative(AxisId id)
        {
            if (!CanMoveCalibration) { AppendLog("Relative move: enable the drives first."); return; }
            double amount = (double)_relNum[id].Value;
            if (Math.Abs(amount) < 1e-9) { AppendLog("Relative move: enter a non-zero amount."); return; }
            bool vision = _jogMode == JogMode.Vision;

            if (id == AxisId.Theta)
            {
                if (vision) await RotateAboutCrosshairAsync(amount);
                else await MoveThetaByDegreesAsync(amount);
                return;
            }

            // X/Y/Z linear. Z is always raw; X/Y are raw unless in VISION mode.
            if (id == AxisId.Z || !vision)
            {
                double? k = _calib.For(id).StepsPerMm;
                if (k is not > 0) { AppendLog($"Relative move: set {id} steps/mm in Calibration → Axes first."); return; }
                if (!TryCurrentUser(id, out long cur)) { AppendLog($"Relative move: {id} position not polled yet."); return; }
                long target = cur + (long)Math.Round(amount * k.Value);
                AppendLog($"{id} relative {amount:+0.###;-0.###} mm → {target:N0}...");
                await MoveUserAsync(id == AxisId.X ? target : (long?)null,
                                    id == AxisId.Y ? target : (long?)null,
                                    id == AxisId.Z ? target : (long?)null);
            }
            else
            {
                // VISION X/Y: mm along the SCREEN axis → pixels → stage (ΔX,ΔY) via the affine.
                // Screen convention (matches the vision jog): right = +col, up = −row.
                (double mmPxCol, double mmPxRow)? mm = VisionViewControl.MmPerPixel(_calib);
                PixelStepAffine? a = _calib.PixelStep;
                if (mm == null || a == null) { AppendLog("Relative move (vision): needs the camera-scale calibration and X/Y steps/mm."); return; }
                if (!TryCurrentUser(AxisId.X, out long curX) || !TryCurrentUser(AxisId.Y, out long curY))
                { AppendLog("Relative move (vision): X/Y position not polled yet."); return; }

                double dCol = 0, dRow = 0;
                if (id == AxisId.X) dCol = amount / mm.Value.mmPxCol;    // screen horizontal
                else                dRow = -amount / mm.Value.mmPxRow;   // screen vertical, up = +amount
                long dX = (long)Math.Round(a.Xr * dRow + a.Xc * dCol);
                long dY = (long)Math.Round(a.Yr * dRow + a.Yc * dCol);
                AppendLog($"Vision {(id == AxisId.X ? "screen-X" : "screen-Y")} {amount:+0.###;-0.###} mm → ΔX={dX:N0} ΔY={dY:N0}...");
                await MoveUserAsync(curX + dX, curY + dY, null);
            }
        }

        // Rotates Θ by a relative angle as a plain Profile-Position move (RAW mode). Θ has no
        // user/raw flip; speed follows the Θ jog slider (raw range), floored so it can't stall.
        private async Task MoveThetaByDegreesAsync(double degrees)
        {
            if (!CanMoveCalibration) return;
            long ticks = CrosshairRotation.DegreesToChuckTicks(degrees, CrosshairRotation.ChuckTicksPerRev);
            if (ticks == 0) { AppendLog("Relative Θ: angle too small to move."); return; }
            int speed = Math.Max(200, _axisRows[AxisId.Theta].Speed.Value);

            using var busyScope = BeginBusy();
            AppendLog($"Rotate Θ by {degrees:+0.#;-0.#}° ({ticks:+0;-0} ticks) at {speed}...");
            long start = 0, end = 0;
            bool ok = await RunDriveOp(() =>
            {
                _motion!.RecoverIfQuickStopped(AxisId.Theta);
                start = _motion.GetStatus(AxisId.Theta).Position;
                _motion.MoveAbsolute(AxisId.Theta, start + ticks, speed);
                WaitOrStop(AxisId.Theta, FIND_TIMEOUT_MS);
                end = _motion.GetStatus(AxisId.Theta).Position;
            });
            AppendLog(ok ? $"Rotate Θ complete ({start:N0} → {end:N0})." : "Rotate Θ FAILED — see error above.");
        }

        // Confirm, then drive X/Y to a stored USER-frame centre (chuck/wafer) via MoveToAsync.
        private async void GoToStoredCentre(string which, long? xUser, long? yUser)
        {
            if (!CanMoveCalibration) { AppendLog("Move to centre: enable the drives first."); return; }
            if (xUser is not long x || yUser is not long y) { AppendLog($"No {which} centre stored — run the {which} centre-find first."); return; }
            if (MessageBox.Show(this,
                    $"Move the table to the {which} centre?\r\n\r\nX = {x:N0}\r\nY = {y:N0}",
                    $"Move to {which} centre", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                return;
            AppendLog($"Moving to {which} centre (X={x:N0}, Y={y:N0})...");
            await MoveUserAsync(x, y, null);
        }

        // Thin adapter over MoveToAsync (which takes optional user-frame coordinate strings and
        // does the bounds-check + Y flip). null = leave that axis where it is.
        private Task MoveUserAsync(long? xUser, long? yUser, long? zUser)
            => MoveToAsync(
                xUser?.ToString() ?? "",
                yUser?.ToString() ?? "",
                zUser?.ToString() ?? "");
    }
}
