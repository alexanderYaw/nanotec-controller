using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NanotecController
{
    /// <summary>
    /// FrmVisionProtocols — AUTOMATIC wafer centre-find by Θ scan. The chuck rim can be circled with
    /// the stage; a 200 mm wafer's rim circle exceeds the X/Y travel in both axes, so only a band is
    /// reachable and a fit over one short arc is badly conditioned.
    ///
    /// But the chuck IS the Θ axis, so the wafer is TURNED rather than circled: the stage parks on
    /// one reachable spot and Θ sweeps the whole rim past the camera. De-rotating each sample by its
    /// own angle (<see cref="WaferCentreScan"/>) puts the points back on a full 360° circle, and the
    /// same Pratt fit the chuck uses finishes the job. That spot is a CORNER of the travel, not a
    /// cardinal — no cardinal reaches a 200 mm rim, but (X min, Y max) stands ~101 mm off the axis.
    ///
    /// STEP-AND-SETTLE, exactly as the chuck run: Θ moves, stops, and only then is a frame grabbed.
    ///
    /// Shape of a run:
    ///   A  park at (X min, Y max) — the corner of the stored travel envelope
    ///   B  raster down in Y until the rim is detected; that spot is the station
    ///   C  N+1 samples, rotating Θ by 360/N between them (the last repeats θ₀ as a closure check).
    ///      Θ ONLY, unless a sample misses, when Y searches either side of the station. Every frame
    ///      is screened with the notch detector's coarse test and an anomalous one DROPPED; if it
    ///      then measures as the notch on a stationary frame, that sighting is saved with the fit
    ///   D  de-rotate + fit, which also settles the handedness and drops outliers
    ///   E  closure check, then persist the offset (chuck frame) + radius + metadata + any notch
    ///   F  drive to the wafer centre for the Θ the run ends on
    /// </summary>
    public sealed partial class FrmVisionProtocols
    {
        #region Auto wafer scan tunables

        /// <summary>Acceptance band on |E − chuck centre| as fractions of the nominal radius. Wide
        /// enough for any eccentricity that could physically sit on the chuck, tight enough to reject
        /// a detection that latched onto something else.</summary>
        private const double WAFER_BAND_LO_FRAC = 0.70;
        private const double WAFER_BAND_HI_FRAC = 1.30;

        /// <summary>A detection this close to the frame edge (px) is refused — wider than
        /// WaferEdgeDetector's own margin, a rim point that close having half its neighbourhood out
        /// of view.</summary>
        private const double WAFER_BORDER_MARGIN_PX = 8.0;

        /// <summary>Local search when a sample misses: this many hops either side of the station,
        /// along Y, down first. BOTH directions are needed — the eccentricity swings the rim radially
        /// in and out, so a downward-only search walks the wafer's interior on every outward swing.
        /// Bounded on purpose: a lost sample is cheap, a 100 mm traverse is not.</summary>
        private const int WAFER_SEARCH_HOPS = 6;

        /// <summary>Θ speed for the scan's rotations (steps/s). Θ tops out at 3200 and a revolution is
        /// 359,859 ticks, so a scan spends ~2 minutes turning whatever N is.</summary>
        private const int WAFER_THETA_SPEED = 5000;

        /// <summary>Arrival tolerance for the station moves, as a fraction of one hop.</summary>
        private const double WAFER_ARRIVE_FRAC = 0.25;

        /// <summary>The closure sample returns to θ₀; its radius must reproduce the first sample's to
        /// within this, or the wafer moved on the chuck and the whole fit is void.</summary>
        private const double WAFER_CLOSURE_TOL_STEPS = 400;

        #endregion

        #region Run state

        private volatile bool _waferCancel;
        private bool _waferRunning;

        // Scale for the coarse notch screen and for any notch measurement the scan makes. Fixed for
        // the run, so it is a field rather than a seventh parameter on the detect path.
        private double _waferUmPerPixel;

        /// <summary>What one look at the rim came to. Anomalous is NOT a miss: the rim IS in view, it
        /// just is not plain rim — the notch, debris, or a chipped edge — so the sample is dropped
        /// where a miss would send the station hunting up and down Y for a rim that is already there.</summary>
        private enum RimLook { Missed, Anomalous, Found }

        /// <summary>A notch measured on one of the dropped frames, held until the fit is saved: the
        /// apex only becomes an angle once the offset it is measured against exists.</summary>
        private readonly record struct NotchSighting(
            double ThetaDeg, double ApexRow, double ApexCol, double CrossRow, double CrossCol,
            long MotorX, long MotorY, double DepthMm, double WidthMm);

        #endregion

        #region Orchestration

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

            // A blurred rim is the one pre-condition the run cannot detect or recover from, so it is
            // the only thing asked. Answering No stops the run and says what to fix.
            if (MessageBox.Show(this, "Is the wafer in focus?",
                    "Wafer Θ scan", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                MessageBox.Show(this, "Ensure the wafer is in focus before proceeding.",
                    "Wafer Θ scan", MessageBoxButtons.OK, MessageBoxIcon.Information);
                _status.Text = "Wafer Θ scan: cancelled — focus the wafer first.";
                return;
            }

            _waferCancel = false;
            _waferRunning = true;
            _autoTarget = AutoTarget.Wafer;
            RefreshWaferUi();
            RefreshNotchUi();
            RefreshAutoUi();
            // Same lockout the chuck run takes, for the same reason: FrmMain's own busy flag drops
            // between steps, which would leave the d-pad and the polled joystick live between a move
            // and the frame paired with it.
            using IDisposable hostLock = _owner.BeginExternalOp("Auto wafer centre-find (Θ scan)");
            try
            {
                _waferLog.Clear();
                // The scan screens every frame with the notch detector, so it takes the same trigger
                // threshold the notch window offers rather than a second copy of the number.
                _waferUmPerPixel = UmPerPixel(a, kX, kY);
                _notchDetector.CoarseThresholdMm = (double)_notchThreshold.Value;
                await WaferScanCoreAsync(a, (ccx, ccy), kX, kY, nominalR, n);
            }
            finally
            {
                _waferRunning = false;
                _waferCancel = false;
                _autoTarget = AutoTarget.Chuck;
                RefreshWaferUi();
                RefreshAutoUi();
                RefreshNotchUi();
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

            // Stage A: park in the corner of the travel envelope
            (long min, long max)? bx = _owner.UserLimits(AxisId.X);
            (long min, long max)? by = _owner.UserLimits(AxisId.Y);
            if (bx == null || by == null)
            {
                AutoLog("X and Y need both travel limits set — run the axis limit-find first.");
                _status.Text = "Wafer Θ scan: X and Y need their travel limits.";
                return;
            }
            double stationX = bx.Value.min, yTop = by.Value.max, yBottom = by.Value.min;

            // The rim is only reachable where the station LINE (X fixed, Y over its travel) passes
            // through the wafer's radius. Checking it here turns a fruitless two-minute descent into
            // an immediate answer. In mm, because X and Y differ by 0.4 % in steps/mm.
            double kMean = (kX + kY) / 2.0;
            double dxMm = (stationX - c.X) / kX;
            double topMm = (yTop - c.Y) / kY, bottomMm = (yBottom - c.Y) / kY;
            double nearMm = c.Y > yTop ? topMm : c.Y < yBottom ? bottomMm : 0;
            double loMm = Math.Sqrt(dxMm * dxMm + nearMm * nearMm);
            double hiMm = Math.Max(Math.Sqrt(dxMm * dxMm + topMm * topMm),
                                   Math.Sqrt(dxMm * dxMm + bottomMm * bottomMm));
            double slackMm = hop / AUTO_HOP_FRAC / 2.0 / kMean;   // half a field of view
            double nominalRmm = nominalR / kMean;
            AutoLog($"Station line X={stationX:F0} sweeps {loMm:F1}..{hiMm:F1} mm from the rotation axis; " +
                    $"the rim is at {nominalRmm:F1} mm.");
            if (nominalRmm < loMm - slackMm || nominalRmm > hiMm + slackMm)
            {
                AutoLog($"The {nominalRmm * 2:F0} mm wafer's rim never crosses that line. Check the wafer " +
                         "diameter and the stored travel limits.");
                _status.Text = "Wafer Θ scan: the wafer rim is not reachable along the station line — see the log.";
                return;
            }

            _status.Text = "Wafer Θ scan: moving to the corner of travel...";
            await MoveToUserAsync(stationX, yTop);
            if (_waferCancel) { AutoLog("Cancelled."); _status.Text = "Wafer Θ scan cancelled."; return; }
            if (!_owner.TryReadUserXyNow(out long px, out long py))
            {
                AutoLog("Motor position unavailable after the park move — run aborted.");
                _status.Text = "Wafer Θ scan: motor position unavailable.";
                return;
            }
            if (Math.Abs(px - stationX) > AUTO_SEED_TOL || Math.Abs(py - yTop) > AUTO_SEED_TOL)
            {
                AutoLog($"Park move did not arrive (wanted {stationX:F0},{yTop:F0}; at {px},{py}) — run aborted.");
                _status.Text = "Wafer Θ scan: the park move did not arrive — see the log.";
                return;
            }
            AutoLog($"Parked at ({px}, {py}).");

            // Stage B: raster down in Y until the rim is in view
            _status.Text = "Wafer Θ scan: stepping down to find the rim...";
            (double X, double Y) station = default;
            bool acquired = false;
            int descent = 0;
            for (double y = yTop; y >= yBottom && descent < AUTO_MAX_HOPS; y -= hop, descent++)
            {
                if (_waferCancel) { AutoLog("Cancelled."); _status.Text = "Wafer Θ scan cancelled."; return; }
                AutoLog($"Descent {descent + 1}: Y={y:F0}...");
                // Anomalous counts as acquired here: the descent only wants to know WHERE the rim is,
                // and refusing the notch would carry the raster on past the rim into the wafer.
                if ((await TryDetectAtAsync((stationX, y), a, c, hop, nominalR)).Look != RimLook.Missed)
                {
                    station = (stationX, y);
                    acquired = true;
                    break;
                }
            }
            if (!acquired)
            {
                AutoLog($"No rim between Y={yTop:F0} and Y={yBottom:F0} on X={stationX:F0} after {descent} step(s).");
                _status.Text = "Wafer Θ scan: could not find the wafer rim — check focus/Z, lighting and the wafer diameter.";
                return;
            }
            AutoLog($"Rim acquired at ({station.X:F0}, {station.Y:F0}) after {descent + 1} step(s).");

            // Stage C: sample around one full revolution
            double stepDeg = 360.0 / n;
            var samples = new List<WaferCentreScan.Sample>(n + 1);
            double firstRadius = 0;
            double lastRadius = 0;
            int missed = 0, dropped = 0;
            NotchSighting? sighting = null;

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
                (RimLook look, (double X, double Y) e, double stationY) =
                    await SampleAtStationAsync(station, a, c, hop, nominalR, yBottom, yTop);
                if (_waferCancel) { AutoLog("Cancelled."); _status.Text = "Wafer Θ scan cancelled."; return; }

                // Follow the rim in Y only: the station stays on the X = X min line, and moves only as
                // far as the search had to go to find the rim again. A dropped sample still saw the
                // rim, so it still says where the station should stand.
                if (look != RimLook.Missed) station = (station.X, stationY);

                if (look == RimLook.Found)
                {
                    double r = Dist(e, c);
                    samples.Add(new WaferCentreScan.Sample(thetaDeg, e.X, e.Y));
                    if (samples.Count == 1) firstRadius = r;
                    lastRadius = r;
                    AutoLog($"Θ={thetaDeg,6:F1}°: rim ({e.X:F0}, {e.Y:F0}), r={r:F0}.");
                }
                else if (look == RimLook.Anomalous)
                {
                    dropped++;
                    AutoLog($"Θ={thetaDeg,6:F1}°: rim is not plain here — sample dropped.");
                    // The stage is standing on it, which is exactly the frame the FINE detector wants.
                    // One grab settles whether the anomaly is the notch or a speck.
                    if (sighting == null) sighting = await TryMeasureNotchAsync(thetaDeg);
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

            // Stage D: de-rotate and fit
            if (samples.Count < 3)
            {
                _status.Text = $"Wafer Θ scan: only {samples.Count} usable sample(s) — 3 are needed to fit.";
                AutoLog($"Only {samples.Count} of {n + 1} samples were usable ({missed} missed the rim, " +
                        $"{dropped} were dropped as anomalous). Nothing fitted." +
                        (dropped > missed
                            ? $" A whole scan reading as anomalous means the rim itself is reading badly, or the " +
                              $"{_notchDetector.CoarseThresholdMm:F2} mm trigger is too low for this lighting — " +
                              "one notch cannot account for more than a sample or two."
                            : ""));
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

            // Stage E: closure, then persist
            // The last sample returned to θ₀. Its radius must reproduce the first's; if it does not,
            // the wafer moved on the chuck (vacuum off) and every earlier sample is suspect.
            bool closed = samples.Count >= 2 && Math.Abs(lastRadius - firstRadius) <= WAFER_CLOSURE_TOL_STEPS;
            if (!closed)
                AutoLog($"CLOSURE FAILED: the return to Θ={samples[0].ThetaDeg:F1}° read r={lastRadius:F0} against " +
                         $"r={firstRadius:F0} at the start ({Math.Abs(lastRadius - firstRadius):F0} steps apart, " +
                         $"tolerance {WAFER_CLOSURE_TOL_STEPS:F0}). The wafer moved on the chuck — result NOT saved.");

            double radiusErr = fit.RadiusSteps - nominalR;
            string summary =
                $"Wafer Θ scan (N={fit.Used}, {missed} missed, {dropped} anomalous, " +
                $"{fit.DroppedAngles.Count} outliers)\r\n" +
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
            // plain WaferCenterX/Y still gets a usable (if Θ-specific) answer. It is also stage F's
            // target — the wafer centre for THIS Θ — so it is kept rather than re-derived.
            (long X, long Y)? centre = null;
            if (_owner.TryReadThetaNow(out long endTicks) &&
                cal.WaferCentreAt(CrosshairRotation.ChuckTicksToDegrees(endTicks, CrosshairRotation.ChuckTicksPerRev)) is { } snap)
            {
                centre = snap;
                cal.WaferCenterX = snap.X;
                cal.WaferCenterY = snap.Y;
            }

            // The notch, if one of the dropped samples turned out to be it. It goes in with the fit's
            // own Save, and only here: the apex is a motor position until there is an offset to measure
            // its bearing against, and that offset is what the lines above have just written. A scan
            // that catches it saves the notch search a whole revolution.
            string notchLine = "";
            if (sighting is { } sight)
            {
                (double X, double Y) apex = CentreFinder.ToStepPoint(
                    sight.ApexRow, sight.ApexCol, sight.CrossRow, sight.CrossCol, a, sight.MotorX, sight.MotorY);
                if (RimStation.TryChuckFrameAngle(cal, sight.ThetaDeg, apex.X, apex.Y,
                                                  out double notchDeg, out string? notchWhy))
                {
                    cal.NotchAngleDeg = notchDeg;
                    cal.NotchDepthMm = sight.DepthMm;
                    cal.NotchTimestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    notchLine = $"notch {notchDeg:F2}° (depth {sight.DepthMm:F3} mm)";
                    AutoLog($"NOTCH seen at Θ={sight.ThetaDeg:F1}° during the scan: {notchDeg:F3}° in the " +
                            $"chuck frame, depth {sight.DepthMm:F3} mm, width {sight.WidthMm:F3} mm. Saved " +
                            "with the fit — the notch search does not need to run for this wafer.");
                }
                else
                    AutoLog("A notch was measured during the scan but its angle could not be computed: " + notchWhy);
            }
            else if (cal.NotchAngleDeg is double stale)
                AutoLog($"No notch was seen this scan. The stored {stale:F2}° angle is left alone, but it " +
                        "predates this run — re-run the notch search if the wafer has been re-placed since.");

            try { cal.Save(); }
            catch (Exception ex)
            {
                _waferResult.Text = $"Computed but SAVE failed:\r\n{ex.Message}\r\n" + summary;
                _status.Text = "Wafer Θ scan: computed but the save failed — see the result panel.";
                return;
            }

            AutoLog($"Fit: offset ({fit.OffsetXSteps:F0}, {fit.OffsetYSteps:F0}) steps, R={fit.RadiusMm:F2} mm, RMS={fit.RmsMm:F3} mm.");
            _waferResult.Text = notchLine.Length > 0 ? summary + "\r\n" + notchLine : summary;
            RefreshWaferUi();   // the notch buttons wait for the finally, which is where the run ends

            // Stage F: end on the wafer centre rather than parked out on the rim. The fit is
            // already saved, so a failure here costs the position, not the measurement.
            string done = $"Wafer Θ scan complete: {fit.Used} samples, RMS {fit.RmsMm:F3} mm";
            if (centre == null)
            {
                _status.Text = done + " (Θ unavailable — stage not moved).";
                return;
            }
            if (_waferCancel)
            {
                _status.Text = done + "; cancelled before moving to the wafer centre.";
                return;
            }
            if (!WithinTravel(centre.Value.X, centre.Value.Y, out string centreWhy))
            {
                AutoLog($"Cannot move to the wafer centre — {centreWhy}. It is saved; the stage is left where it is.");
                _status.Text = done + ", but the wafer centre is outside the travel envelope.";
                return;
            }
            AutoLog($"Moving to the wafer centre ({centre.Value.X}, {centre.Value.Y})...");
            _status.Text = "Wafer Θ scan: moving to the wafer centre...";
            await MoveToUserAsync(centre.Value.X, centre.Value.Y);
            if (_owner.TryReadUserXyNow(out long wx, out long wy) &&
                (Math.Abs(wx - centre.Value.X) > AUTO_SEED_TOL || Math.Abs(wy - centre.Value.Y) > AUTO_SEED_TOL))
            {
                AutoLog($"Move to the wafer centre did not arrive (wanted {centre.Value.X},{centre.Value.Y}; at {wx},{wy}).");
                _status.Text = done + ", but the move to the wafer centre did not arrive — see the log.";
                return;
            }
            AutoLog($"At the wafer centre ({centre.Value.X}, {centre.Value.Y}).");
            _status.Text = done + ", stage at the wafer centre.";
        }

        #endregion

        #region Sampling
        /// <summary>
        /// One sample: detect the rim at <paramref name="station"/>. On a MISS, searches Y either side
        /// of it, down first, up to <see cref="WAFER_SEARCH_HOPS"/> hops each way — the eccentricity
        /// swings the rim out of a ~4 mm field as Θ turns, and it swings BOTH ways. Returns the Y it
        /// settled at so the station can follow; on a miss the station is left where it was, so one
        /// lost sample cannot strand the run away from the rim.
        ///
        /// An ANOMALOUS look ends the sample there and then, with no search: the rim is in the frame,
        /// so there is nothing to hunt for, and hunting would walk ±9 mm of Y over a feature that is
        /// only ~3 mm of rim wide and find the same anomaly again at the far end of it.
        /// </summary>
        private async Task<(RimLook Look, (double X, double Y) Point, double StationY)> SampleAtStationAsync(
            (double X, double Y) station, PixelStepAffine a, (double X, double Y) c,
            double hop, double nominalR, double yMin, double yMax)
        {
            (RimLook look, (double X, double Y) point) = await TryDetectAtAsync(station, a, c, hop, nominalR);
            if (look != RimLook.Missed) return (look, point, station.Y);

            for (int k = 1; k <= WAFER_SEARCH_HOPS; k++)
            {
                foreach (int sgn in new[] { -1, +1 })
                {
                    if (_waferCancel) return (RimLook.Missed, default, station.Y);
                    double y = station.Y + sgn * k * hop;
                    if (y < yMin || y > yMax) continue;
                    AutoLog($"  search {sgn * k:+0;-0} hop(s): Y={y:F0}...");
                    (look, point) = await TryDetectAtAsync((station.X, y), a, c, hop, nominalR);
                    if (look != RimLook.Missed) return (look, point, y);
                }
            }
            AutoLog($"  searched ±{WAFER_SEARCH_HOPS * hop:F0} steps of Y about {station.Y:F0} — no rim.");
            return (RimLook.Missed, default, station.Y);
        }

        // Moves to target, verifies arrival, grabs, and returns the rim point if the detection passes
        // the border and radial-band gates. Anomalous when the coarse notch test fires on that same
        // frame — the point is then thrown away rather than fitted, because it is a point on the
        // notch (or on debris) and not on the circle the fit is looking for.
        private async Task<(RimLook Look, (double X, double Y) Point)> TryDetectAtAsync(
            (double X, double Y) target, PixelStepAffine a, (double X, double Y) c, double hop, double nominalR)
        {
            if (!WithinTravel(target.X, target.Y, out _)) return (RimLook.Missed, default);
            await MoveToUserAsync(target.X, target.Y);
            if (_waferCancel) return (RimLook.Missed, default);

            // Fresh read, not the cache: this M is what the rim point is built from.
            if (!_owner.TryReadUserXyNow(out long mx, out long my)) return (RimLook.Missed, default);
            double tol = Math.Max(hop * WAFER_ARRIVE_FRAC, 1.0);
            if (Math.Abs(mx - target.X) > tol || Math.Abs(my - target.Y) > tol) return (RimLook.Missed, default);

            AutoDetection d = await DetectEdgeAsync(_waferUmPerPixel);
            // Logged for every frame, accepted or not: a point that lands off the rim still passes the
            // gates below, so the detector's own view of the frame is the only record of WHY.
            if (!string.IsNullOrEmpty(d.Report))
                AutoLog($"  detect @({target.X:F0}, {target.Y:F0}) px=({d.Row:F0}, {d.Column:F0})  {d.Report}");
            if (d.Anomalous)
            {
                AutoLog($"  anomalous: {d.AnomalyMm:F3} mm off straight over {d.AnomalyRun} points, past the " +
                        $"{_notchDetector.CoarseThresholdMm:F2} mm / {_notchDetector.CoarseMinRunPoints} pt trigger.");
                return (RimLook.Anomalous, default);
            }
            if (!AcceptDetection(d, a, c, nominalR, mx, my, out (double X, double Y) e)) return (RimLook.Missed, default);

            // Confirm on a second frame without moving, so a one-frame artefact cannot enter the fit.
            // Not screened again: nothing has moved, so the first frame's verdict still holds.
            AutoDetection d2 = await DetectEdgeAsync();
            if (!AcceptDetection(d2, a, c, nominalR, mx, my, out (double X, double Y) e2)) return (RimLook.Missed, default);
            if (Math.Abs(e2.X - e.X) > tol || Math.Abs(e2.Y - e.Y) > tol) return (RimLook.Missed, default);

            return (RimLook.Found, e);
        }

        // The fine measurement on a frame the coarse screen has already called anomalous. Costs one
        // grab, and answers a question the run needs anyway: an anomaly that measures as a notch is
        // the notch, and one that does not is debris — either way the sample stays dropped.
        private async Task<NotchSighting?> TryMeasureNotchAsync(double thetaDeg)
        {
            if (!_owner.TryReadUserXyNow(out long mx, out long my)) return null;

            NotchDetector.Measurement? m = await MeasureAsync(_waferUmPerPixel);
            if (m == null)
            {
                AutoLog("  not a notch (" + _notchDetector.LastReport.Trim() + ") — debris or a chipped edge.");
                return null;
            }
            AutoLog($"  NOTCH: depth {m.Value.DepthMm:F3} mm, width {m.Value.WidthMm:F3} mm — held until the fit is saved.");
            return new NotchSighting(thetaDeg, m.Value.ApexRow, m.Value.ApexCol, _lastCrossRow, _lastCrossCol,
                                     mx, my, m.Value.DepthMm, m.Value.WidthMm);
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

        #endregion

        #region Small helpers
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
            _waferRunBtn.Enabled = _view.IsCameraOpen && !_waferRunning && !_autoRunning && !_notchRunning;
            _waferDia.Enabled = _waferSamples.Enabled = !_waferRunning && !_autoRunning && !_notchRunning;
            _waferCancelBtn.Enabled = _waferRunning;
            // Go to Centre needs the OFFSET, not the snapshot: the target is recomputed for the
            // current Θ, which is the whole point of storing the offset.
            _waferGoBtn.Enabled = !_waferRunning && !_autoRunning && !_notchRunning
                                  && _owner.Calibration.WaferOffsetX.HasValue;
        }

        #endregion
    }
}
