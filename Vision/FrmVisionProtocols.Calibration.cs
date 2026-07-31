using System;
using System.Drawing;
using System.Text;
using HalconDotNet;

namespace NanotecController
{
    // FrmVisionProtocols — camera-scale calibration: manually capture (motor X,Y ↔ detected fiducial pixel)
    // samples and solve the pixel→step affine, writing it to the shared CalibrationStore.
    // (Partial of FrmVisionProtocols; layout lives in FrmVisionProtocols.cs.)
    public sealed partial class FrmVisionProtocols
    {
        // Asks the grab thread to detect the fiducial in the next frame (the table is held
        // still by the user during a manual sample).
        private void RequestSample()
        {
            if (!_view.IsCameraOpen) return;
            _view.RequestFrame(frame =>
            {
                bool found;
                SolidCircleDetector.Mark mark;
                // Read the detector's diagnostics HERE, on the grab thread that just wrote them,
                // and carry them across in the closure — the UI callback runs later, by which
                // time another sample could have overwritten the detector's state.
                string? why;
                double cut;
                try
                {
                    found = _markDetector.TryDetect(frame, out mark);
                    why = _markDetector.LastFailure;
                    cut = _markDetector.LastThreshold;
                }
                catch (HOperatorException ex)
                {
                    found = false; mark = default; cut = double.NaN;
                    why = "HALCON error: " + ex.GetErrorMessage();
                }
                _view.PostFrameBitmap(frame, flip: false, raw => OnSampleGrabbed(found, mark, raw, why, cut));
            });
            _status.Text = "Sampling: detecting fiducial...";
        }

        // UI thread: the grab thread found (or didn't) the fiducial and handed us a raw full-res
        // frame. Pair the detected pixel with the current motor position and store a sample.
        private void OnSampleGrabbed(bool found, SolidCircleDetector.Mark mark, Bitmap raw,
                                     string? why, double cut)
        {
            if (IsDisposed) { raw.Dispose(); return; }

            if (!found)
            {
                ShowCaptured(raw);   // show what the camera saw so the user can adjust
                _status.Text = "Sample: fiducial NOT found - "
                             + (why ?? "adjust framing/lighting (test thresholds in HDevelop).");
                return;
            }
            if (!_owner.TryCurrentUser(AxisId.X, out long x)
                || !_owner.TryCurrentUser(AxisId.Y, out long y))
            {
                ShowCaptured(raw);
                _status.Text = "Sample: motor position unavailable - connect & enable so the live position is updating.";
                return;
            }

            _calibrator.Add(mark.Row, mark.Column, x, y);
            raw = DrawMarkOverlay(raw, mark);   // may return a widened copy; see EnsureDrawable
            ShowCaptured(raw);
            RefreshCalibUi();
            _status.Text = $"Sample {_calibrator.Count}: pos=({x}, {y})  px=(r {mark.Row:F1}, c {mark.Column:F1})"
                         + (double.IsNaN(cut) ? "" : $"  cut {cut:F0}");
        }

        // Draws the detected circle + centre cross onto the (raw) sample bitmap. GDI x = column,
        // y = row. Drawn at full-res coordinates; the Zoom pane scales it to fit. RETURNS the
        // bitmap drawn on — a mono frame is indexed and gets replaced by a drawable copy, so the
        // caller must keep the returned reference (the original is disposed).
        private static Bitmap DrawMarkOverlay(Bitmap bmp, SolidCircleDetector.Mark mark)
        {
            bmp = VisionOverlay.EnsureDrawable(bmp);
            using var g = Graphics.FromImage(bmp);
            float cx = (float)mark.Column, cy = (float)mark.Row, r = (float)mark.Radius;
            float w = VisionOverlay.PenWidth(bmp.Width);
            VisionOverlay.DrawPoint(g, mark.Row, mark.Column, r, Color.Lime, w);
            using var pen = new Pen(Color.Lime, w);   // centre cross sized to the fiducial radius
            g.DrawLine(pen, cx - r, cy, cx + r, cy);
            g.DrawLine(pen, cx, cy - r, cx, cy + r);
            return bmp;
        }

        private void ClearSamples()
        {
            _calibrator.Clear();
            RefreshCalibUi();
            _status.Text = "Calibration samples cleared.";
        }

        private void RefreshCalibUi()
        {
            var sb = new StringBuilder();
            int i = 1;
            foreach (CameraCalibrator.Sample s in _calibrator.Samples)
                sb.AppendLine($"{i++,2}: X={s.X,8} Y={s.Y,8}  r={s.Row,7:F1} c={s.Column,7:F1}");
            _sampleList.Text = sb.ToString();
            _computeBtn.Enabled = _calibrator.Count >= 3;
            _clearBtn.Enabled = _calibrator.Count > 0;
        }

        // Solves the pixel->step affine from the samples and saves it to the shared store.
        private void ComputeAndSave()
        {
            if (!_calibrator.TrySolve(out PixelStepAffine a, out double resid, out string? err))
            {
                _calibResult.Text = "Solve failed:\r\n" + err;
                return;
            }
            _owner.Calibration.PixelStep = a;
            try { _owner.Calibration.Save(); }
            catch (Exception ex) { _calibResult.Text = $"Computed but SAVE failed:\r\n{ex.Message}"; return; }
            _calibResult.Text =
                $"Saved.  N={a.SampleCount}  RMS resid={resid:F1} steps\r\n" +
                $"dX/drow={a.Xr:F3}  dX/dcol={a.Xc:F3}\r\n" +
                $"dY/drow={a.Yr:F3}  dY/dcol={a.Yc:F3}";
            _status.Text = "Calibration computed and saved to calibration.json.";
        }
    }
}
