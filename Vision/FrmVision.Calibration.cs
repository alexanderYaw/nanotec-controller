using System;
using System.Drawing;
using System.Text;
using HalconDotNet;

namespace NanotecController
{
    // FrmVision — camera-scale calibration: manually capture (motor X,Y ↔ detected fiducial pixel)
    // samples and solve the pixel→step affine, writing it to the shared CalibrationStore.
    // (Partial of FrmVision; layout + grab loop live in FrmVision.cs.)
    public sealed partial class FrmVision
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
                try { found = _markDetector.TryDetect(frame, out mark); }
                catch (HOperatorException) { found = false; mark = default; }
                _view.PostFrameBitmap(frame, flip: false, raw => OnSampleGrabbed(found, mark, raw));
            });
            _status.Text = "Sampling: detecting fiducial...";
        }

        // UI thread: the grab thread found (or didn't) the fiducial and handed us a raw full-res
        // frame. Pair the detected pixel with the current motor position and store a sample.
        private void OnSampleGrabbed(bool found, SolidCircleDetector.Mark mark, Bitmap raw)
        {
            if (IsDisposed) { raw.Dispose(); return; }

            if (!found)
            {
                ShowCaptured(raw);   // show what the camera saw so the user can adjust
                _status.Text = "Sample: fiducial NOT found - adjust framing/lighting (test thresholds in HDevelop).";
                return;
            }
            if (_owner == null
                || !_owner.TryCurrentUser(AxisId.X, out long x)
                || !_owner.TryCurrentUser(AxisId.Y, out long y))
            {
                ShowCaptured(raw);
                _status.Text = "Sample: motor position unavailable - connect & enable so the live position is updating.";
                return;
            }

            _calibrator.Add(mark.Row, mark.Column, x, y);
            DrawMarkOverlay(raw, mark);
            ShowCaptured(raw);
            RefreshCalibUi();
            _status.Text = $"Sample {_calibrator.Count}: pos=({x}, {y})  px=(r {mark.Row:F1}, c {mark.Column:F1})";
        }

        // Draws the detected circle + centre cross onto the (raw) sample bitmap. GDI x = column,
        // y = row. Drawn at full-res coordinates; the Zoom pane scales it to fit.
        private static void DrawMarkOverlay(Bitmap bmp, SolidCircleDetector.Mark mark)
        {
            using var g = Graphics.FromImage(bmp);
            float cx = (float)mark.Column, cy = (float)mark.Row, r = (float)mark.Radius;
            float w = VisionOverlay.PenWidth(bmp.Width);
            VisionOverlay.DrawPoint(g, mark.Row, mark.Column, r, Color.Lime, w);
            using var pen = new Pen(Color.Lime, w);   // centre cross sized to the fiducial radius
            g.DrawLine(pen, cx - r, cy, cx + r, cy);
            g.DrawLine(pen, cx, cy - r, cx, cy + r);
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
            if (_owner != null)
            {
                _owner.Calibration.PixelStep = a;
                try { _owner.Calibration.Save(); }
                catch (Exception ex) { _calibResult.Text = $"Computed but SAVE failed:\r\n{ex.Message}"; return; }
            }
            _calibResult.Text =
                $"Saved.  N={a.SampleCount}  RMS resid={resid:F1} steps\r\n" +
                $"dX/drow={a.Xr:F3}  dX/dcol={a.Xc:F3}\r\n" +
                $"dY/drow={a.Yr:F3}  dY/dcol={a.Yc:F3}";
            _status.Text = "Calibration computed and saved to calibration.json.";
        }
    }
}
