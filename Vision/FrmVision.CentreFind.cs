using System;
using System.Drawing;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using HalconDotNet;

namespace NanotecController
{
    // FrmVision — chuck + wafer centre-find: capture ≥3 rim points (step space), circle-fit them to
    // a centre, and drive there. The two features share CentreFinder plus the compute/go-to halves
    // (TryComputeAndSaveCentre / GoToCentreAsync); only the grab callbacks differ, because the chuck
    // edge (focus) and wafer edge (brightness) use different detectors and overlays.
    // (Partial of FrmVision; layout + grab loop live in FrmVision.cs.)
    public sealed partial class FrmVision
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
            PixelStepAffine? a = _owner?.Calibration.PixelStep;
            if (a == null)
            {
                ShowCaptured(raw);
                _status.Text = "Edge: needs the camera-scale calibration first.";
                return;
            }
            if (!_owner!.TryCurrentUser(AxisId.X, out long mx) || !_owner.TryCurrentUser(AxisId.Y, out long my))
            {
                ShowCaptured(raw);
                _status.Text = "Edge: motor position unavailable — connect & enable.";
                return;
            }

            var (ex, ey) = _chuckFinder.Add(edge.Row, edge.Column, crossRow, crossCol, a, mx, my);

            DrawEdgeOverlay(raw, edge, crossRow, crossCol);
            ShowCaptured(raw);
            RefreshEdgeUi();
            _status.Text = $"Edge {_chuckFinder.Count}: px=(r {edge.Row:F0}, c {edge.Column:F0}) → step=({ex:F0}, {ey:F0})";
        }

        // Draws the frame-centre crosshair (green) and the detected edge point (yellow circle).
        private static void DrawEdgeOverlay(Bitmap bmp, ChuckEdgeDetector.EdgePoint edge, double crossRow, double crossCol)
        {
            using var g = Graphics.FromImage(bmp);
            float w = VisionOverlay.PenWidth(bmp.Width), rad = bmp.Width / 60f;
            VisionOverlay.DrawCrosshair(g, bmp.Width, crossRow, crossCol, Color.Lime);
            VisionOverlay.DrawPoint(g, edge.Row, edge.Column, rad, Color.Yellow, w);
        }

        // UI thread: a wafer-rim point was (or wasn't) found. On success convert it to step space
        // (E = M + A·(p_cross − p_edge), the motor position that brings this rim point to the
        // crosshair — same maths as the chuck edge), add it to the wafer set, and overlay the
        // detected boundary (cyan) + the rim point (yellow) on the captured pane. (cRows/cCols px.)
        private void OnWaferGrabbed(bool found, WaferEdgeDetector.EdgePoint edge,
                                    double[] cRows, double[] cCols, double crossRow, double crossCol, Bitmap raw)
        {
            if (IsDisposed) { raw.Dispose(); return; }
            if (!found)
            {
                ShowCaptured(raw);
                _status.Text = "Wafer edge NOT found — adjust lighting or tune WaferEdgeDetector (WaferIsBrighter / radii).";
                return;
            }
            PixelStepAffine? a = _owner?.Calibration.PixelStep;
            if (a == null)
            {
                ShowCaptured(raw);
                _status.Text = "Wafer edge: needs the camera-scale calibration first.";
                return;
            }
            if (!_owner!.TryCurrentUser(AxisId.X, out long mx) || !_owner.TryCurrentUser(AxisId.Y, out long my))
            {
                ShowCaptured(raw);
                _status.Text = "Wafer edge: motor position unavailable — connect & enable.";
                return;
            }

            var (ex, ey) = _waferFinder.Add(edge.Row, edge.Column, crossRow, crossCol, a, mx, my);

            using (var g = Graphics.FromImage(raw))
            {
                float w = VisionOverlay.PenWidth(raw.Width), rad = raw.Width / 60f;
                VisionOverlay.DrawCrosshair(g, raw.Width, crossRow, crossCol, Color.Lime);
                VisionOverlay.DrawContour(g, cRows, cCols, Color.Cyan, w);
                VisionOverlay.DrawPoint(g, edge.Row, edge.Column, rad, Color.Yellow, w * 1.5f);
            }

            ShowCaptured(raw);
            RefreshWaferUi();
            _status.Text = $"Wafer edge {_waferFinder.Count}: px=(r {edge.Row:F0}, c {edge.Column:F0}) → step=({ex:F0}, {ey:F0})";
        }

        private void ClearWaferPoints()
        {
            _waferFinder.Clear();
            RefreshWaferUi();
            _status.Text = "Wafer edge points cleared.";
        }

        private void RefreshWaferUi()
        {
            _waferCentreBtn.Enabled = _waferFinder.Count >= 3;
            _waferClearBtn.Enabled = _waferFinder.Count > 0;
        }

        // Shared chuck/wafer compute+persist: circle-fits the finder's points, stores the centre via
        // the per-feature setter, and persists. Returns false (with display text) on a fit/save failure;
        // on success, text is the result readout and centre is set. The grab callbacks differ per
        // detector (overlay + type), but the compute and go-to halves are identical modulo label/field.
        private bool TryComputeAndSaveCentre(CentreFinder finder, string label, Action<long, long> store,
                                             out (long X, long Y)? centre, out string text)
        {
            centre = null;
            if (!finder.TryComputeCentre(out long cx, out long cy, out CircleFit.Result fit, out string? err))
            {
                text = "Fit failed:\r\n" + err;
                return false;
            }
            if (_owner != null)
            {
                store(cx, cy);
                try { _owner.Calibration.Save(); }
                catch (Exception ex) { text = $"Computed but SAVE failed:\r\n{ex.Message}"; return false; }
            }
            centre = (cx, cy);
            text = $"{label} (N={finder.Count}): X={cx} Y={cy}\r\nR={fit.Radius:F0}  RMS={fit.RmsError:F0} steps";
            return true;
        }

        // Shared "Go to centre": confirm, then issue the absolute X/Y move (Z unchanged) via FrmMain.
        private async Task GoToCentreAsync(string label, (long X, long Y)? centre)
        {
            if (_owner == null || centre == null) return;
            long cx = centre.Value.X, cy = centre.Value.Y;
            if (MessageBox.Show(this,
                    $"Move the {label} centre to the view centre?\r\nTarget: X={cx}, Y={cy}  (Z unchanged).",
                    $"Go to {label} centre", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            _status.Text = $"Moving to {label} centre...";
            await _owner.MoveToAsync(cx.ToString(), cy.ToString(), "");
            _status.Text = $"Go to {label} centre: move issued (see main-window log).";
        }

        // Circle-fits the wafer rim points (step space) and persists the centre (separate from chuck).
        private void ComputeWaferCentre()
        {
            bool ok = TryComputeAndSaveCentre(_waferFinder, "Wafer",
                (x, y) => { _owner!.Calibration.WaferCenterX = x; _owner.Calibration.WaferCenterY = y; },
                out (long X, long Y)? centre, out string text);
            if (ok)
            {
                _waferCentre = centre;
                _waferGoBtn.Enabled = true;
                _status.Text = "Wafer centre computed and saved.";
            }
            _waferResult.Text = text;
        }

        private Task GoToWaferCentreAsync() => GoToCentreAsync("wafer", _waferCentre);

        private void ClearEdges()
        {
            _chuckFinder.Clear();
            RefreshEdgeUi();
            _status.Text = "Edge points cleared.";
        }

        private void RefreshEdgeUi()
        {
            var sb = new StringBuilder();
            int i = 1;
            foreach ((double X, double Y) p in _chuckFinder.Points)
                sb.AppendLine($"{i++,2}: X={p.X,9:F0} Y={p.Y,9:F0}");
            _edgeList.Text = sb.ToString();
            _centreBtn.Enabled = _chuckFinder.Count >= 3;
            _edgeClearBtn.Enabled = _chuckFinder.Count > 0;
        }

        // Circle-fits the chuck edge points (step space) and persists the centre.
        private void ComputeCentre()
        {
            bool ok = TryComputeAndSaveCentre(_chuckFinder, "Chuck",
                (x, y) => { _owner!.Calibration.ChuckCenterX = x; _owner.Calibration.ChuckCenterY = y; },
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
