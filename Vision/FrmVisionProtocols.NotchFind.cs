using System;
using System.Threading.Tasks;
using System.Windows.Forms;
using HalconDotNet;

namespace NanotecController
{
    // FrmVisionProtocols — NOTCH search by continuous Θ sweep.
    //
    // The notch has to be found on every wafer from an unknown orientation, so the whole 628 mm
    // circumference has to be looked at. The camera sees ~4.9 mm of rim at a time, which sounds
    // hopeless until two things are noticed:
    //
    //   1. The cost is Θ's top speed, not the number of frames. A revolution is 359,859 ticks at
    //      3200 steps/s, so ~112 s of rotation happens whatever else does. Stepping and settling
    //      ~150 times would roughly triple that; sweeping continuously adds almost nothing, because
    //      the coarse detector runs in ~130 ms against a ~710 ms per-frame budget.
    //   2. A frame only has to OVERLAP the notch, not contain it. The notch is 2.9 mm wide and the
    //      frame covers 4.9 mm of rim, so the detection window is ~7 mm and a 4 mm capture pitch is
    //      about twice as fine as it needs to be.
    //
    // Shape of a run:
    //   A  park on the rim station (X = X min; Y computed from the stored wafer offset — see
    //      Geometry/RimStation) and confirm the rim is actually there
    //   B  sweep Θ continuously, grabbing frames free-running and testing each with the COARSE
    //      detector, until one is anomalous or a full revolution has gone by
    //   C  stop, back up onto the hit, and re-measure with the FINE detector on a STATIONARY frame
    //   D  convert the apex to a chuck-frame bearing and persist it
    // Rotating to a datum is a separate button, so a run never turns the wafer as a side effect.
    //
    // The two detectors are not interchangeable and the split is the point — see NotchDetector.
    // (Partial of FrmVisionProtocols; layout lives in FrmVisionProtocols.cs.)
    public sealed partial class FrmVisionProtocols
    {
        // Overlap past a full revolution. The sweep starts from wherever Θ happens to be, so a notch
        // sitting exactly at the start angle can be half out of frame on the first look; the extra
        // arc guarantees it is seen whole on the way round.
        private const double NOTCH_SWEEP_DEGREES = 375.0;

        // The re-centring search. The STEP is the number that matters and it must be finer than the
        // window it is hunting for: the notch is only both fully enclosed and clear of the chord's
        // end anchors over 0.32-1.16° of Θ (see NotchDetector.EndSpanPoints). A 1° step walked over
        // that window on hardware and reported "ends are not plain rim" on the very frames that had
        // the notch in them. 0.25° is under even the worst case.
        // The SPAN has to cover the frame, since the coarse test can fire with the notch anywhere in
        // it: a frame is 2.2-3.0° of rim, so ±1.5° reaches either edge from the middle.
        private const double NOTCH_NUDGE_DEG = 0.25;
        private const int NOTCH_NUDGE_TRIES = 6;

        // Speed for the re-centring moves. NOT a "slow because it is delicate" number: these are
        // profile-POSITION moves, so the drive lands on the target regardless of how fast it gets
        // there, and the wafer Θ scan commands 5000 for its own 14.4° steps. An earlier 400 made the
        // whole re-centring take ~54 s and looked, correctly, like the machine crawling backwards.
        private const int NOTCH_NUDGE_SPEED = 3200;

        // The measured rim radius must stay within this of the stored radius, or the wafer has moved
        // on the chuck (vacuum loss) and every angle measured against the stored offset is void. Same
        // role as the Θ scan's closure check, and the same reason for existing.
        private const double NOTCH_RADIUS_TOL_STEPS = 2000;

        // How far the apex may sit from where the rim says it should be before the feature is refused
        // as not-on-the-rim. Budget: the stored radius is the CHUCK-side gap boundary while the apex
        // is traced on the WAFER side, a systematic ~0.3 mm, plus the radius fit (0.08 mm) and the
        // pixel→step conversion (~0.1 mm). 1000 steps is 0.79 mm — 2.6x that budget, and half the
        // 1.67 mm miss of the chuck port that was accepted as a notch on 2026-08-06.
        private const double NOTCH_APEX_TOL_STEPS = 1000;

        // Exclusion window around an anomaly already confirmed as not-a-notch. A frame covers ~2.8° of
        // rim, so ±3° recognises the same feature whether it was seen at a frame edge or its middle.
        private const double NOTCH_REJECT_WINDOW_DEG = 3.0;

        // How many anomalies may fail confirmation before the run gives up. The arc budget already
        // bounds the rotation; this bounds the CONFIRMATION time, and more importantly it draws the
        // line between "a dirty wafer" and "the rim is reading badly", which want different fixes.
        private const int NOTCH_MAX_FALSE_HITS = 8;

        // Consecutive coarse failures — a lost rim, not a clean one — before a sweep is abandoned.
        // TryCoarse returns false rather than a small residual when the contour is too short to fit,
        // precisely so this can tell "clean rim, no notch" from "no rim at all".
        private const int NOTCH_MAX_MISSES = 40;

        private readonly NotchDetector _notchDetector = new();
        private volatile bool _notchCancel;
        private bool _notchRunning;

        // Written by the grab loop, read by the sweep's stop predicate on the DRIVE thread — so both
        // are volatile. _notchHitTheta is plain: it is written before the volatile _notchHit, whose
        // release semantics publish it, and it is not read until _notchHit is seen set.
        private volatile bool _notchHit;
        private volatile int _notchMisses;
        private long _notchHitTheta;

        // --- Orchestration ----------------------------------------------------------

        private async Task RunNotchFindAsync()
        {
            if (_notchRunning || _waferRunning || _autoRunning) return;

            CalibrationStore cal = _owner.Calibration;
            if (cal.PixelStep is not PixelStepAffine a)
            { _status.Text = "Notch find: needs the camera-scale calibration first."; return; }
            if (!_view.IsCameraOpen) { _status.Text = "Notch find: the camera is not streaming."; return; }
            if (!_owner.CanMoveCalibration) { _status.Text = "Notch find: needs the drives enabled and idle."; return; }
            if (cal.WaferOffsetX is null || cal.WaferRadius is null || cal.WaferFitSign is null)
            {
                _status.Text = "Notch find: needs the wafer centre-find first.";
                MessageBox.Show(this,
                    "The notch search follows the rim using the wafer's offset from the rotation " +
                    "axis, so it needs that measured first.\r\n\r\nRun the automatic wafer " +
                    "centre-find (Θ scan), then try again.",
                    "Notch find", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (!TryStepsPerMm(out double kX, out double kY))
            { _status.Text = "Notch find: X and Y both need steps-per-mm."; return; }
            if (_owner.UserLimits(AxisId.X) is not { } bx)
            { _status.Text = "Notch find: X needs its travel limits."; return; }

            _notchCancel = false;
            _notchRunning = true;
            _notchHit = false;
            _notchMisses = 0;
            _notchDetector.CoarseThresholdMm = (double)_notchThreshold.Value;
            // DetectEdgeAsync dispatches on this, and the default is the CHUCK detector — which on a
            // wafer rim would segment on a fixed threshold and report nonsense.
            _autoTarget = AutoTarget.Wafer;
            RefreshNotchUi();
            using IDisposable hostLock = _owner.BeginExternalOp("Notch find (Θ sweep)");
            try
            {
                _notchLog.Clear();
                await NotchFindCoreAsync(cal, a, kX, kY, bx.min);
            }
            finally
            {
                _notchRunning = false;
                _notchCancel = false;
                _autoTarget = AutoTarget.Chuck;
                RefreshNotchUi();
            }
        }

        private async Task NotchFindCoreAsync(
            CalibrationStore cal, PixelStepAffine a, double kX, double kY, double stationX)
        {
            double umPerPixel = UmPerPixel(a, kX, kY);
            double radius = cal.WaferRadius!.Value;
            int speed = _owner.ThetaSpeedMax;
            double revSeconds = CrosshairRotation.ChuckTicksPerRev / (double)speed;
            NotchLog($"Scale {umPerPixel:F3} µm/px; Θ capped at {speed} steps/s, so a revolution " +
                     $"takes {revSeconds:F0} s — that is the floor, not the vision.");

            // ---- Stage A: park on the rim station ----
            if (!_owner.TryReadThetaNow(out long theta0Ticks))
            { _status.Text = "Notch find: Θ position unavailable."; return; }
            double theta0 = CrosshairRotation.ChuckTicksToDegrees(theta0Ticks, CrosshairRotation.ChuckTicksPerRev);

            // WHICH of the two rim crossings to sweep on. Both have to be tested against travel over a
            // whole revolution, because on this machine one of them fits and the other does not, and
            // no rule based on the travel limits alone picks the right one — see RimStation.
            if (_owner.UserLimits(AxisId.Y) is not { } byLim)
            { _status.Text = "Notch find: Y needs its travel limits."; return; }
            double nearestY = _owner.TryCurrentUser(AxisId.Y, out long curY) ? curY : byLim.max;

            if (!RimStation.TryChooseBranch(cal, stationX, theta0, byLim.min, byLim.max, nearestY,
                                            out double y0, out double minY, out double maxY, out string? why))
            {
                NotchLog("Cannot plan the sweep: " + why);
                _status.Text = "Notch find: no reachable rim path — see the log.";
                return;
            }
            _branchY = y0;
            NotchLog($"Station X={stationX:F0}; Y sweeps {minY:F0}..{maxY:F0} " +
                     $"({(maxY - minY) / kY:F2} mm) over the revolution, inside travel " +
                     $"{byLim.min:F0}..{byLim.max:F0}.");

            _status.Text = "Notch find: moving to the rim station...";
            await MoveToUserAsync(stationX, y0);
            if (_notchCancel) { NotchLog("Cancelled."); _status.Text = "Notch find cancelled."; return; }
            if (!_owner.TryReadUserXyNow(out long px, out long py) ||
                Math.Abs(px - stationX) > AUTO_SEED_TOL || Math.Abs(py - y0) > AUTO_SEED_TOL)
            { NotchLog("The move to the station did not arrive — run aborted."); _status.Text = "Notch find: the station move did not arrive."; return; }

            if (!await ConfirmRimAsync(umPerPixel))
            {
                NotchLog("No rim in view at the station. Check focus/Z, the lighting, and the stored wafer radius.");
                _status.Text = "Notch find: no rim at the station — see the log.";
                return;
            }
            NotchLog($"Rim acquired at ({px}, {py}).");

            // ---- Stages B and C, interleaved: sweep, confirm, and RESUME on a false hit ----
            //
            // The coarse test is deliberately loose — it has to fire on a notch that is only half in
            // frame, which costs it the order-of-magnitude margin the fine test enjoys (0.12 mm
            // against 0.046 mm of plain rim). So it WILL stop on debris, a chipped edge, or a ring
            // broken by dust. A false hit is an expected event, not an error, and abandoning the run
            // on one would make the search useless on any wafer with a speck on its rim.
            //
            // So each stop is confirmed against the fine detector, and a stop that does not confirm
            // is remembered and swept past. The arc budget is shared across all the attempts, so the
            // total rotation is still bounded by one revolution however many false hits there are.
            var rejected = new List<double>();
            double swept = 0;
            NotchDetector.Measurement? best = null;
            double bestTheta = 0;
            long bestX = 0, bestY = 0;
            double branch = _branchY;

            while (best == null && swept < NOTCH_SWEEP_DEGREES && rejected.Count < NOTCH_MAX_FALSE_HITS)
            {
                _status.Text = rejected.Count == 0
                    ? $"Notch find: sweeping (up to {revSeconds:F0} s)..."
                    : $"Notch find: resuming after {rejected.Count} false hit(s)...";
                _notchHit = false;
                _notchMisses = 0;

                double[] ignore = rejected.ToArray();
                var grabbing = new System.Threading.CancellationTokenSource();
                Task grabLoop = RunCoarseLoopAsync(umPerPixel, grabbing.Token, ignore);

                // The branch preference is FIXED, deliberately. The two rim crossings are ~130 mm
                // apart while the path moves 10 mm, so one constant preference holds the same branch
                // throughout — and the obvious alternative, "prefer the current Y", cannot be used:
                // the delegate runs on the drive thread, where TryCurrentUser reads a position cache
                // that RunDriveOp has paused for the duration and the UI thread may be writing.
                FrmMain.RimSweepResult sweep = await _owner.SweepRimAsync(
                    deg => RimStation.TryStationY(cal, deg, stationX, branch, out double y, out _) ? y : null,
                    () => _notchHit || _notchCancel || _notchMisses >= NOTCH_MAX_MISSES,
                    +1, NOTCH_SWEEP_DEGREES - swept, speed);

                grabbing.Cancel();
                try { await grabLoop; } catch (OperationCanceledException) { }
                swept += sweep.SweptDegrees;

                if (!sweep.Completed) { _status.Text = "Notch find: the sweep failed — see the main log."; return; }
                if (_notchCancel) { NotchLog("Cancelled."); _status.Text = "Notch find cancelled."; return; }
                if (_notchMisses >= NOTCH_MAX_MISSES)
                {
                    NotchLog($"Lost the rim for {_notchMisses} frames running — sweep abandoned. The rim " +
                             "has drifted out of view, so the stored wafer offset or radius is wrong.");
                    _status.Text = "Notch find: lost the rim — see the log.";
                    return;
                }
                if (!sweep.StoppedEarly) break;      // arc exhausted with nothing anomalous left

                double hitDeg = CrosshairRotation.ChuckTicksToDegrees(_notchHitTheta, CrosshairRotation.ChuckTicksPerRev);
                NotchLog($"Anomaly at Θ={hitDeg:F2}° ({swept:F1}° of arc used so far). Confirming...");

                _status.Text = "Notch find: re-measuring...";

                // Order chosen to MINIMISE TRAVEL, because every one of these is a Θ move and the
                // zig-zag is what makes re-centring feel like the machine running away backwards:
                //
                //   null  measure where the sweep actually stopped — costs NO move at all, and it is
                //         the best guess anyway. Θ over-runs by ~0.65° while ramping down, and since
                //         the sweep drives the notch INTO the frame that over-run leaves it more
                //         centred than the angle the hit was recorded at, not less.
                //   +1,+2 carry on in the direction Θ was already turning: no reversal, and it keeps
                //         pushing the notch towards the middle of the frame.
                //   -1,-2 only then come back, in one reversal rather than four.
                //
                // Walking 0,+1,-1,+2,-2,+3,-3 instead covers the same span but travels 21.6° doing
                // it, against 5.4° for this order.
                var offsets = new List<double?> { null };
                for (int k = 1; k <= NOTCH_NUDGE_TRIES; k++) offsets.Add(+k * NOTCH_NUDGE_DEG);
                for (int k = 1; k <= NOTCH_NUDGE_TRIES; k++) offsets.Add(-k * NOTCH_NUDGE_DEG);

                foreach (double? offset in offsets)
                {
                    if (best != null) break;
                    if (_notchCancel) { NotchLog("Cancelled."); _status.Text = "Notch find cancelled."; return; }

                    if (offset is double off)
                    {
                        double want = hitDeg + off;
                        if (!await GoToRimAsync(cal, stationX, want))
                        { NotchLog($"  Θ={want:F2}°: could not get back onto the rim — skipped."); continue; }
                    }
                    if (!_owner.TryReadThetaNow(out long atTicks) || !_owner.TryReadUserXyNow(out long mx, out long my))
                    { NotchLog("  position unavailable — skipping this angle."); continue; }
                    double atDeg = CrosshairRotation.ChuckTicksToDegrees(atTicks, CrosshairRotation.ChuckTicksPerRev);

                    NotchDetector.Measurement? m = await MeasureAsync(umPerPixel);
                    NotchLog($"  Θ={atDeg:F2}° ({(offset == null ? "where it stopped" : $"{offset:+0.0;-0.0}° from the hit")}): " +
                             (m == null ? "no notch — " + _notchDetector.LastReport
                                        : $"depth {m.Value.DepthMm:F3} mm, width {m.Value.WidthMm:F3} mm"));
                    if (m == null) continue;

                    // Shape passed; now WHERE it is. Every other test asks what the feature looks
                    // like, and a circular arc looks like a notch from two flank fits — so this is
                    // the one that separates the notch from a feature on the CHUCK whose dark edge
                    // has merged with the rim gap into a single region.
                    if (!ApexOnRim(cal, a, m.Value, atDeg, mx, my, radius, (kX + kY) / 2.0, out double miss))
                    {
                        NotchLog($"    ...but its apex sits {miss / ((kX + kY) / 2.0):+0.00;-0.00} mm off the rim " +
                                 $"({miss:+0;-0} steps), so it is not ON the wafer edge. Something on the chuck " +
                                 "whose dark boundary has joined the rim gap reads exactly like this.");
                        continue;
                    }
                    NotchLog($"    apex {miss / ((kX + kY) / 2.0):+0.00;-0.00} mm off the rim — on the wafer edge, as a notch must be.");
                    best = m; bestTheta = atDeg; bestX = mx; bestY = my;
                }

                if (best == null)
                {
                    rejected.Add(hitDeg);
                    NotchLog($"  Not a notch — debris, a chipped edge, or a ring broken by dust. " +
                             $"Ignoring Θ={hitDeg:F2}° ±{NOTCH_REJECT_WINDOW_DEG:F0}° and sweeping on " +
                             $"({NOTCH_SWEEP_DEGREES - swept:F0}° of arc left).");
                }
            }

            if (best == null)
            {
                if (rejected.Count >= NOTCH_MAX_FALSE_HITS)
                {
                    NotchLog($"Gave up after {rejected.Count} anomalies, none of which measured as a notch. " +
                             "That many false hits means the rim itself is reading badly — check focus, " +
                             "the lighting, and whether the sweep is blurring the edge — rather than that " +
                             "the wafer is unusually dirty.");
                    _status.Text = $"Notch find: {rejected.Count} false hits, no notch — see the log.";
                    return;
                }
                NotchLog($"Swept {swept:F1}° in total" +
                         (rejected.Count > 0 ? $", rejecting {rejected.Count} anomaly(s) on the way, " : " ") +
                         "and found no notch. Either this wafer has no notch at the rim radius being " +
                         "watched, or the coarse threshold is too high for this lighting.");
                await ReportRadiusAsync(cal, a, radius, umPerPixel);
                _status.Text = "Notch find: no notch found in a full revolution.";
                return;
            }
            if (rejected.Count > 0)
                NotchLog($"(Rejected {rejected.Count} anomaly(s) before this one.)");

            // ---- Stage D: apex -> chuck-frame bearing ----
            NotchDetector.Measurement notch = best.Value;
            (double X, double Y) apex = CentreFinder.ToStepPoint(
                notch.ApexRow, notch.ApexCol, _lastCrossRow, _lastCrossCol, a, bestX, bestY);

            if (!RimStation.TryChuckFrameAngle(cal, bestTheta, apex.X, apex.Y, out double notchDeg, out why))
            { NotchLog("Could not convert the apex to a wafer angle: " + why); _status.Text = "Notch find: the angle conversion failed."; return; }

            cal.NotchAngleDeg = notchDeg;
            cal.NotchDepthMm = notch.DepthMm;
            cal.NotchTimestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            try { cal.Save(); }
            catch (Exception ex) { NotchLog("Computed but SAVE failed: " + ex.Message); }

            NotchLog($"NOTCH at {notchDeg:F3}° in the chuck frame. depth {notch.DepthMm:F3} mm, " +
                     $"width {notch.WidthMm:F3} mm, included {notch.IncludedDeg:F1}°.");
            if (notch.ChordFitMm > 0.10)
                NotchLog($"  NOTE the baseline was {notch.ChordFitMm:F3} mm off straight, so the notch was " +
                          "off-centre in the frame. The ANGLE is still sound — it comes from the flank " +
                          "fits, which the baseline does not move — but treat the depth as approximate.");
            NotchLog($"  (found at Θ={bestTheta:F2}°; the vertex offset is {notch.VertexOffsetMm:F3} mm — " +
                      "that is the flank intersection overshooting the rounded apex, NOT a depth.)");
            _status.Text = $"Notch find: notch at {notchDeg:F2}° (depth {notch.DepthMm:F3} mm).";
            RefreshNotchUi();
        }

        /// <summary>
        /// Is this notch-shaped feature actually ON the rim? A real notch's apex sits at the rim
        /// radius less its own depth; nothing about the SHAPE says where the feature is, so this is
        /// the only test that can refuse one that is somewhere else entirely.
        ///
        /// Measured against <see cref="NotchDetector.Measurement.VertexOffsetMm"/> and not the depth,
        /// because the apex being placed IS the flank intersection, and that point is the vertex
        /// offset deep — the depth belongs to the deepest contour point, ~0.37 mm shallower. Comparing
        /// one point's radius against another point's depth spent that difference out of the budget
        /// for no reason. (When the flank fit fails the two are the same point and the same number,
        /// so the test still holds.)
        ///
        /// Returns true — cannot judge — when there is no wafer centre for this Θ.
        /// </summary>
        private bool ApexOnRim(
            CalibrationStore cal, PixelStepAffine a, NotchDetector.Measurement m,
            double atDeg, long mx, long my, double radius, double kMean, out double missSteps)
        {
            missSteps = 0;
            if (cal.WaferCentreAt(atDeg) is not (long wcx, long wcy)) return true;

            (double X, double Y) apex = CentreFinder.ToStepPoint(
                m.ApexRow, m.ApexCol, _lastCrossRow, _lastCrossCol, a, mx, my);
            missSteps = Dist(apex, (wcx, wcy)) - (radius - m.VertexOffsetMm * kMean);
            return Math.Abs(missSteps) <= NOTCH_APEX_TOL_STEPS;
        }

        // --- The sweep's grab loop --------------------------------------------------

        // Grabs frames free-running while the sweep turns, testing each with the COARSE detector.
        // Runs until cancelled; sets _notchHit (which the sweep's stop predicate reads) on the first
        // anomalous frame. Θ is taken from the sweep's published tick INSIDE the grab callback, before
        // the ~130 ms of processing, so the angle recorded belongs to the frame rather than to the
        // moment the answer came back — at 5.6 mm/s that difference would otherwise be 0.7 mm of rim.
        private async Task RunCoarseLoopAsync(
            double umPerPixel, System.Threading.CancellationToken token, double[] ignoreAngles)
        {
            int frames = 0, suppressed = 0, narrow = 0;
            double worst = 0;
            while (!token.IsCancellationRequested && !_notchHit)
            {
                (bool ok, double residual, int run, long theta) = await CoarseFrameAsync(umPerPixel);
                if (token.IsCancellationRequested) break;
                frames++;

                if (!ok) { _notchMisses++; continue; }
                _notchMisses = 0;
                if (residual > worst) worst = residual;
                if (!_notchDetector.IsAnomalous(residual, run))
                {
                    // Deep but narrow is debris, a skeleton spur, or a ring bridged across a break —
                    // never a notch, which runs to ~1100 points. Logged rather than silently dropped,
                    // because a rim throwing these repeatedly is worth knowing about.
                    if (residual > _notchDetector.CoarseThresholdMm) narrow++;
                    continue;
                }

                // An anomaly already confirmed as NOT a notch. Without this the resumed sweep would
                // stop on the same speck immediately and make no progress — the arc budget would be
                // spent re-confirming one piece of debris.
                double deg = CrosshairRotation.ChuckTicksToDegrees(theta, CrosshairRotation.ChuckTicksPerRev);
                if (IsIgnored(deg, ignoreAngles)) { suppressed++; continue; }

                _notchHitTheta = theta;
                _notchHit = true;
                NotchLog($"Frame {frames}: {residual:F3} mm deep over {run} points at Θ={deg:F2}° — past " +
                         $"{_notchDetector.CoarseThresholdMm:F2} mm / {_notchDetector.CoarseMinRunPoints} pts. Stopping.");
                break;
            }
            if (!_notchHit)
                NotchLog($"{frames} frames tested; worst residual {worst:F3} mm against a " +
                         $"{_notchDetector.CoarseThresholdMm:F2} mm threshold" +
                         (narrow > 0 ? $"; {narrow} deep-but-narrow frame(s) rejected as debris" : "") +
                         (suppressed > 0 ? $"; {suppressed} suppressed as already-rejected" : "") + ".");
        }

        // Is this angle within the exclusion window of one already rejected? The window is a little
        // wider than a frame (~2.8° of rim), so a feature seen at the edge of one frame and the
        // middle of the next is recognised as the same feature.
        private static bool IsIgnored(double deg, double[] ignoreAngles)
        {
            foreach (double bad in ignoreAngles)
            {
                double d = Math.Abs(((deg - bad) % 360.0 + 540.0) % 360.0 - 180.0);
                if (d <= NOTCH_REJECT_WINDOW_DEG) return true;
            }
            return false;
        }

        // One free-running grab + coarse test. Returns the Θ sampled at grab time.
        private async Task<(bool Ok, double ResidualMm, int RunPoints, long ThetaTicks)> CoarseFrameAsync(
            double umPerPixel)
        {
            if (!_view.IsCameraOpen) return (false, 0, 0, 0);

            var tcs = new TaskCompletionSource<(bool, double, int, long)>(TaskCreationOptions.RunContinuationsAsynchronously);
            _view.RequestFrame(frame =>
            {
                // Guarded whole: anything escaping here is swallowed by GrabLoop, which would leave
                // the TCS uncompleted and stall the sweep until its own timeout.
                try
                {
                    long theta = _owner.SweepThetaTicks;
                    bool ok;
                    double residual = 0;
                    int run = 0;
                    try { ok = _notchDetector.TryCoarse(frame, umPerPixel, out residual, out run); }
                    catch (HOperatorException) { ok = false; }
                    tcs.TrySetResult((ok, residual, run, theta));
                }
                catch (HOperatorException) { tcs.TrySetResult((false, 0, 0, 0)); }
            });

            using var timeout = new System.Threading.CancellationTokenSource();
            Task done = await Task.WhenAny(tcs.Task, Task.Delay(AUTO_GRAB_TIMEOUT_MS, timeout.Token));
            if (!ReferenceEquals(done, tcs.Task)) return (false, 0, 0, 0);
            timeout.Cancel();
            return await tcs.Task;
        }

        // --- Stationary helpers -----------------------------------------------------

        // Full measurement on one stationary frame.
        private async Task<NotchDetector.Measurement?> MeasureAsync(double umPerPixel)
        {
            if (!_view.IsCameraOpen) return null;

            var tcs = new TaskCompletionSource<NotchDetector.Measurement?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _view.RequestFrame(frame =>
            {
                try
                {
                    HOperatorSet.GetImageSize(frame, out HTuple fw, out HTuple fh);
                    _lastCrossRow = fh.D / 2.0;
                    _lastCrossCol = fw.D / 2.0;
                    NotchDetector.Measurement? result = null;
                    try
                    {
                        if (_notchDetector.TryMeasure(frame, umPerPixel, out NotchDetector.Measurement m))
                            result = m;
                    }
                    catch (HOperatorException) { }
                    tcs.TrySetResult(result);
                }
                catch (HOperatorException) { tcs.TrySetResult(null); }
            });

            using var timeout = new System.Threading.CancellationTokenSource();
            Task done = await Task.WhenAny(tcs.Task, Task.Delay(AUTO_GRAB_TIMEOUT_MS, timeout.Token));
            if (!ReferenceEquals(done, tcs.Task)) return null;
            timeout.Cancel();
            return await tcs.Task;
        }

        // Is there a rim in front of us at all? Uses the coarse path, which needs a long contour and
        // so fails on a frame with no rim — exactly the question being asked.
        private async Task<bool> ConfirmRimAsync(double umPerPixel)
        {
            (bool ok, double residual, int run, _) = await CoarseFrameAsync(umPerPixel);
            if (ok) NotchLog($"Rim check: residual {residual:F3} mm over {run} points.");
            return ok;
        }

        // Turns Θ to want, then puts Y back on the rim for that angle.
        private async Task<bool> GoToRimAsync(CalibrationStore cal, double stationX, double want)
        {
            if (!_owner.TryReadThetaNow(out long nowTicks)) return false;
            double now = CrosshairRotation.ChuckTicksToDegrees(nowTicks, CrosshairRotation.ChuckTicksPerRev);
            // Shortest way round. Both angles are already folded to [0,360) by ChuckTicksToDegrees,
            // so without this a move from 359° to 1° would go the long way — 358° of it.
            double delta = ((want - now) % 360.0 + 540.0) % 360.0 - 180.0;
            if (Math.Abs(delta) > 1e-3)
            {
                NotchLog($"    Θ {now:F2}° -> {want:F2}° ({delta:+0.00;-0.00}°)");
                if (!await _owner.RotateThetaOnlyAsync(delta, NOTCH_NUDGE_SPEED)) return false;
            }

            if (!_owner.TryReadThetaNow(out long atTicks)) return false;
            double at = CrosshairRotation.ChuckTicksToDegrees(atTicks, CrosshairRotation.ChuckTicksPerRev);
            if (!RimStation.TryStationY(cal, at, stationX, _branchY, out double y, out _))
                return false;
            if (!WithinTravel(stationX, y, out _)) return false;
            await MoveToUserAsync(stationX, y);
            return true;
        }

        // Radius sanity after a fruitless sweep: a wafer that shifted on the chuck explains "no notch"
        // far better than a wafer with no notch does.
        private async Task ReportRadiusAsync(CalibrationStore cal, PixelStepAffine a, double radius, double umPerPixel)
        {
            AutoDetection d = await DetectEdgeAsync();
            if (!d.Found || !_owner.TryReadUserXyNow(out long mx, out long my)) return;
            if (!_owner.TryReadThetaNow(out long tTicks)) return;
            double deg = CrosshairRotation.ChuckTicksToDegrees(tTicks, CrosshairRotation.ChuckTicksPerRev);
            if (cal.WaferCentreAt(deg) is not (long wcx, long wcy)) return;

            (double X, double Y) e = CentreFinder.ToStepPoint(d.Row, d.Column, d.CrossRow, d.CrossCol, a, mx, my);
            double r = Dist(e, (wcx, wcy));
            NotchLog($"Rim radius now {r:F0} steps against the stored {radius:F0} ({r - radius:+0;-0}).");
            if (Math.Abs(r - radius) > NOTCH_RADIUS_TOL_STEPS)
                NotchLog("  The wafer has MOVED on the chuck — re-run the wafer centre-find before " +
                         "trusting anything measured this run.");
        }

        // --- Check ------------------------------------------------------------------

        // Search either side of where the stored angle says the notch is. The STEP is a frame's worth
        // of rim rather than the find's 0.25°, because this is not hunting for the enclosure window —
        // it is asking how far off the angle is, and anything past ~1.4° is out of frame entirely.
        // The SPAN is deliberately wider than the error anyone would call "a few degrees", so the
        // check can quantify a bias instead of reporting a bare "not found".
        private const double NOTCH_CHECK_STEP_DEG = 0.5;
        private const double NOTCH_CHECK_SPAN_DEG = 4.0;

        /// <summary>
        /// Measures the stored notch angle against the wafer, and reports the difference. Turns Θ so
        /// the notch comes round to the camera station, re-measures it there, and converts the apex
        /// back to a chuck-frame bearing — the same conversion the search made, from an independent
        /// frame at a different Θ. A residual near zero says the stored angle and the whole Θ chain
        /// are self-consistent, so a datum that looks wrong is wrong OUTSIDE the machine; a residual
        /// of degrees is the correction, and its sign says which way.
        ///
        /// This is not circular. Where the notch is DRIVEN to depends on the stored angle, but what
        /// is measured there does not: an angle wrong by ψ leaves the notch ψ off the crosshair, and
        /// the measurement reads that ψ back.
        /// </summary>
        private async Task RunNotchCheckAsync()
        {
            if (_notchRunning || _waferRunning || _autoRunning) return;

            CalibrationStore cal = _owner.Calibration;
            if (cal.NotchAngleDeg is not double stored)
            { _status.Text = "Check notch: no notch angle stored — run the notch find first."; return; }
            if (cal.PixelStep is not PixelStepAffine a)
            { _status.Text = "Check notch: needs the camera-scale calibration first."; return; }
            if (!_view.IsCameraOpen) { _status.Text = "Check notch: the camera is not streaming."; return; }
            if (!_owner.CanMoveCalibration) { _status.Text = "Check notch: needs the drives enabled and idle."; return; }
            if (!TryStepsPerMm(out double kX, out double kY))
            { _status.Text = "Check notch: X and Y both need steps-per-mm."; return; }
            if (_owner.UserLimits(AxisId.X) is not { } bx || _owner.UserLimits(AxisId.Y) is not { } by)
            { _status.Text = "Check notch: X and Y need their travel limits."; return; }

            _notchCancel = false;
            _notchRunning = true;
            _autoTarget = AutoTarget.Wafer;
            RefreshNotchUi();
            using IDisposable hostLock = _owner.BeginExternalOp("Notch check");
            try
            {
                _notchLog.Clear();
                await NotchCheckCoreAsync(cal, a, kX, kY, bx.min, by.min, by.max, stored);
            }
            finally
            {
                _notchRunning = false;
                _notchCancel = false;
                _autoTarget = AutoTarget.Chuck;
                RefreshNotchUi();
            }
        }

        private async Task NotchCheckCoreAsync(
            CalibrationStore cal, PixelStepAffine a, double kX, double kY,
            double stationX, double travelMinY, double travelMaxY, double stored)
        {
            double umPerPixel = UmPerPixel(a, kX, kY);
            double mmPerDeg = Math.PI * (cal.WaferRadius!.Value / ((kX + kY) / 2.0)) / 180.0;
            int sign = cal.WaferFitSign ?? +1;

            if (!_owner.TryReadThetaNow(out long nowTicks))
            { _status.Text = "Check notch: Θ position unavailable."; return; }
            double now = CrosshairRotation.ChuckTicksToDegrees(nowTicks, CrosshairRotation.ChuckTicksPerRev);

            double nearestY = _owner.TryCurrentUser(AxisId.Y, out long curY) ? curY : travelMaxY;
            if (!RimStation.TryChooseBranch(cal, stationX, now, travelMinY, travelMaxY, nearestY,
                                            out double y0, out _, out _, out string? why))
            { NotchLog("Cannot plan the check: " + why); _status.Text = "Check notch: no reachable rim path — see the log."; return; }
            _branchY = y0;

            // The Θ that brings the notch to the station. Solved rather than assumed: the station's
            // bearing depends on the Θ it is evaluated at, so this is a fixed point — but a weak one
            // (the bearing drifts only ±atan(e/R)), so two passes are already at the noise floor.
            double target = now, bearing = 0;
            for (int i = 0; i < 3; i++)
            {
                if (!RimStation.TryStationBearing(cal, target, stationX, _branchY, out bearing, out _, out why))
                { NotchLog("Cannot work out where the station bears: " + why); _status.Text = "Check notch: the station bearing failed — see the log."; return; }
                target = ((sign * (bearing - stored)) % 360.0 + 360.0) % 360.0;
            }
            double viewTilt = CameraFrame.TiltDeg(cal) ?? 0;
            NotchLog($"The station bears {bearing:F2}° from the wafer centre at Θ={target:F2}° " +
                     $"({CameraFrame.ToView(bearing, viewTilt):F2}° in the view frame the datum uses), and the " +
                     $"stored notch is {stored:F2}° in the chuck frame — so Θ={target:F2}° should put the notch " +
                     $"on the crosshair. One degree is {mmPerDeg:F2} mm of rim; the frame holds about 4.9 mm.");
            _status.Text = "Check notch: turning the notch to the camera...";

            // Where the stored angle says it is, then out either way. Same order as the search's
            // re-centring and for the same reason — no reversal until the forward side is exhausted.
            var offsets = new List<double> { 0 };
            for (double d = NOTCH_CHECK_STEP_DEG; d <= NOTCH_CHECK_SPAN_DEG + 1e-9; d += NOTCH_CHECK_STEP_DEG)
            { offsets.Add(+d); offsets.Add(-d); }

            foreach (double off in offsets)
            {
                if (_notchCancel) { NotchLog("Cancelled."); _status.Text = "Check notch cancelled."; return; }

                double want = ((target + off) % 360.0 + 360.0) % 360.0;
                if (!await GoToRimAsync(cal, stationX, want))
                { NotchLog($"  Θ={want:F2}°: could not get onto the rim — skipped."); continue; }
                if (!_owner.TryReadThetaNow(out long atTicks) || !_owner.TryReadUserXyNow(out long mx, out long my))
                { NotchLog("  position unavailable — skipping this angle."); continue; }
                double atDeg = CrosshairRotation.ChuckTicksToDegrees(atTicks, CrosshairRotation.ChuckTicksPerRev);

                NotchDetector.Measurement? m = await MeasureAsync(umPerPixel);
                NotchLog($"  Θ={atDeg:F2}° ({off:+0.0;-0.0}° from the target): " +
                         (m == null ? "no notch — " + _notchDetector.LastReport
                                    : $"depth {m.Value.DepthMm:F3} mm, width {m.Value.WidthMm:F3} mm"));
                if (m == null) continue;
                // Same position test the search applies, for the same reason: a chuck feature that
                // reads as a notch would otherwise be reported as a wafer angle discrepancy.
                if (!ApexOnRim(cal, a, m.Value, atDeg, mx, my, cal.WaferRadius!.Value, (kX + kY) / 2.0, out double miss))
                {
                    NotchLog($"    ...but its apex is {miss / ((kX + kY) / 2.0):+0.00;-0.00} mm off the rim — not the notch.");
                    continue;
                }

                (double X, double Y) apex = CentreFinder.ToStepPoint(
                    m.Value.ApexRow, m.Value.ApexCol, _lastCrossRow, _lastCrossCol, a, mx, my);
                if (!RimStation.TryChuckFrameAngle(cal, atDeg, apex.X, apex.Y, out double measured, out why))
                { NotchLog("Could not convert the apex to a wafer angle: " + why); _status.Text = "Check notch: the angle conversion failed."; return; }

                double err = ((measured - stored) % 360.0 + 540.0) % 360.0 - 180.0;
                NotchLog($"MEASURED {measured:F3}° against the stored {stored:F3}° — off by " +
                         $"{err:+0.000;-0.000}° ({err * mmPerDeg:+0.00;-0.00} mm of rim).");

                // Draw what the machine believes, so it can be compared with what is on screen.
                if (cal.WaferCentreAt(atDeg) is (long wcx, long wcy) &&
                    TryRadialPixels(a, apex, (wcx, wcy), kX, kY, out double dirRow, out double dirCol))
                {
                    ShowNotchOverlay(m.Value, dirRow, dirCol);
                    NotchLog($"  The notch's axis lies {Math.Atan2(-dirRow, dirCol) * 180.0 / Math.PI:F1}° from " +
                             "horizontal in the Captured pane (cyan line, apex in yellow). That is the OUTWARD " +
                             "RADIAL at this station, and a notch points outwards, so it is the angle the notch " +
                             "must appear at whatever datum is asked for — it is set by where the camera stands " +
                             "on the wafer, not by the notch. The live pane shows the same rim turned 180° (the " +
                             "camera is mounted inverted), so the notch points the opposite way there.");
                }
                NotchLog(Math.Abs(err) * mmPerDeg < 0.5
                    ? "  That is inside the measurement's own noise, so the stored angle and the Θ chain " +
                      "agree with each other. A datum that still looks wrong is wrong OUTSIDE this chain — " +
                      "the reference it is being judged against, not the number."
                    : "  That is a real discrepancy. Re-run it after turning Θ well away and back: an error " +
                      "that repeats with the same sign is a bias in the stored angle (re-run the notch " +
                      "find), while one that changes with the approach direction is backlash or slip " +
                      "between the encoder and the chuck, which no stored number can fix.");
                _status.Text = $"Check notch: stored angle is {err:+0.00;-0.00}° out ({err * mmPerDeg:+0.00;-0.00} mm of rim).";
                return;
            }

            NotchLog($"No notch found within ±{NOTCH_CHECK_SPAN_DEG:F0}° of where the stored angle puts it " +
                     $"(±{NOTCH_CHECK_SPAN_DEG * mmPerDeg:F0} mm of rim). Either the stored angle is out by more " +
                     "than that, the wafer has been re-placed since it was measured, or the rim is not reading " +
                     "at this station — the per-angle lines above say which.");
            _status.Text = "Check notch: the notch is not where the stored angle says — see the log.";
        }

        // The OUTWARD RADIAL at a rim point, as a unit direction in IMAGE space (row, col) — where a
        // notch's axis of symmetry has to lie, since a notch points at the wafer centre. Built in mm
        // (X and Y differ by 0.4 % in steps/mm, so a direction is only a direction there), scaled back
        // to steps, then through A⁻¹ into pixels.
        private static bool TryRadialPixels(
            PixelStepAffine a, (double X, double Y) point, (long X, long Y) centre,
            double kX, double kY, out double dirRow, out double dirCol)
        {
            dirRow = dirCol = 0;
            double vx = (point.X - centre.X) / kX, vy = (point.Y - centre.Y) / kY;
            double n = Math.Sqrt(vx * vx + vy * vy);
            double det = a.Xr * a.Yc - a.Xc * a.Yr;
            if (n < 1e-9 || Math.Abs(det) < 1e-9) return false;

            double sx = vx / n * kX, sy = vy / n * kY;
            double row = (a.Yc * sx - a.Xc * sy) / det, col = (-a.Yr * sx + a.Xr * sy) / det;
            double m = Math.Sqrt(row * row + col * col);
            if (m < 1e-9) return false;
            dirRow = row / m; dirCol = col / m;
            return true;
        }

        // Posts one fresh frame to the Captured pane with the apex and that radial drawn on it. The
        // stage is stationary, so the frame is the same scene the measurement ran on.
        private void ShowNotchOverlay(NotchDetector.Measurement m, double dirRow, double dirCol)
        {
            _view.RequestFrame(frame =>
            {
                try
                {
                    _view.PostFrameBitmap(frame, flip: false, raw =>
                    {
                        if (IsDisposed) { raw.Dispose(); return; }
                        raw = VisionOverlay.EnsureDrawable(raw);
                        using (var g = System.Drawing.Graphics.FromImage(raw))
                        {
                            float pen = VisionOverlay.PenWidth(raw.Width);
                            float len = raw.Width / 6f;
                            using var axis = new System.Drawing.Pen(System.Drawing.Color.Cyan, pen);
                            g.DrawLine(axis,
                                (float)(m.ApexCol - dirCol * len), (float)(m.ApexRow - dirRow * len),
                                (float)(m.ApexCol + dirCol * len), (float)(m.ApexRow + dirRow * len));
                            VisionOverlay.DrawPoint(g, m.ApexRow, m.ApexCol, raw.Width / 60f,
                                                    System.Drawing.Color.Yellow, pen);
                            VisionOverlay.DrawCrosshair(g, raw.Width, _lastCrossRow, _lastCrossCol,
                                                        System.Drawing.Color.Lime);
                        }
                        ShowCaptured(raw);
                    });
                }
                catch (HOperatorException) { }
            });
        }

        // --- Datum ------------------------------------------------------------------

        private async Task RotateNotchToDatumAsync()
        {
            CalibrationStore cal = _owner.Calibration;
            if (cal.NotchAngleDeg is not double notch)
            { _status.Text = "Rotate to datum: no notch angle stored — run the notch find first."; return; }
            if (!_owner.CanMoveCalibration) { _status.Text = "Rotate to datum: needs the drives enabled and idle."; return; }

            if (!_owner.TryReadThetaNow(out long nowTicks))
            { _status.Text = "Rotate to datum: Θ position unavailable."; return; }
            double now = CrosshairRotation.ChuckTicksToDegrees(nowTicks, CrosshairRotation.ChuckTicksPerRev);
            int sign = cal.WaferFitSign ?? +1;

            // The datum is read in the CAMERA's frame — the bearing as it appears on the live view —
            // because that is the frame the operator works in. The conversion is one angle, the
            // camera's mounting tilt (CameraFrame), so the chuck turns by that much more and the
            // wafer ends up square to the VIEW rather than to the stage.
            //
            // Underneath it is still a lab bearing. The stored notch angle is a CHUCK-frame bearing,
            // invariant as Θ turns; the two are related by
            //     lab = chuckFrame + sign·Θ            (the same relation WaferCentreAt applies)
            // so the Θ that puts the notch on the datum is sign·(labDatum − notch), and the move is
            // the difference from where Θ stands NOW.
            //
            // Θ and sign are both load-bearing and both were missing when this first shipped.
            // Dropping Θ treats a target as a delta, so the answer is only right when Θ happens to
            // read 0; dropping sign turns the wrong way entirely on this machine, where WaferFitSign
            // is −1.
            double tilt = CameraFrame.TiltDeg(cal) ?? 0;
            double datum = (double)_notchDatum.Value;
            double labDatum = CameraFrame.ToLab(datum, tilt);
            double nowLab = ((notch + sign * now) % 360.0 + 360.0) % 360.0;
            double targetTheta = sign * (labDatum - notch);
            double delta = ((targetTheta - now) % 360.0 + 540.0) % 360.0 - 180.0;
            NotchLog($"Notch bears {CameraFrame.ToView(nowLab, tilt):F2}° in the view now " +
                     $"({nowLab:F2}° in the machine frame; chuck-frame {notch:F2}°, Θ={now:F2}°, " +
                     $"sign {sign:+0;-0}).");
            NotchLog($"The camera is mounted {tilt:+0.00;-0.00}° off the machine's axes, so the {datum:F1}° " +
                     $"datum is {labDatum:F2}° in the machine frame; turning Θ by {delta:+0.00;-0.00}°.");
            _status.Text = $"Rotate to datum: turning {delta:+0.0;-0.0}°...";
            bool ok = await _owner.RotateThetaOnlyAsync(delta, NOTCH_NUDGE_SPEED);
            _status.Text = ok
                ? $"Notch at the {datum:F1}° datum (view frame; {labDatum:F1}° machine)."
                : "Rotate to datum: the move did not complete — see the main log.";
        }

        // --- Small helpers ----------------------------------------------------------

        // Crosshair of the frame the last measurement ran on. ToStepPoint needs the same crosshair the
        // apex was measured against, and the measurement path does not go through AutoDetection.
        private double _lastCrossRow, _lastCrossCol;

        // Micrometres per pixel, from the affine and steps-per-mm. Averaged over the two image axes:
        // the affine's row and column vectors differ by ~1% here, and a residual measured across a
        // contour running diagonally through the frame is aligned with neither.
        private static double UmPerPixel(PixelStepAffine a, double kX, double kY)
        {
            double colX = a.Xc / kX, colY = a.Yc / kY;     // mm of stage per pixel COLUMN
            double rowX = a.Xr / kX, rowY = a.Yr / kY;     // ...and per pixel ROW
            double col = Math.Sqrt(colX * colX + colY * colY);
            double row = Math.Sqrt(rowX * rowX + rowY * rowY);
            return (col + row) / 2.0 * 1000.0;
        }

        // Which of the station line's two rim crossings this run settled on (RimStation.TryChooseBranch,
        // stage A). Held for the run so the sweep, and every re-centring nudge afterwards, stay on the
        // same branch — a nudge that flipped branch would jump the stage across the wafer.
        private double _branchY;

        private void NotchLog(string line)
        {
            if (IsDisposed) return;
            if (_notchLog.InvokeRequired) { _notchLog.BeginInvoke(new Action<string>(NotchLog), line); return; }
            _notchLog.AppendText(line + Environment.NewLine);
        }

        private void CancelNotchFind()
        {
            if (!_notchRunning) return;
            _notchCancel = true;
            _owner.RequestStop();
            _status.Text = "Notch find: cancelling...";
        }

        private void RefreshNotchUi()
        {
            bool idle = !_notchRunning && !_waferRunning && !_autoRunning;
            _notchRunBtn.Enabled = _view.IsCameraOpen && idle;
            _notchCancelBtn.Enabled = _notchRunning;
            _notchDatum.Enabled = _notchThreshold.Enabled = !_notchRunning;
            _notchGoBtn.Enabled = idle && _owner.Calibration.NotchAngleDeg.HasValue;
            // The check re-measures, so unlike the datum move it needs the camera.
            _notchCheckBtn.Enabled = _view.IsCameraOpen && idle && _owner.Calibration.NotchAngleDeg.HasValue;
        }
    }
}
