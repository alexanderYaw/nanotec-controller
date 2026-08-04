using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using HalconDotNet;

namespace NanotecController
{
    // FrmVisionProtocols — chuck + wafer centre-find: capture ≥3 rim points (step space), circle-fit them to
    // a centre, and drive there. The two features share CentreFinder plus the compute/go-to halves
    // (TryComputeAndSaveCentre / GoToCentreAsync); only the grab callbacks differ, because the chuck
    // edge (focus) and wafer edge (brightness) use different detectors and overlays.
    // (Partial of FrmVisionProtocols; layout lives in FrmVisionProtocols.cs.)
    public sealed partial class FrmVisionProtocols
    {
        private void RequestEdge()
        {
            if (!_view.IsCameraOpen) return;
            _view.RequestFrame(frame =>
            {
                HOperatorSet.GetImageSize(frame, out HTuple fw, out HTuple fh);
                double crossRow = fh.D / 2.0, crossCol = fw.D / 2.0;
                bool found;
                ChuckEdgeDetector.EdgePoint edge;
                try { found = _edgeDetector.TryDetect(frame, crossRow, crossCol, out edge); }
                catch (HOperatorException) { found = false; edge = default; }
                _view.PostFrameBitmap(frame, flip: false, raw => OnEdgeGrabbed(found, edge, crossRow, crossCol, raw));
            });
            _status.Text = "Detecting chuck edge...";
        }

        // UI thread: the grab thread found (or not) the chuck-edge point nearest the crosshair. Convert
        // it to step space via the calibration and store it: E = M + A·(p_crosshair − p_edge),
        // the motor position that would bring this edge point onto the crosshair (user frame).
        private void OnEdgeGrabbed(bool found, ChuckEdgeDetector.EdgePoint edge, double crossRow, double crossCol, Bitmap raw)
        {
            if (IsDisposed) { raw.Dispose(); return; }

            if (!found)
            {
                ShowCaptured(raw);
                _status.Text = "Edge: chuck edge NOT found — reframe so the edge crosses the view (tune in HDevelop).";
                return;
            }
            PixelStepAffine? a = _owner.Calibration.PixelStep;
            if (a == null)
            {
                ShowCaptured(raw);
                _status.Text = "Edge: needs the camera-scale calibration first.";
                return;
            }
            if (!_owner.TryCurrentUser(AxisId.X, out long mx) || !_owner.TryCurrentUser(AxisId.Y, out long my))
            {
                ShowCaptured(raw);
                _status.Text = "Edge: motor position unavailable — connect & enable.";
                return;
            }

            var (ex, ey) = _chuckFinder.Add(edge.Row, edge.Column, crossRow, crossCol, a, mx, my);

            raw = DrawEdgeOverlay(raw, edge.Row, edge.Column, crossRow, crossCol);   // may return a widened copy
            ShowCaptured(raw);
            RefreshEdgeUi();
            _status.Text = $"Edge {_chuckFinder.Count}: px=(r {edge.Row:F0}, c {edge.Column:F0}) → step=({ex:F0}, {ey:F0})";
        }

        // Draws the frame-centre crosshair (green) and the detected edge point (yellow circle). Takes
        // the point as plain row/col so the chuck and wafer detectors — whose EdgePoint types are
        // distinct — can share it. RETURNS the bitmap drawn on: a mono frame is indexed and gets
        // replaced by a drawable copy, so the caller must keep the returned reference (the original
        // is disposed).
        private static Bitmap DrawEdgeOverlay(Bitmap bmp, double edgeRow, double edgeCol, double crossRow, double crossCol)
        {
            bmp = VisionOverlay.EnsureDrawable(bmp);
            using var g = Graphics.FromImage(bmp);
            float w = VisionOverlay.PenWidth(bmp.Width), rad = bmp.Width / 60f;
            VisionOverlay.DrawCrosshair(g, bmp.Width, crossRow, crossCol, Color.Lime);
            VisionOverlay.DrawPoint(g, edgeRow, edgeCol, rad, Color.Yellow, w);
            return bmp;
        }

        // Compute+persist for the chuck: circle-fits the finder's points, stores the centre via the
        // setter, and persists. Returns false (with display text) on a fit/save failure; on success,
        // text is the result readout and centre is set.
        private bool TryComputeAndSaveCentre(CentreFinder finder, string label,
                                             Action<long, long, CircleFit.Result> store,
                                             out (long X, long Y)? centre, out string text)
        {
            centre = null;
            if (!finder.TryComputeCentre(out long cx, out long cy, out CircleFit.Result fit, out string? err))
            {
                text = "Fit failed:\r\n" + err;
                return false;
            }
            store(cx, cy, fit);
            try { _owner.Calibration.Save(); }
            catch (Exception ex) { text = $"Computed but SAVE failed:\r\n{ex.Message}"; return false; }
            centre = (cx, cy);
            text = $"{label} (N={finder.Count}): X={cx} Y={cy}\r\nR={fit.Radius:F0}  RMS={fit.RmsError:F0} steps";
            return true;
        }

        // Shared "Go to centre": confirm, then issue the absolute X/Y move (Z unchanged) via FrmMain.
        private async Task GoToCentreAsync(string label, (long X, long Y)? centre)
        {
            if (centre == null) return;
            long cx = centre.Value.X, cy = centre.Value.Y;
            if (MessageBox.Show(this,
                    $"Move the {label} centre to the view centre?\r\nTarget: X={cx}, Y={cy}  (Z unchanged).",
                    $"Go to {label} centre", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            _status.Text = $"Moving to {label} centre...";
            await _owner.MoveToAsync(cx.ToString(), cy.ToString(), "");
            _status.Text = $"Go to {label} centre: move issued (see main-window log).";
        }

        // "Go to wafer centre". Unlike the chuck's, this target is COMPUTED rather than read: the wafer
        // sits eccentric on the chuck, so its centre orbits the rotation axis as Θ turns and the stored
        // snapshot is only valid at the angle the scan ended on. The invariant offset is rotated out to
        // whatever Θ the chuck is standing at now.
        private async Task GoToWaferCentreAsync()
        {
            if (!_owner.TryCurrentUser(AxisId.Theta, out long theta))
            {
                _status.Text = "Go to wafer centre: Θ position unavailable — connect & enable.";
                return;
            }
            double deg = CrosshairRotation.ChuckTicksToDegrees(theta, CrosshairRotation.ChuckTicksPerRev);
            if (_owner.Calibration.WaferCentreAt(deg) is not { } target)
            {
                _status.Text = "Go to wafer centre: run the Θ scan first (it also needs a chuck centre and X/Y steps-per-mm).";
                return;
            }
            await GoToCentreAsync($"wafer (Θ={deg:F1}°)", target);
        }

        // Adds a rim point WITHOUT running the detector: the operator has jogged the chuck edge onto
        // the crosshair by eye, so the current motor position IS the point (p_edge = p_cross ⇒ E = M).
        // Reads the motor position on the UI thread (stage is stationary while aligning), then grabs a
        // frame purely to show the crosshair on the captured pane as a record.
        private void AddEdgeAtCrosshair()
        {
            if (!_owner.TryCurrentUser(AxisId.X, out long mx) || !_owner.TryCurrentUser(AxisId.Y, out long my))
            {
                _status.Text = "Edge: motor position unavailable — connect & enable.";
                return;
            }

            var (ex, ey) = _chuckFinder.AddPoint(mx, my);
            RefreshEdgeUi();
            _status.Text = $"Edge {_chuckFinder.Count} (crosshair): step=({ex:F0}, {ey:F0})";

            if (!_view.IsCameraOpen) return;
            _view.RequestFrame(frame =>
            {
                HOperatorSet.GetImageSize(frame, out HTuple fw, out HTuple fh);
                double crossRow = fh.D / 2.0, crossCol = fw.D / 2.0;
                _view.PostFrameBitmap(frame, flip: false, raw =>
                {
                    if (IsDisposed) { raw.Dispose(); return; }
                    raw = VisionOverlay.EnsureDrawable(raw);
                    using (var g = Graphics.FromImage(raw))
                        VisionOverlay.DrawCrosshair(g, raw.Width, crossRow, crossCol, Color.Lime);
                    ShowCaptured(raw);
                });
            });
        }

        // Removes the point currently selected in the edge list (before computing the centre).
        private void DeleteSelectedEdge()
        {
            int idx = _edgeList.SelectedIndex;
            if (idx < 0) return;
            _chuckFinder.RemoveAt(idx);
            RefreshEdgeUi();
            _status.Text = $"Deleted edge point {idx + 1}.";
        }

        private void ClearEdges()
        {
            _chuckFinder.Clear();
            RefreshEdgeUi();
            _status.Text = "Edge points cleared.";
        }

        private void RefreshEdgeUi()
        {
            int sel = _edgeList.SelectedIndex;
            _edgeList.BeginUpdate();
            _edgeList.Items.Clear();
            int i = 1;
            foreach ((double X, double Y) p in _chuckFinder.Points)
                _edgeList.Items.Add($"{i++,2}: X={p.X,9:F0} Y={p.Y,9:F0}");
            _edgeList.EndUpdate();
            if (sel >= 0 && sel < _edgeList.Items.Count) _edgeList.SelectedIndex = sel;
            // Everything that edits the point set is locked out while either automatic run is live
            // (RefreshAutoUi routes through here, so this is the single gate).
            bool manual = !_autoRunning && !_waferRunning;
            _edgeBtn.Enabled = manual && _view.IsCameraOpen;
            _edgeAtCrossBtn.Enabled = manual && _view.IsCameraOpen;
            _centreBtn.Enabled = manual && _chuckFinder.Count >= 3;
            _edgeClearBtn.Enabled = manual && _chuckFinder.Count > 0;
            _edgeDeleteBtn.Enabled = manual && _edgeList.SelectedIndex >= 0;
        }

        // Circle-fits the chuck edge points (step space) and persists the centre.
        private void ComputeCentre()
        {
            bool ok = TryComputeAndSaveCentre(_chuckFinder, "Chuck",
                (x, y, fit) =>
                {
                    _owner.Calibration.ChuckCenterX = x;
                    _owner.Calibration.ChuckCenterY = y;
                    // Persisted so the auto centre-find's nominal radius — which arms its travel guard
                    // — defaults from a measurement instead of being re-typed each session.
                    _owner.Calibration.ChuckRadius = (long)Math.Round(fit.Radius);
                },
                out (long X, long Y)? centre, out string text);
            if (ok)
            {
                _chuckCentre = centre;
                _goCentreBtn.Enabled = true;
                _status.Text = "Chuck centre computed and saved.";
            }
            _centreResult.Text = text;
        }

        private Task GoToCentreAsync() => GoToCentreAsync("chuck", _chuckCentre);
    }
}
