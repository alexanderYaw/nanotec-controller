using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using HalconDotNet;

namespace NanotecController
{
    /// <summary>
    /// FrmVisionProtocols — AUTOMATIC chuck centre-find: the stage homes itself, drives to a fixed
    /// seed point and collects the eight rim points on its own, with no operator input beyond the max
    /// search radius. The points go into the same _chuckFinder and the same Pratt fit as the manual
    /// flow; this is purely the orchestration that used to be the operator's hand on the jog.
    ///
    /// STEP-AND-SETTLE: every probe advances in discrete hops and captures with the stage stopped, so
    /// the motor position paired with each frame is exact and the travel guard is inherent — a target
    /// past the guard is never even COMMANDED. A continuous jog gives neither.
    ///
    /// Shape of a run:
    ///   0  X and Y Home, then AUTO_SEED_DY along Y   → the seed point, in place of a rough centre
    ///   A  probe ±Y from the seed point              → bisect for cy
    ///   B  probe ±X from (seedX, cy)                 → bisect for cx, giving C₁ and a radius
    ///   C  probe the four diagonals from C₁          → four more rim points
    ///   D  Pratt-fit all eight; persist centre + radius
    ///   E  report per-point radial residuals
    ///   F  drive to the fitted centre                → the run ends with the chuck centred
    /// </summary>
    public sealed partial class FrmVisionProtocols
    {
        #region Auto centre-find tunables

        /// <summary>Offset along +Y from Home to the seed point. Home is repeatable, so it replaces the
        /// operator's eye as the rough centre. USER frame (userY = −rawY). Z is NOT homed — it holds
        /// the focus the detector needs.</summary>
        private const double AUTO_SEED_DY = +15000;

        /// <summary>Arrival tolerance for the two seed moves — a plain absolute band, since the probes
        /// size theirs from a hop not known until the first frame.</summary>
        private const double AUTO_SEED_TOL = 50;

        /// <summary>Stage C only: jump straight to this multiple of the MEASURED radius before hopping,
        /// skipping the empty interior. Safe only because C₁ came from the bisection rather than a
        /// guess — from a rough centre off by δ the jump can land past the boundary, skipping it UNSEEN.</summary>
        private const double AUTO_APPROACH_R = 0.8;

        /// <summary>Distance-band LOWER bounds. Both upper bounds are the flat max search radius the
        /// operator enters, so a detection is rejected on distance only for being implausibly CLOSE to
        /// the start. That makes the max search radius the single number governing a probe's reach —
        /// but it also means stage C no longer re-checks a diagonal against the measured radius.</summary>
        private const double AUTO_BAND_LO_FRAC = 0.2;    // stages A/B, fraction of the max radius
        private const double AUTO_TIGHT_LO_FRAC = 0.7;   // stage C, fraction of the measured r₁

        /// <summary>One hop, as a fraction of the frame's smaller extent in step space. Must stay well
        /// under a full frame: a bigger hop can carry the rim past the camera between captures, and a
        /// rim merely clipping a corner does not meet ChuckEdgeDetector's MinArcLength.</summary>
        private const double AUTO_HOP_FRAC = 0.4;

        /// <summary>Backstop on one probe's hop count, should the guard arithmetic ever be defeated by
        /// a degenerate hop size.</summary>
        private const int AUTO_MAX_HOPS = 500;

        /// <summary>Give up waiting on a detection job — the grab thread can drop one silently, which
        /// would otherwise hang the run forever.</summary>
        private const int AUTO_GRAB_TIMEOUT_MS = 8000;

        private volatile bool _autoCancel;
        private bool _autoRunning;

        private enum ProbeOutcome { Found, Missed, Aborted }

        /// <summary>Which rim <see cref="DetectEdgeAsync"/> looks for. The chuck and wafer edges need
        /// different detectors (fixed vs adaptive threshold) but the same hop / band / confirm
        /// machinery, so the target is a field rather than a second copy of ProbeAsync.</summary>
        private enum AutoTarget { Chuck, Wafer }

        private AutoTarget _autoTarget = AutoTarget.Chuck;

        /// <summary>One detection job's result, carried back off the grab thread. FrameW/FrameH are the
        /// LIVE frame size (a centred-ROI crop, so it shrinks with zoom) and size the hop. Report is the
        /// wafer detector's per-region diagnostic, null for the chuck target. Anomalous is the coarse
        /// notch test's verdict on the SAME frame, filled in only when the caller asks for it.</summary>
        private readonly record struct AutoDetection(
            bool Found, double Row, double Column, double CrossRow, double CrossCol,
            double FrameW, double FrameH, string? Report = null,
            bool Anomalous = false, double AnomalyMm = 0, int AnomalyRun = 0);

        #endregion

        #region Awaitable grab + detect
        // The same detector call as RequestEdge, but awaitable, with the result handed back instead of
        // stored. The overlay still lands on the captured pane so the operator can watch each hop.
        // Returns a default (FrameW = 0) if the camera is closed or the job never comes back.
        //
        // screenUmPerPixel > 0 additionally runs the COARSE notch test on the same frame, so a rim
        // carrying the notch — or debris, or a chip — is recognised as such instead of being measured
        // as if it were plain rim. It is the same frame deliberately: a verdict from a second grab
        // would not belong to the point being judged.
        private async Task<AutoDetection> DetectEdgeAsync(double screenUmPerPixel = 0)
        {
            if (!_view.IsCameraOpen) return default;

            var tcs = new TaskCompletionSource<AutoDetection>(TaskCreationOptions.RunContinuationsAsynchronously);
            _view.RequestFrame(frame =>
            {
                // The WHOLE job body is guarded: anything that escapes here is swallowed by GrabLoop,
                // which would leave the TCS uncompleted and stall the probe for the full timeout.
                // GetImageSize sitting outside this try was exactly that hole.
                try
                {
                    HOperatorSet.GetImageSize(frame, out HTuple fw, out HTuple fh);
                    double crossRow = fh.D / 2.0, crossCol = fw.D / 2.0;
                    bool found;
                    double edgeRow = 0, edgeCol = 0;
                    string? report = null;
                    try
                    {
                        if (_autoTarget == AutoTarget.Wafer)
                        {
                            found = _waferDetector.TryDetect(frame, crossRow, crossCol, out WaferEdgeDetector.EdgePoint w);
                            edgeRow = w.Row; edgeCol = w.Column;
                            report = _waferDetector.LastReport;
                        }
                        else
                        {
                            found = _edgeDetector.TryDetect(frame, crossRow, crossCol, out ChuckEdgeDetector.EdgePoint c);
                            edgeRow = c.Row; edgeCol = c.Column;
                        }
                    }
                    catch (HOperatorException) { found = false; }

                    // Only worth asking of a frame that HAS a rim in it: on an empty frame the coarse
                    // test fails on contour length anyway, and it costs ~230 ms to say so.
                    bool anomalous = false;
                    double anomalyMm = 0;
                    int anomalyRun = 0;
                    if (found && screenUmPerPixel > 0)
                    {
                        try
                        {
                            if (_notchDetector.TryCoarse(frame, screenUmPerPixel, out anomalyMm, out anomalyRun))
                                anomalous = _notchDetector.IsAnomalous(anomalyMm, anomalyRun);
                        }
                        catch (HOperatorException) { }
                    }

                    var result = new AutoDetection(found, edgeRow, edgeCol, crossRow, crossCol, fw.D, fh.D,
                                                   report, anomalous, anomalyMm, anomalyRun);
                    _view.PostFrameBitmap(frame, flip: false, raw =>
                    {
                        if (IsDisposed) { raw.Dispose(); tcs.TrySetResult(result); return; }
                        if (found)
                        {
                            // Returns the bitmap drawn on — a mono frame is indexed and is
                            // replaced by a drawable copy, so keep the returned reference.
                            raw = DrawEdgeOverlay(raw, result.Row, result.Column, crossRow, crossCol);
                        }
                        else
                        {
                            raw = VisionOverlay.EnsureDrawable(raw);
                            using (var g = Graphics.FromImage(raw))
                                VisionOverlay.DrawCrosshair(g, raw.Width, crossRow, crossCol, Color.Lime);
                        }
                        ShowCaptured(raw);
                        tcs.TrySetResult(result);
                    });
                }
                catch (HOperatorException) { tcs.TrySetResult(default); }
            });

            // Cancel the timeout on the fast path: an uncancelled Task.Delay keeps a live timer (and
            // everything its continuation captures) for the full 8 s on EVERY hop of every probe.
            using var timeout = new CancellationTokenSource();
            Task done = await Task.WhenAny(tcs.Task, Task.Delay(AUTO_GRAB_TIMEOUT_MS, timeout.Token));
            if (!ReferenceEquals(done, tcs.Task)) return default;
            timeout.Cancel();
            return await tcs.Task;
        }

        #endregion

        #region Motion helpers
        // Absolute X/Y move in the USER frame (Z deliberately untouched — blank zText). MoveToAsync
        // reports nothing back (an out-of-range target just logs "Move cancelled" and completes), so
        // arrival is verified by the caller against a fresh position read.
        private Task MoveToUserAsync(double x, double y)
            => _owner.MoveToAsync(((long)Math.Round(x)).ToString(), ((long)Math.Round(y)).ToString(), "");

        // Pre-flight bounds check against the STORED travel envelope. The drives' own soft limits read
        // a fake ±9999999, so this — plus the radius guard — is the whole of the protection.
        private bool WithinTravel(double x, double y, out string why)
        {
            why = "";
            if (_owner.UserLimits(AxisId.X) is { } bx && (x < bx.min || x > bx.max))
            { why = $"target X {x:F0} outside travel {bx.min:N0}..{bx.max:N0}"; return false; }
            if (_owner.UserLimits(AxisId.Y) is { } by && (y < by.min || y > by.max))
            { why = $"target Y {y:F0} outside travel {by.min:N0}..{by.max:N0}"; return false; }
            return true;
        }

        #endregion

        #region The probe
        /// <summary>
        /// Hops outward from <paramref name="c"/> along <paramref name="dir"/> until the rim is
        /// detected, the guard or travel envelope is reached (Missed), or a move fails / the operator
        /// cancels (Aborted). Always starts by returning to <paramref name="c"/>: the rim leaves the
        /// frame, so the previous leg's edge cannot re-fire, and every point is approached OUTWARD, so
        /// backlash loads the same way at all eight.
        /// </summary>
        private async Task<(ProbeOutcome Outcome, (double X, double Y) Point)> ProbeAsync(
            (double X, double Y) c, (double X, double Y) dir, PixelStepAffine a,
            double guard, double bandLo, double bandHi, double jump, double hop, string label)
        {
            _status.Text = $"{(_autoTarget == AutoTarget.Wafer ? "Wafer Θ scan" : "Auto centre-find")}: probing {label}...";
            if (!WithinTravel(c.X, c.Y, out string cWhy))
            {
                AutoLog($"{label}: cannot return to the centre estimate — {cWhy}.");
                return (ProbeOutcome.Aborted, default);
            }
            await MoveToUserAsync(c.X, c.Y);
            if (_autoCancel) return (ProbeOutcome.Aborted, default);

            double arriveTol = hop / 4.0;
            for (int k = 1; k <= AUTO_MAX_HOPS; k++)
            {
                if (_autoCancel) return (ProbeOutcome.Aborted, default);

                double dist = jump + k * hop;
                if (dist > guard)
                {
                    AutoLog($"{label}: no edge within the {guard:N0} step search radius — direction skipped.");
                    return (ProbeOutcome.Missed, default);
                }
                double tx = c.X + dir.X * dist, ty = c.Y + dir.Y * dist;
                if (!WithinTravel(tx, ty, out string why))
                {
                    AutoLog($"{label}: {why} — direction skipped.");
                    return (ProbeOutcome.Missed, default);
                }

                await MoveToUserAsync(tx, ty);
                if (_autoCancel) return (ProbeOutcome.Aborted, default);

                // Fresh read, NOT TryCurrentUser: the cached position is stale for at least one status
                // period after every move (RunDriveOp pauses that timer), and this M is what every rim
                // point is built from.
                if (!_owner.TryReadUserXyNow(out long mx, out long my))
                {
                    AutoLog($"{label}: motor position unavailable — run aborted.");
                    return (ProbeOutcome.Aborted, default);
                }
                // Arrival check. This is what catches a move MoveToAsync silently rejected (which would
                // otherwise hop in place until the guard and report a clean miss) and a quick-stop.
                if (Math.Abs(mx - tx) > arriveTol || Math.Abs(my - ty) > arriveTol)
                {
                    AutoLog($"{label}: move did not arrive (wanted {tx:F0},{ty:F0}; at {mx},{my}) — run aborted.");
                    return (ProbeOutcome.Aborted, default);
                }

                AutoDetection d = await DetectEdgeAsync();
                if (!d.Found) continue;

                (double X, double Y) e = CentreFinder.ToStepPoint(d.Row, d.Column, d.CrossRow, d.CrossCol, a, mx, my);
                double ex = e.X - c.X, ey = e.Y - c.Y;
                if (ex * dir.X + ey * dir.Y <= 0) continue;              // behind us — not this probe's edge
                double r = Math.Sqrt(ex * ex + ey * ey);
                if (r < bandLo || r > bandHi) continue;                  // stray ridge, not the rim

                // Confirm on a second frame without moving, so a one-frame detector artefact cannot
                // enter the fit. The stage has not moved, so the same M applies.
                AutoDetection d2 = await DetectEdgeAsync();
                if (!d2.Found) continue;
                (double X, double Y) e2 = CentreFinder.ToStepPoint(d2.Row, d2.Column, d2.CrossRow, d2.CrossCol, a, mx, my);
                if (Math.Abs(e2.X - e.X) > arriveTol || Math.Abs(e2.Y - e.Y) > arriveTol) continue;

                AutoLog($"{label}: edge at ({e.X:F0}, {e.Y:F0}), r={r:F0}, after {k} hop(s).");
                return (ProbeOutcome.Found, e);
            }
            AutoLog($"{label}: hop limit reached — direction skipped.");
            return (ProbeOutcome.Missed, default);
        }

        #endregion

        #region Orchestration
        /// <summary>
        /// Starts the automatic chuck centre-find from OUTSIDE this window — the main window's
        /// unified "Home &amp; centre chuck" chain. Deliberately just the Run button's own handler, so
        /// there is ONE run path: the same preconditions, the same confirmation dialog, the same
        /// log pane. A no-op if a run is already live. The window must be visible for the operator
        /// to read that log, which is the caller's job.
        /// </summary>
        public Task RunAutoCentreFromHostAsync() => RunAutoCentreAsync();

        private async Task RunAutoCentreAsync()
        {
            if (_autoRunning) return;

            PixelStepAffine? a = _owner.Calibration.PixelStep;
            if (a == null) { _status.Text = "Auto centre-find: needs the camera-scale calibration first."; return; }
            if (!_view.IsCameraOpen) { _status.Text = "Auto centre-find: the camera is not streaming."; return; }
            if (!_owner.CanMoveCalibration) { _status.Text = "Auto centre-find: needs the drives enabled and idle."; return; }

            long maxR = (long)_autoRadius.Value;
            if (maxR <= 0) { _status.Text = "Auto centre-find: enter the max search radius first."; return; }

            // The run STARTS by homing X and Y, so a missing Home is a hard error, not a warning: with
            // no Home there is no defined starting point and the whole sequence is undefined. Home for
            // X/Y is the centre of the measured travel, so this is also the check that both limits have
            // been found — which is what makes the travel envelope below meaningful.
            if (_owner.HomeTargetFor(AxisId.X) == null || _owner.HomeTargetFor(AxisId.Y) == null)
            {
                _status.Text = "Auto centre-find: X and Y need Home set (run the axis limit-find first) — the run starts by homing them.";
                MessageBox.Show(this,
                    "X and/or Y has no Home.\r\n\r\nHome for X/Y is the centre of the measured travel, so " +
                    "this means their Min/Max limits have not been found. The automatic run starts by " +
                    "sending both axes Home, so it cannot start.\r\n\r\n" +
                    "Run the X+Y limit-find in the axis calibration window first.",
                    "Auto centre-find", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (MessageBox.Show(this,
                    "Automatic chuck centre-find.\r\n\r\nThe stage will:\r\n" +
                    $"  1. send X and Y to Home\r\n" +
                    $"  2. move {Math.Abs(AUTO_SEED_DY):N0} steps along {(AUTO_SEED_DY < 0 ? "−Y" : "+Y")}\r\n" +
                    $"  3. probe outward in 8 directions, returning to the centre estimate between\r\n" +
                    $"     each, and abort any direction past {maxR:N0} steps\r\n\r\n" +
                    "Confirm before running:\r\n" +
                    "  - Z / focus is set so the chuck edge is sharp\r\n" +
                    "  - the path from here to Home is clear\r\n\r\n" +
                    "Z is never moved.\r\n\r\nProceed?",
                    "Auto centre-find", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            _autoCancel = false;
            _autoRunning = true;
            RefreshAutoUi();
            // Lock the MAIN window's manual controls too: _autoRunning only gates this window's
            // buttons, and between hops FrmMain's own busy flag is clear, so the d-pad and the
            // polled analog joystick would otherwise stay live and could move the stage between a
            // move and the capture paired with it. Taken BEFORE the homing moves, so the seed
            // sequence is covered by it as well as the probes.
            using IDisposable hostLock = _owner.BeginExternalOp("Auto chuck centre-find");
            try
            {
                _autoLog.Clear();
                if (await SeedFromHomeAsync())
                    await AutoCentreCoreAsync(a, maxR);
            }
            finally
            {
                _autoRunning = false;
                _autoCancel = false;
                RefreshAutoUi();
            }
        }

        /// <summary>
        /// Puts the stage on its starting point: X and Y Home, then <see cref="AUTO_SEED_DY"/> along Y.
        /// This is what makes the run fully automatic — Home is repeatable, so the rough centre no
        /// longer comes from the operator's eye. False (with the reason logged) if anything did not
        /// arrive, because every rim point is built against the position read here.
        /// </summary>
        private async Task<bool> SeedFromHomeAsync()
        {
            _status.Text = "Auto centre-find: homing X and Y...";
            AutoLog("Homing X and Y...");
            await _owner.GoHomeAsync(AxisId.X);
            if (_autoCancel) { AutoLog("Cancelled during homing."); return false; }
            await _owner.GoHomeAsync(AxisId.Y);
            if (_autoCancel) { AutoLog("Cancelled during homing."); return false; }

            // GoHomeAsync reports only to the main log, so verify here against a FRESH read — the
            // cached position is stale for at least a status period after every move.
            if (!_owner.TryReadUserXyNow(out long hx, out long hy))
            {
                AutoLog("Motor position unavailable after homing — run aborted.");
                _status.Text = "Auto centre-find: motor position unavailable after homing.";
                return false;
            }
            AutoLog($"Home reached: ({hx}, {hy}).");

            double sx = hx, sy = hy + AUTO_SEED_DY;
            if (!WithinTravel(sx, sy, out string why))
            {
                AutoLog($"Seed offset rejected — {why}.");
                _status.Text = $"Auto centre-find: the {AUTO_SEED_DY:+0;-0} step seed offset leaves the travel envelope.";
                return false;
            }
            AutoLog($"Seed offset {AUTO_SEED_DY:+0;-0} on Y → ({sx:F0}, {sy:F0})...");
            await MoveToUserAsync(sx, sy);
            if (_autoCancel) { AutoLog("Cancelled during the seed offset."); return false; }

            if (!_owner.TryReadUserXyNow(out long mx, out long my))
            {
                AutoLog("Motor position unavailable after the seed offset — run aborted.");
                return false;
            }
            // Same arrival check the probes use, for the same reason: MoveToAsync reports a rejected
            // move only to the main log, and starting the scan from the wrong place biases every point.
            if (Math.Abs(mx - sx) > AUTO_SEED_TOL || Math.Abs(my - sy) > AUTO_SEED_TOL)
            {
                AutoLog($"Seed move did not arrive (wanted {sx:F0},{sy:F0}; at {mx},{my}) — run aborted.");
                _status.Text = "Auto centre-find: the seed move did not arrive — see the log.";
                return false;
            }
            return true;
        }

        private async Task AutoCentreCoreAsync(PixelStepAffine a, long maxR)
        {
            // A capture at the start does double duty: it sizes the hop from the LIVE frame, and it
            // rejects the worst starting condition — the rim already in view, which would make the
            // first probe's own edge indistinguishable from the one it is looking for.
            AutoDetection start = await DetectEdgeAsync();
            if (start.FrameW <= 0) { _status.Text = "Auto centre-find: no frame from the camera."; return; }
            if (start.Found)
            {
                _status.Text = "Auto centre-find: the rim is already in view — jog nearer the centre and retry.";
                return;
            }
            double hop = HopSteps(a, start.FrameW, start.FrameH);
            if (hop < 1.0) { _status.Text = "Auto centre-find: the calibration affine gives a degenerate hop size."; return; }

            if (!_owner.TryReadUserXyNow(out long c0x, out long c0y))
            { _status.Text = "Auto centre-find: motor position unavailable — connect & enable."; return; }
            (double X, double Y) c0 = (c0x, c0y);

            _chuckFinder.Clear();
            RefreshEdgeUi();
            AutoLog($"Start ({c0x}, {c0y})  max R={maxR:N0}  hop={hop:F0} steps.");

            // The max search radius is the guard AND the top of the acceptance band, for every stage:
            // a probe never travels past it, and a detection past it cannot occur. Only the lower
            // bounds stay fractional (see AUTO_BAND_LO_FRAC).
            double wideLo = AUTO_BAND_LO_FRAC * maxR, wideHi = maxR;

            // Stage A: vertical bisection (no jump — see AUTO_APPROACH_R)
            (ProbeOutcome oN, (double X, double Y) eN) = await ProbeAsync(c0, (0, 1), a, maxR, wideLo, wideHi, 0, hop, "N");
            if (oN == ProbeOutcome.Aborted) { Abandon(); return; }
            (ProbeOutcome oS, (double X, double Y) eS) = await ProbeAsync(c0, (0, -1), a, maxR, wideLo, wideHi, 0, hop, "S");
            if (oS == ProbeOutcome.Aborted) { Abandon(); return; }
            if (oN != ProbeOutcome.Found || oS != ProbeOutcome.Found)
            {
                _status.Text = "Auto centre-find: the ±Y probes did not both find the rim — check focus/Z and the nominal radius.";
                return;
            }
            double cy1 = (eN.Y + eS.Y) / 2.0;

            // Stage B: horizontal bisection, from the corrected Y
            (double X, double Y) ch = (c0.X, cy1);
            (ProbeOutcome oE, (double X, double Y) eE) = await ProbeAsync(ch, (1, 0), a, maxR, wideLo, wideHi, 0, hop, "E");
            if (oE == ProbeOutcome.Aborted) { Abandon(); return; }
            (ProbeOutcome oW, (double X, double Y) eW) = await ProbeAsync(ch, (-1, 0), a, maxR, wideLo, wideHi, 0, hop, "W");
            if (oW == ProbeOutcome.Aborted) { Abandon(); return; }
            if (oE != ProbeOutcome.Found || oW != ProbeOutcome.Found)
            {
                _status.Text = "Auto centre-find: the ±X probes did not both find the rim — check focus/Z and the nominal radius.";
                return;
            }
            double cx1 = (eE.X + eW.X) / 2.0;
            (double X, double Y) c1 = (cx1, cy1);

            // The radius is the MEAN DISTANCE of the four cardinal points from C₁ — not half the N–S
            // span. Each E lies on the rim by construction, so |E − C₁| is the radius once C₁ is right,
            // whereas the N–S span shortens to 2·√(R²−δ²) when the start was offset laterally by δ.
            List<(double X, double Y)> points = [eN, eS, eE, eW];
            double r1 = 0;
            foreach ((double X, double Y) p in points) r1 += Dist(p, c1);
            r1 /= points.Count;
            AutoLog($"Bisection: centre ~({cx1:F0}, {cy1:F0}), R ~{r1:F0} steps.");
            if (r1 < 1.0) { _status.Text = "Auto centre-find: the bisection produced a degenerate radius."; return; }

            // Stages A/B RE-CENTRE; they are not the estimator. TryDetect returns the rim point
            // nearest the CROSSHAIR, which lies along the ray C→M rather than on the scan line, so with
            // a laterally offset start the midpoint only approximates a true chord bisection. It is
            // good enough to aim the diagonals; the answer comes from the Pratt fit over all eight.

            // Stage C: four diagonals from C₁, now with the approach jump
            double tightLo = AUTO_TIGHT_LO_FRAC * r1, tightHi = maxR, jump = AUTO_APPROACH_R * r1;
            double k = Math.Sqrt(0.5);
            (double X, double Y, string Label)[] diagonals =
            [
                (k, k, "NE"), (-k, k, "NW"), (-k, -k, "SW"), (k, -k, "SE"),
            ];
            foreach ((double X, double Y, string Label) d in diagonals)
            {
                (ProbeOutcome o, (double X, double Y) e) =
                    await ProbeAsync(c1, (d.X, d.Y), a, maxR, tightLo, tightHi, jump, hop, d.Label);
                if (o == ProbeOutcome.Aborted) { Abandon(); return; }
                if (o == ProbeOutcome.Found) points.Add(e);
            }

            // Stage D: fit
            foreach ((double X, double Y) p in points) _chuckFinder.AddPoint(p.X, p.Y);
            RefreshEdgeUi();
            if (_chuckFinder.Count < 3)
            {
                _status.Text = $"Auto centre-find: only {_chuckFinder.Count} point(s) accepted — 3 are needed to fit.";
                return;
            }
            ComputeCentre();
            if (_chuckCentre == null) return;   // ComputeCentre already reported the failure
            // The fitted radius is NOT fed back into the max-search-radius box: that box is an
            // operational bound on how far a probe may travel, not a measurement of the feature.
            // ComputeCentre still persists the measured radius as ChuckRadius.

            // Stage E: per-point residuals. The fit's own RMS hides a single bad point among
            // eight; these say which direction it came from.
            (double X, double Y) fit = (_chuckCentre.Value.X, _chuckCentre.Value.Y);
            double rFit = 0;
            foreach ((double X, double Y) p in points) rFit += Dist(p, fit);
            rFit /= points.Count;
            double worst = 0;
            foreach ((double X, double Y) p in points) worst = Math.Max(worst, Math.Abs(Dist(p, fit) - rFit));
            AutoLog($"Fit over {points.Count} points: centre ({fit.X:F0}, {fit.Y:F0}), worst radial residual {worst:F0} steps.");

            // Stage F: drive to the centre just found, so the run ends with the chuck centred
            // instead of parked out on the last diagonal. Bounds-checked and arrival-verified like
            // every other move here — MoveToAsync reports a rejected target only to the main log, and
            // a silent no-op would leave the stage at the rim while the status read "complete".
            // The fit is already saved by ComputeCentre, so a failure here costs the position, not
            // the measurement: it is reported and the run still ends with the centre stored.
            if (_autoCancel)
            {
                _status.Text = $"Auto centre-find: centre found and saved ({points.Count} points); cancelled before returning to it.";
                return;
            }
            if (!WithinTravel(fit.X, fit.Y, out string fitWhy))
            {
                AutoLog($"Cannot return to the centre — {fitWhy}. It is saved; the stage is left where it is.");
                _status.Text = "Auto centre-find: centre found and saved, but it lies outside the travel envelope.";
                return;
            }
            AutoLog($"Returning to the fitted centre ({fit.X:F0}, {fit.Y:F0})...");
            _status.Text = "Auto centre-find: returning to the centre...";
            await MoveToUserAsync(fit.X, fit.Y);
            if (_owner.TryReadUserXyNow(out long fx, out long fy) &&
                (Math.Abs(fx - fit.X) > AUTO_SEED_TOL || Math.Abs(fy - fit.Y) > AUTO_SEED_TOL))
            {
                AutoLog($"Return move did not arrive (wanted {fit.X:F0},{fit.Y:F0}; at {fx},{fy}).");
                _status.Text = "Auto centre-find: centre found and saved, but the return move did not arrive — see the log.";
                return;
            }
            AutoLog($"At the chuck centre ({fit.X:F0}, {fit.Y:F0}).");
            _status.Text = $"Auto centre-find complete: {points.Count} points, worst residual {worst:F0} steps, stage at the centre.";
        }

        #endregion

        #region Small helpers
        // The frame's two extents in step space, through the affine; the hop is a fraction of the
        // smaller. Computed per run, never cached: ZoomFactor is a centred-ROI crop applied by the grab
        // thread, so the field of view in steps changes with whatever zoom the operator is on.
        private static double HopSteps(PixelStepAffine a, double frameW, double frameH)
        {
            double rowX = a.Xr * frameH, rowY = a.Yr * frameH;
            double colX = a.Xc * frameW, colY = a.Yc * frameW;
            double extRow = Math.Sqrt(rowX * rowX + rowY * rowY);
            double extCol = Math.Sqrt(colX * colX + colY * colY);
            return AUTO_HOP_FRAC * Math.Min(extRow, extCol);
        }

        private static double Dist((double X, double Y) p, (double X, double Y) q)
        {
            double dx = p.X - q.X, dy = p.Y - q.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        // A run that aborted mid-way leaves the stage somewhere on a probe and the point set partial;
        // keeping those points would let a later Compute Centre fit a half-collected rim.
        private void Abandon()
        {
            _chuckFinder.Clear();
            RefreshEdgeUi();
            AutoLog("ABANDONED — points discarded. See the main-window log.");
            _status.Text = "Auto centre-find ABANDONED — points discarded.";
        }

        // Routed by target so a wafer run's probe transcript lands in the wafer pane, not the chuck's:
        // ProbeAsync and DetectEdgeAsync are shared, so they cannot pick the pane themselves.
        private void AutoLog(string line)
        {
            _status.Text = line;
            (_autoTarget == AutoTarget.Wafer ? _waferLog : _autoLog).AppendText(line + "\r\n");
        }

        private void CancelAutoCentre()
        {
            if (!_autoRunning) return;
            _autoCancel = true;
            _owner.RequestStop();   // halts an in-flight move; the probe loop sees the flag next hop
            _status.Text = "Auto centre-find: cancelling...";
        }

        // Run is available only with a live camera and no run in progress; Cancel only during one. The
        // manual chuck buttons lock out while a run is live (RefreshEdgeUi also honours _autoRunning)
        // so a stray click cannot inject a point into the set being collected.
        private void RefreshAutoUi()
        {
            _autoRunBtn.Enabled = _view.IsCameraOpen && !_autoRunning && !_waferRunning && !_notchRunning;
            _autoRadius.Enabled = !_autoRunning && !_waferRunning && !_notchRunning;
            _autoCancelBtn.Enabled = _autoRunning;
            RefreshEdgeUi();
        }

        #endregion
    }
}
