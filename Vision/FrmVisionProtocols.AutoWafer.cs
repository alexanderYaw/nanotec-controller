using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NanotecController
{
    // FrmVisionProtocols — AUTOMATIC wafer centre-find by Θ scan.
    //
    // The chuck centre-find drives the stage right around the chuck rim. That cannot work for the
    // wafer: viewing the wafer rim needs motor positions on a circle of the wafer's own radius about
    // the chuck centre, and a 200 mm wafer's rim circle is larger than the X/Y travel in both axes —
    // only a band of it is reachable, and a circle fit over one short arc is badly conditioned.
    //
    // But the chuck IS the Θ axis and Θ is continuous, so the wafer can be turned instead of
    // circled. The stage parks on ONE reachable spot on the rim and Θ sweeps the whole rim past the
    // camera. De-rotating each sample by the angle it was taken at (WaferCentreScan) puts the points
    // back on a full 360° circle, and the same Pratt fit the chuck uses finishes the job.
    //
    // STEP-AND-SETTLE, exactly as the chuck run: Θ moves, stops, and only then is a frame grabbed,
    // so the angle and the position paired with each frame are both exact.
    //
    // Shape of a run:
    //   A  pick the station direction — the cardinal from the chuck centre with the most travel
    //   B  acquire the rim: ProbeAsync outward from the chuck centre along it
    //   C  N+1 samples, rotating Θ by 360/N between them (the last repeats θ₀ as a closure check);
    //      after each hit the station moves ONTO the rim point just measured, which is what keeps
    //      the rim inside a ~5 mm field of view while the eccentricity sweeps it past
    //   D  de-rotate + fit (WaferCentreScan), which also settles the handedness and drops outliers
    //   E  closure check, then persist the offset (chuck frame) + radius + fit metadata
    // (Partial of FrmVisionProtocols; layout lives in FrmVisionProtocols.cs.)
    public sealed partial class FrmVisionProtocols
    {
        // How far a station probe may travel out from the chuck centre, as a multiple of the nominal
        // wafer radius. The rim sits at R + e, so this only has to cover a plausible eccentricity.
        private const double WAFER_GUARD_FRAC = 1.25;
        // Acceptance band on |E − chuck centre|, as fractions of the nominal radius. Wide enough for
        // any eccentricity that could physically sit on the chuck, tight enough to reject a
        // detection that latched onto something other than the rim.
        private const double WAFER_BAND_LO_FRAC = 0.70;
        private const double WAFER_BAND_HI_FRAC = 1.30;
        // Skip the empty middle of the wafer on the way out to the rim.
        private const double WAFER_APPROACH_FRAC = 0.90;
        // A detection this close to the frame edge (px) is refused. WaferEdgeDetector already drops
        // boundary points sitting on the frame itself; this widens that margin, since a rim point
        // that close to the edge has half its neighbourhood out of view.
        private const double WAFER_BORDER_MARGIN_PX = 8.0;
        // Local radial search when a sample misses: this many hops either side of the station, along
        // the station direction. Covers the rim having slipped out of view without the long trip back
        // to the chuck centre that ProbeAsync would make.
        private const int WAFER_SEARCH_HOPS = 3;
        // Θ speed for the scan's rotations (drive units = steps/s). Θ tops out at 3200; a full
        // revolution is 359,859 ticks, so one scan spends ~2 minutes turning whatever N is.
        private const int WAFER_THETA_SPEED = 3000;
        // Arrival tolerance for the station moves, as a fraction of one hop.
        private const double WAFER_ARRIVE_FRAC = 0.25;
        // The closure sample returns to θ₀; its radius must reproduce the first sample's to within
        // this many steps or the wafer moved on the chuck and the whole fit is void.
        private const double WAFER_CLOSURE_TOL_STEPS = 400;

        private volatile bool _waferCancel;
        private bool _waferRunning;

        // --- Orchestration ----------------------------------------------------------

        private async Task RunWaferScanAsync()
        {
            if (_waferRunning || _autoRunning) return;

            CalibrationStore cal = _owner.Calibration;
            PixelStepAffine? a = cal.PixelStep;
            if (a == null) { _status.Text = "Wafer Θ scan: needs the camera-scale calibration first."; return; }
            if (!_view.IsCameraOpen) { _status.Text = "Wafer Θ scan: the camera is not streaming."; return; }
            if (!_owner.CanMoveCalibration) { _status.Text = "Wafer Θ scan: needs the drives enabled and idle."; return; }
            if (cal.ChuckCenterX is not long ccx || cal.ChuckCenterY is not long ccy)
            {
                _status.Text = "Wafer Θ scan: needs the chuck centre — run the chuck centre-find first.";
                MessageBox.Show(this,
                    "The wafer scan measures the wafer's offset from the chuck's ROTATION AXIS, so it " +
                    "needs the chuck centre before it can start.\r\n\r\nRun the automatic chuck " +
                    "centre-find first.",
                    "Wafer Θ scan", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (!TryStepsPerMm(out double kX, out double kY))
            {
                _status.Text = "Wafer Θ scan: X and Y both need steps-per-mm (axis calibration window).";
                return;
            }

            int n = (int)_waferSamples.Value;
            double nominalR = (double)_waferDia.Value / 2.0 * ((kX + kY) / 2.0);
            if (nominalR < 1) { _status.Text = "Wafer Θ scan: the wafer diameter gives a degenerate radius."; return; }

            if (MessageBox.Show(this,
                    "Automatic wafer centre-find (Θ scan).\r\n\r\nThe stage will:\r\n" +
                    $"  1. move to the chuck centre and probe outward to the wafer rim\r\n" +
                    $"  2. take {n} samples around the rim, turning Θ by {360.0 / n:F1}° between each\r\n" +
                    $"     — one full revolution, about two minutes of turning\r\n" +
                    $"  3. re-sample the starting angle as a closure check\r\n\r\n" +
                    "Confirm before running:\r\n" +
                    "  - the wafer is held (vacuum on) — it must not move on the chuck\r\n" +
                    "  - Z / focus is set so the wafer edge is sharp\r\n" +
                    "  - a full 360° turn is clear\r\n\r\n" +
                    "Z is never moved.\r\n\r\nProceed?",
                    "Wafer Θ scan", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            _waferCancel = false;
            _waferRunning = true;
            _autoTarget = AutoTarget.Wafer;
            RefreshWaferUi();
            RefreshAutoUi();
            // Same lockout the chuck run takes, for the same reason: FrmMain's own busy flag drops
            // between steps, which would leave the d-pad and the polled joystick live between a move
            // and the frame paired with it.
            using IDisposable hostLock = _owner.BeginExternalOp("Auto wafer centre-find (Θ scan)");
            try
            {
                _waferLog.Clear();
                await WaferScanCoreAsync(a, (ccx, ccy), kX, kY, nominalR, n);
            }
            finally
            {
                _waferRunning = false;
                _waferCancel = false;
                _autoTarget = AutoTarget.Chuck;
                RefreshWaferUi();
                RefreshAutoUi();
            }
        }

        private async Task WaferScanCoreAsync(
            PixelStepAffine a, (double X, double Y) c, double kX, double kY, double nominalR, int n)
        {
            // Size the hop from the LIVE frame, as the chuck run does — zoom is a centred-ROI crop,
            // so the field of view in steps changes with whatever zoom the operator is on.
            AutoDetection start = await DetectEdgeAsync();
            if (start.FrameW <= 0) { _status.Text = "Wafer Θ scan: no frame from the camera."; return; }
            double hop = HopSteps(a, start.FrameW, start.FrameH);
            if (hop < 1.0) { _status.Text = "Wafer Θ scan: the calibration affine gives a degenerate hop size."; return; }

            AutoLog($"Chuck centre ({c.X:F0}, {c.Y:F0})  nominal R={nominalR:F0} steps  hop={hop:F0}.");

            // ---- Stage A: the station direction is whichever cardinal has the most travel ----
            if (!TryPickStationDirection(c, nominalR, out (double X, double Y) dir, out double headroom, out string dirWhy))
            {
                AutoLog(dirWhy);
                _status.Text = "Wafer Θ scan: the wafer rim is outside the travel envelope in every direction.";
                return;
            }
            AutoLog($"Station direction ({dir.X:+0;-0;0}, {dir.Y:+0;-0;0}) — {headroom:N0} steps of travel, rim needs {nominalR:N0}.");

            // ---- Stage B: acquire the rim ----
            double guard = WAFER_GUARD_FRAC * nominalR;
            (ProbeOutcome outcome, (double X, double Y) station) = await ProbeAsync(
                c, dir, a, guard,
                WAFER_BAND_LO_FRAC * nominalR, WAFER_BAND_HI_FRAC * nominalR,
                WAFER_APPROACH_FRAC * nominalR, hop, "rim");
            if (outcome != ProbeOutcome.Found)
            {
                _status.Text = outcome == ProbeOutcome.Aborted
                    ? "Wafer Θ scan: aborted while acquiring the rim — see the log."
                    : "Wafer Θ scan: could not find the wafer rim — check focus/Z, lighting and the wafer diameter.";
                return;
            }

            // ---- Stage C: sample around one full revolution ----
            double stepDeg = 360.0 / n;
            var samples = new List<WaferCentreScan.Sample>(n + 1);
            double firstRadius = 0;
            double lastRadius = 0;
            int missed = 0;

            for (int k = 0; k <= n; k++)
            {
                if (_waferCancel) { AutoLog("Cancelled."); _status.Text = "Wafer Θ scan cancelled."; return; }

                if (!_owner.TryReadThetaNow(out long thetaTicks))
                {
                    AutoLog("Θ position unavailable — run aborted.");
                    _status.Text = "Wafer Θ scan: Θ position unavailable.";
                    return;
                }
                double thetaDeg = CrosshairRotation.ChuckTicksToDegrees(thetaTicks, CrosshairRotation.ChuckTicksPerRev);

                _status.Text = $"Wafer Θ scan: sample {k + 1}/{n + 1} at Θ={thetaDeg:F1}°...";
                (bool got, (double X, double Y) e) = await SampleAtStationAsync(station, dir, a, c, hop, nominalR);
                if (_waferCancel) { AutoLog("Cancelled."); _status.Text = "Wafer Θ scan cancelled."; return; }

                if (got)
                {
                    double r = Dist(e, c);
                    samples.Add(new WaferCentreScan.Sample(thetaDeg, e.X, e.Y));
                    if (samples.Count == 1) firstRadius = r;
                    lastRadius = r;
                    AutoLog($"Θ={thetaDeg,6:F1}°: rim ({e.X:F0}, {e.Y:F0}), r={r:F0}.");
                    station = e;   // follow the rim: the point just measured is on the crosshair
                }
                else
                {
                    missed++;
                    AutoLog($"Θ={thetaDeg,6:F1}°: no rim found — sample skipped.");
                }

                if (k < n && !await _owner.RotateThetaOnlyAsync(stepDeg, WAFER_THETA_SPEED))
                {
                    AutoLog($"Θ rotation to sample {k + 2} did not complete — run aborted.");
                    _status.Text = "Wafer Θ scan: a Θ rotation did not complete — see the log.";
                    return;
                }
            }

            // ---- Stage D: de-rotate and fit ----
            if (samples.Count < 3)
            {
                _status.Text = $"Wafer Θ scan: only {samples.Count} sample(s) found — 3 are needed to fit.";
                AutoLog($"Only {samples.Count} of {n + 1} samples found the rim. Nothing fitted.");
                return;
            }

            int expected = _owner.Calibration.RotationSign is int rs
                ? WaferCentreScan.ExpectedSign(a, rs)
                : 0;
            if (!WaferCentreScan.TryFit(samples, (long)Math.Round(c.X), (long)Math.Round(c.Y),
                                        kX, kY, expected, out WaferCentreScan.Result fit, out string? err))
            {
                AutoLog("Fit failed: " + err);
                _waferResult.Text = "Fit failed:\r\n" + err;
                _status.Text = "Wafer Θ scan: the fit failed — see the log.";
                return;
            }
            if (expected != 0 && fit.Sign != expected)
                AutoLog($"NOTE: the fit chose handedness {fit.Sign:+0;-0} but the stored RotationSign implies " +
                         $"{expected:+0;-0}. One of them is wrong — re-run the crosshair sign test.");
            foreach (double d in fit.DroppedAngles)
                AutoLog($"Dropped the Θ={d:F1}° sample as an outlier (the notch, or a bad detection).");

            // ---- Stage E: closure, then persist ----
            // The last sample returned to θ₀. Its radius must reproduce the first's; if it does not,
            // the wafer moved on the chuck (vacuum off) and every earlier sample is suspect.
            bool closed = samples.Count >= 2 && Math.Abs(lastRadius - firstRadius) <= WAFER_CLOSURE_TOL_STEPS;
            if (!closed)
                AutoLog($"CLOSURE FAILED: the return to Θ={samples[0].ThetaDeg:F1}° read r={lastRadius:F0} against " +
                         $"r={firstRadius:F0} at the start ({Math.Abs(lastRadius - firstRadius):F0} steps apart, " +
                         $"tolerance {WAFER_CLOSURE_TOL_STEPS:F0}). The wafer moved on the chuck — result NOT saved.");

            double radiusErr = fit.RadiusSteps - nominalR;
            string summary =
                $"Wafer Θ scan (N={fit.Used}, {missed} missed, {fit.DroppedAngles.Count} dropped)\r\n" +
                $"offset X={fit.OffsetXSteps:F0}  Y={fit.OffsetYSteps:F0} steps\r\n" +
                $"eccentricity {Math.Sqrt(fit.OffsetXSteps * fit.OffsetXSteps + fit.OffsetYSteps * fit.OffsetYSteps) / ((kX + kY) / 2.0):F3} mm\r\n" +
                $"R={fit.RadiusMm:F2} mm ({radiusErr:+0;-0} steps vs nominal)\r\n" +
                $"RMS={fit.RmsMm:F3} mm  handedness {fit.Sign:+0;-0}";

            if (!closed)
            {
                _waferResult.Text = "CLOSURE FAILED — not saved.\r\n" + summary;
                _status.Text = "Wafer Θ scan: closure check FAILED — the wafer moved on the chuck. Nothing saved.";
                return;
            }

            CalibrationStore cal = _owner.Calibration;
            cal.WaferOffsetX = (long)Math.Round(fit.OffsetXSteps);
            cal.WaferOffsetY = (long)Math.Round(fit.OffsetYSteps);
            cal.WaferRadius = (long)Math.Round(fit.RadiusSteps);
            cal.WaferFitSign = fit.Sign;
            cal.WaferFitRms = fit.RmsSteps;
            cal.WaferFitN = fit.Used;
            cal.WaferFitTimestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            // Snapshot the lab-frame centre at the angle the run ends on, so anything reading the
            // plain WaferCenterX/Y still gets a usable (if Θ-specific) answer.
            if (_owner.TryReadThetaNow(out long endTicks) &&
                cal.WaferCentreAt(CrosshairRotation.ChuckTicksToDegrees(endTicks, CrosshairRotation.ChuckTicksPerRev)) is { } snap)
            {
                cal.WaferCenterX = snap.X;
                cal.WaferCenterY = snap.Y;
            }
            try { cal.Save(); }
            catch (Exception ex)
            {
                _waferResult.Text = $"Computed but SAVE failed:\r\n{ex.Message}\r\n" + summary;
                _status.Text = "Wafer Θ scan: computed but the save failed — see the result panel.";
                return;
            }

            AutoLog($"Fit: offset ({fit.OffsetXSteps:F0}, {fit.OffsetYSteps:F0}) steps, R={fit.RadiusMm:F2} mm, RMS={fit.RmsMm:F3} mm.");
            _waferResult.Text = summary;
            RefreshWaferUi();
            _status.Text = $"Wafer Θ scan complete: {fit.Used} samples, RMS {fit.RmsMm:F3} mm.";
        }

        // --- Sampling ---------------------------------------------------------------

        /// <summary>
        /// One sample: park on <paramref name="station"/> and detect the rim. On a miss, searches a
        /// few hops either side along <paramref name="dir"/> — the rim can leave a 5 mm view when the
        /// eccentricity swings it, and that is far cheaper to recover from here than by going back to
        /// the chuck centre and re-probing.
        /// </summary>
        private async Task<(bool Found, (double X, double Y) Point)> SampleAtStationAsync(
            (double X, double Y) station, (double X, double Y) dir, PixelStepAffine a,
            (double X, double Y) c, double hop, double nominalR)
        {
            if (await TryDetectAtAsync(station, a, c, hop, nominalR) is { } hit) return (true, hit);

            for (int k = 1; k <= WAFER_SEARCH_HOPS; k++)
            {
                foreach (int sgn in new[] { +1, -1 })
                {
                    if (_waferCancel) return (false, default);
                    double d = sgn * k * hop;
                    (double X, double Y) t = (station.X + dir.X * d, station.Y + dir.Y * d);
                    if (!WithinTravel(t.X, t.Y, out _)) continue;
                    if (await TryDetectAtAsync(t, a, c, hop, nominalR) is { } found) return (true, found);
                }
            }
            return (false, default);
        }

        // Moves to target, verifies arrival, grabs, and returns the rim point if the detection passes
        // the border and radial-band gates. Null on anything short of that.
        private async Task<(double X, double Y)?> TryDetectAtAsync(
            (double X, double Y) target, PixelStepAffine a, (double X, double Y) c, double hop, double nominalR)
        {
            if (!WithinTravel(target.X, target.Y, out _)) return null;
            await MoveToUserAsync(target.X, target.Y);
            if (_waferCancel) return null;

            // Fresh read, not the cache: this M is what the rim point is built from.
            if (!_owner.TryReadUserXyNow(out long mx, out long my)) return null;
            double tol = Math.Max(hop * WAFER_ARRIVE_FRAC, 1.0);
            if (Math.Abs(mx - target.X) > tol || Math.Abs(my - target.Y) > tol) return null;

            AutoDetection d = await DetectEdgeAsync();
            if (!AcceptDetection(d, a, c, nominalR, mx, my, out (double X, double Y) e)) return null;

            // Confirm on a second frame without moving, so a one-frame artefact cannot enter the fit.
            AutoDetection d2 = await DetectEdgeAsync();
            if (!AcceptDetection(d2, a, c, nominalR, mx, my, out (double X, double Y) e2)) return null;
            if (Math.Abs(e2.X - e.X) > tol || Math.Abs(e2.Y - e.Y) > tol) return null;

            return e;
        }

        // Border gate + radial band. The detector's threshold is adaptive, so a frame with no rim in
        // it can still segment into something; the band is what says "this is not at wafer radius".
        private static bool AcceptDetection(
            AutoDetection d, PixelStepAffine a, (double X, double Y) c, double nominalR,
            long mx, long my, out (double X, double Y) point)
        {
            point = default;
            if (!d.Found || d.FrameW <= 0) return false;
            if (d.Row < WAFER_BORDER_MARGIN_PX || d.Row > d.FrameH - WAFER_BORDER_MARGIN_PX ||
                d.Column < WAFER_BORDER_MARGIN_PX || d.Column > d.FrameW - WAFER_BORDER_MARGIN_PX)
                return false;

            point = CentreFinder.ToStepPoint(d.Row, d.Column, d.CrossRow, d.CrossCol, a, mx, my);
            double r = Dist(point, c);
            return r >= WAFER_BAND_LO_FRAC * nominalR && r <= WAFER_BAND_HI_FRAC * nominalR;
        }

        // --- Small helpers ----------------------------------------------------------

        // The rim can only be reached where the travel from the chuck centre exceeds the wafer
        // radius. Picks the cardinal with the most headroom; the scan needs exactly one.
        private bool TryPickStationDirection(
            (double X, double Y) c, double nominalR,
            out (double X, double Y) dir, out double headroom, out string why)
        {
            dir = default; headroom = 0; why = "";
            (long min, long max)? bx = _owner.UserLimits(AxisId.X);
            (long min, long max)? by = _owner.UserLimits(AxisId.Y);
            if (bx == null || by == null) { why = "X and Y need both travel limits set — run the axis limit-find first."; return false; }

            (double X, double Y, double Room)[] options =
            [
                (+1, 0, bx.Value.max - c.X),
                (-1, 0, c.X - bx.Value.min),
                (0, +1, by.Value.max - c.Y),
                (0, -1, c.Y - by.Value.min),
            ];
            foreach ((double X, double Y, double Room) o in options)
                if (o.Room > headroom) { headroom = o.Room; dir = (o.X, o.Y); }

            if (headroom < nominalR)
            {
                why = $"The best direction reaches {headroom:N0} steps from the chuck centre but the rim is at " +
                      $"{nominalR:N0}. Check the wafer diameter and the stored travel limits.";
                return false;
            }
            return true;
        }

        private bool TryStepsPerMm(out double kX, out double kY)
        {
            kX = _owner.Calibration.For(AxisId.X).StepsPerMm ?? 0;
            kY = _owner.Calibration.For(AxisId.Y).StepsPerMm ?? 0;
            return kX > 0 && kY > 0;
        }

        private void CancelWaferScan()
        {
            if (!_waferRunning) return;
            _waferCancel = true;
            _owner.RequestStop();   // halts an in-flight move; the loop sees the flag next step
            _status.Text = "Wafer Θ scan: cancelling...";
        }

        private void RefreshWaferUi()
        {
            _waferRunBtn.Enabled = _view.IsCameraOpen && !_waferRunning && !_autoRunning;
            _waferDia.Enabled = _waferSamples.Enabled = !_waferRunning && !_autoRunning;
            _waferCancelBtn.Enabled = _waferRunning;
            // Go to Centre needs the OFFSET, not the snapshot: the target is recomputed for the
            // current Θ, which is the whole point of storing the offset.
            _waferGoBtn.Enabled = !_waferRunning && !_autoRunning && _owner.Calibration.WaferOffsetX.HasValue;
        }
    }
}
