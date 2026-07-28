using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using HalconDotNet;

namespace NanotecController
{
    // FrmVisionProtocols — AUTOMATIC chuck centre-find: the operator roughly centres the chuck once,
    // and the stage collects the eight rim points itself. Nothing here replaces the maths — the points
    // go into the same _chuckFinder and the same Pratt fit (ComputeCentre) as the manual flow; this is
    // purely the orchestration that used to be the operator's hand on the jog.
    //
    // STEP-AND-SETTLE. Every probe advances in discrete hops and captures with the stage stopped, so
    // the motor position paired with each frame is exact and the travel guard is inherent: a target
    // past the guard is never even COMMANDED. A continuous jog gives neither — the frame would be
    // exposed in motion, and there is no position sample corresponding to the exposure instant
    // (see FrmMain.TryReadUserXyNow for why the cached one will not do).
    //
    // Shape of a run:
    //   A  probe ±Y from the operator's rough centre   → bisect for cy
    //   B  probe ±X from (roughX, cy)                  → bisect for cx, giving C₁ and a measured radius
    //   C  probe the four diagonals from C₁            → four more rim points
    //   D  Pratt-fit all eight; persist centre + radius
    // (Partial of FrmVisionProtocols; layout lives in FrmVisionProtocols.cs.)
    public sealed partial class FrmVisionProtocols
    {
        // Abort a probe once it would travel past this multiple of the nominal radius. Sized generously
        // because the operator's starting point may be well off-centre — this is the crash guard, and
        // on X it is effectively the ONLY one (the +end limit switch is dead, and 0x607D is fake).
        private const double AUTO_GUARD_R = 1.8;
        // Stage C only: jump straight to this multiple of the MEASURED radius before hopping. Pure time
        // optimisation — it skips the empty chuck interior. Safe only because C₁ came from the
        // bisection rather than the operator's eye: from a rough centre off by δ, a jump aligned with δ
        // lands at 0.8·R + δ, which can be past the rim — and the rim is then skipped UNSEEN.
        private const double AUTO_APPROACH_R = 0.8;
        // One hop, as a fraction of the frame's smaller extent in step space. Must stay well under a
        // full frame: a bigger hop can carry the rim past the camera between captures, and
        // ChuckEdgeDetector wants a ≥MinLineLength (500 px) ridge, so a rim merely clipping a corner
        // does not count as seen.
        private const double AUTO_HOP_FRAC = 0.4;
        // Backstop on one probe's hop count, in case the guard arithmetic is ever defeated by a
        // degenerate hop size.
        private const int AUTO_MAX_HOPS = 500;
        // Give up waiting on a detection job. The grab thread can drop one silently (camera closed
        // mid-run, or PostFrameBitmap failing to convert), which would otherwise hang the run forever.
        private const int AUTO_GRAB_TIMEOUT_MS = 8000;

        private volatile bool _autoCancel;
        private bool _autoRunning;

        private enum ProbeOutcome { Found, Missed, Aborted }

        // One detection job's result, carried back off the grab thread. FrameW/FrameH are the LIVE
        // frame size (ZoomFactor is a centred-ROI crop, so this shrinks with zoom) and size the hop.
        private readonly record struct AutoDetection(
            bool Found, double Row, double Column, double CrossRow, double CrossCol,
            double FrameW, double FrameH);

        // --- Awaitable grab + detect ------------------------------------------------

        // The same detector call as RequestEdge, but awaitable, with the result handed back instead of
        // stored. The overlay still lands on the captured pane so the operator can watch each hop.
        // Returns a default (FrameW = 0) if the camera is closed or the job never comes back.
        private async Task<AutoDetection> DetectEdgeAsync()
        {
            if (!_view.IsCameraOpen) return default;

            var tcs = new TaskCompletionSource<AutoDetection>(TaskCreationOptions.RunContinuationsAsynchronously);
            _view.RequestFrame(frame =>
            {
                HOperatorSet.GetImageSize(frame, out HTuple fw, out HTuple fh);
                double crossRow = fh.D / 2.0, crossCol = fw.D / 2.0;
                bool found;
                ChuckEdgeDetector.EdgePoint edge;
                try { found = _edgeDetector.TryDetect(frame, crossRow, crossCol, out edge); }
                catch (HOperatorException) { found = false; edge = default; }

                var result = new AutoDetection(found, edge.Row, edge.Column, crossRow, crossCol, fw.D, fh.D);
                _view.PostFrameBitmap(frame, flip: false, raw =>
                {
                    if (IsDisposed) { raw.Dispose(); tcs.TrySetResult(result); return; }
                    if (found)
                    {
                        DrawEdgeOverlay(raw, new ChuckEdgeDetector.EdgePoint(result.Row, result.Column), crossRow, crossCol);
                    }
                    else
                    {
                        using var g = Graphics.FromImage(raw);
                        VisionOverlay.DrawCrosshair(g, raw.Width, crossRow, crossCol, Color.Lime);
                    }
                    ShowCaptured(raw);
                    tcs.TrySetResult(result);
                });
            });

            Task done = await Task.WhenAny(tcs.Task, Task.Delay(AUTO_GRAB_TIMEOUT_MS));
            return ReferenceEquals(done, tcs.Task) ? tcs.Task.Result : default;
        }

        // --- Motion helpers ---------------------------------------------------------

        // Absolute X/Y move in the USER frame (Z deliberately untouched — blank zText). MoveToAsync
        // reports nothing back (an out-of-range target just logs "Move cancelled" and completes), so
        // arrival is verified by the caller against a fresh position read.
        private Task MoveToUserAsync(double x, double y)
            => _owner!.MoveToAsync(((long)Math.Round(x)).ToString(), ((long)Math.Round(y)).ToString(), "");

        // Pre-flight bounds check against the STORED travel envelope. The drives' own soft limits read
        // a fake ±9999999, so this — plus the radius guard — is the whole of the protection.
        private bool WithinTravel(double x, double y, out string why)
        {
            why = "";
            if (_owner!.UserLimits(AxisId.X) is { } bx && (x < bx.min || x > bx.max))
            { why = $"target X {x:F0} outside travel {bx.min:N0}..{bx.max:N0}"; return false; }
            if (_owner.UserLimits(AxisId.Y) is { } by && (y < by.min || y > by.max))
            { why = $"target Y {y:F0} outside travel {by.min:N0}..{by.max:N0}"; return false; }
            return true;
        }

        // --- The probe --------------------------------------------------------------

        /// <summary>
        /// Hops outward from <paramref name="c"/> along <paramref name="dir"/> until the rim is
        /// detected, the guard or travel envelope is reached (Missed), or a move fails / the operator
        /// cancels (Aborted). Always starts by returning to <paramref name="c"/>: the rim leaves the
        /// frame, so the previous leg's edge cannot re-fire, and every point is approached OUTWARD, so
        /// backlash loads the same way at all eight.
        /// </summary>
        private async Task<(ProbeOutcome Outcome, (double X, double Y) Point)> ProbeAsync(
            (double X, double Y) c, (double X, double Y) dir, PixelStepAffine a,
            double rNom, double bandLo, double bandHi, double jump, double hop, string label)
        {
            _status.Text = $"Auto centre-find: probing {label}...";
            if (!WithinTravel(c.X, c.Y, out string cWhy))
            {
                AutoLog($"{label}: cannot return to the centre estimate — {cWhy}.");
                return (ProbeOutcome.Aborted, default);
            }
            await MoveToUserAsync(c.X, c.Y);
            if (_autoCancel) return (ProbeOutcome.Aborted, default);

            double guard = AUTO_GUARD_R * rNom;
            double arriveTol = hop / 4.0;
            for (int k = 1; k <= AUTO_MAX_HOPS; k++)
            {
                if (_autoCancel) return (ProbeOutcome.Aborted, default);

                double dist = jump + k * hop;
                if (dist > guard)
                {
                    AutoLog($"{label}: no edge within the {AUTO_GUARD_R:0.0}xR guard ({guard:N0} steps) — direction skipped.");
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
                if (!_owner!.TryReadUserXyNow(out long mx, out long my))
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

        // --- Orchestration ----------------------------------------------------------

        private async Task RunAutoCentreAsync()
        {
            if (_owner == null || _autoRunning) return;

            PixelStepAffine? a = _owner.Calibration.PixelStep;
            if (a == null) { _status.Text = "Auto centre-find: needs the camera-scale calibration first."; return; }
            if (!_view.IsCameraOpen) { _status.Text = "Auto centre-find: the camera is not streaming."; return; }
            if (!_owner.CanMoveCalibration) { _status.Text = "Auto centre-find: needs the drives enabled and idle."; return; }

            long rNom = (long)_autoRadius.Value;
            if (rNom <= 0) { _status.Text = "Auto centre-find: enter the nominal chuck radius first."; return; }

            if (_owner.UserLimits(AxisId.X) == null || _owner.UserLimits(AxisId.Y) == null)
            {
                if (MessageBox.Show(this,
                        "X and/or Y has no Min/Max travel set, so the only backstop on a probe is the " +
                        $"{AUTO_GUARD_R:0.0}x radius guard ({AUTO_GUARD_R * rNom:N0} steps).\r\n\r\nRun anyway?",
                        "Auto centre-find", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                    return;
            }
            if (MessageBox.Show(this,
                    "Automatic chuck centre-find.\r\n\r\nConfirm before running:\r\n" +
                    "  - the chuck is roughly centred in the view\r\n" +
                    "  - Z / focus is set so the chuck edge is sharp\r\n" +
                    "  - the rim is NOT currently in view\r\n\r\n" +
                    "The stage probes outward in 8 directions, returning to the centre between each, " +
                    $"and aborts any direction past {AUTO_GUARD_R * rNom:N0} steps. Z is never moved.\r\n\r\nProceed?",
                    "Auto centre-find", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            _autoCancel = false;
            _autoRunning = true;
            RefreshAutoUi();
            try { await AutoCentreCoreAsync(a, rNom); }
            finally
            {
                _autoRunning = false;
                _autoCancel = false;
                RefreshAutoUi();
            }
        }

        private async Task AutoCentreCoreAsync(PixelStepAffine a, long rNom)
        {
            _autoLog.Clear();

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

            if (!_owner!.TryReadUserXyNow(out long c0x, out long c0y))
            { _status.Text = "Auto centre-find: motor position unavailable — connect & enable."; return; }
            (double X, double Y) c0 = (c0x, c0y);

            _chuckFinder.Clear();
            RefreshEdgeUi();
            AutoLog($"Start ({c0x}, {c0y})  R_nom={rNom:N0}  hop={hop:F0} steps.");

            // Wide band for stages A/B: c0 is the operator's eye, so the rim may sit anywhere from very
            // near to nearly the guard. It tightens in stage C once the radius has been measured.
            double wideLo = 0.2 * rNom, wideHi = AUTO_GUARD_R * rNom;

            // ---- Stage A: vertical bisection (no jump — see AUTO_APPROACH_R) ----
            (ProbeOutcome oN, (double X, double Y) eN) = await ProbeAsync(c0, (0, 1), a, rNom, wideLo, wideHi, 0, hop, "N");
            if (oN == ProbeOutcome.Aborted) { Abandon(); return; }
            (ProbeOutcome oS, (double X, double Y) eS) = await ProbeAsync(c0, (0, -1), a, rNom, wideLo, wideHi, 0, hop, "S");
            if (oS == ProbeOutcome.Aborted) { Abandon(); return; }
            if (oN != ProbeOutcome.Found || oS != ProbeOutcome.Found)
            {
                _status.Text = "Auto centre-find: the ±Y probes did not both find the rim — check focus/Z and the nominal radius.";
                return;
            }
            double cy1 = (eN.Y + eS.Y) / 2.0;

            // ---- Stage B: horizontal bisection, from the corrected Y ----
            (double X, double Y) ch = (c0.X, cy1);
            (ProbeOutcome oE, (double X, double Y) eE) = await ProbeAsync(ch, (1, 0), a, rNom, wideLo, wideHi, 0, hop, "E");
            if (oE == ProbeOutcome.Aborted) { Abandon(); return; }
            (ProbeOutcome oW, (double X, double Y) eW) = await ProbeAsync(ch, (-1, 0), a, rNom, wideLo, wideHi, 0, hop, "W");
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
            var points = new List<(double X, double Y)> { eN, eS, eE, eW };
            double r1 = 0;
            foreach ((double X, double Y) p in points) r1 += Dist(p, c1);
            r1 /= points.Count;
            AutoLog($"Bisection: centre ~({cx1:F0}, {cy1:F0}), R ~{r1:F0} steps.");
            if (r1 < 1.0) { _status.Text = "Auto centre-find: the bisection produced a degenerate radius."; return; }

            // Stages A/B are a RE-CENTRING stage, not the estimator: TryDetect returns the rim point
            // nearest the CROSSHAIR, which lies along the ray C→M rather than on the scan line, so with
            // a laterally offset start the midpoint only approximates a true chord bisection. It is
            // good enough to aim the diagonals; the answer comes from the Pratt fit over all eight.

            // ---- Stage C: four diagonals from C₁, now with the approach jump ----
            double tightLo = 0.7 * r1, tightHi = 1.3 * r1, jump = AUTO_APPROACH_R * r1;
            double k = Math.Sqrt(0.5);
            var diagonals = new (double X, double Y, string Label)[]
            {
                (k, k, "NE"), (-k, k, "NW"), (-k, -k, "SW"), (k, -k, "SE"),
            };
            foreach ((double X, double Y, string Label) d in diagonals)
            {
                (ProbeOutcome o, (double X, double Y) e) =
                    await ProbeAsync(c1, (d.X, d.Y), a, rNom, tightLo, tightHi, jump, hop, d.Label);
                if (o == ProbeOutcome.Aborted) { Abandon(); return; }
                if (o == ProbeOutcome.Found) points.Add(e);
            }

            // ---- Stage D: fit ----
            foreach ((double X, double Y) p in points) _chuckFinder.AddPoint(p.X, p.Y);
            RefreshEdgeUi();
            if (_chuckFinder.Count < 3)
            {
                _status.Text = $"Auto centre-find: only {_chuckFinder.Count} point(s) accepted — 3 are needed to fit.";
                return;
            }
            ComputeCentre();
            if (_chuckCentre == null) return;   // ComputeCentre already reported the failure
            if (_owner.Calibration.ChuckRadius is long saved && saved > 0 && saved <= _autoRadius.Maximum)
                _autoRadius.Value = saved;      // next run's nominal radius comes from this fit

            // ---- Stage E: per-point residuals. The fit's own RMS hides a single bad point among
            // eight; these say which direction it came from.
            (double X, double Y) fit = (_chuckCentre.Value.X, _chuckCentre.Value.Y);
            double rFit = 0;
            foreach ((double X, double Y) p in points) rFit += Dist(p, fit);
            rFit /= points.Count;
            double worst = 0;
            foreach ((double X, double Y) p in points) worst = Math.Max(worst, Math.Abs(Dist(p, fit) - rFit));
            AutoLog($"Fit over {points.Count} points: centre ({fit.X:F0}, {fit.Y:F0}), worst radial residual {worst:F0} steps.");
            _status.Text = $"Auto centre-find complete: {points.Count} points, worst residual {worst:F0} steps.";
        }

        // --- Small helpers ----------------------------------------------------------

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

        private void AutoLog(string line)
        {
            _status.Text = line;
            _autoLog.AppendText(line + "\r\n");
        }

        private void CancelAutoCentre()
        {
            if (!_autoRunning) return;
            _autoCancel = true;
            _owner?.RequestStop();   // halts an in-flight move; the probe loop sees the flag next hop
            _status.Text = "Auto centre-find: cancelling...";
        }

        // Run is available only with a live camera and no run in progress; Cancel only during one. The
        // manual chuck buttons lock out while a run is live (RefreshEdgeUi also honours _autoRunning)
        // so a stray click cannot inject a point into the set being collected.
        private void RefreshAutoUi()
        {
            _autoRunBtn.Enabled = _view.IsCameraOpen && !_autoRunning;
            _autoRadius.Enabled = !_autoRunning;
            _autoCancelBtn.Enabled = _autoRunning;
            RefreshEdgeUi();
        }
    }
}
