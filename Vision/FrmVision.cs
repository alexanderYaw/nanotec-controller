using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using HalconDotNet;

namespace MotorControlApp
{
    /// <summary>
    /// Stage A live-view test: opens the HALCON USB3Vision camera, streams frames into a
    /// live pane, and a "Capture Image" button freezes the current frame into a second pane.
    /// Pure camera proof-out — no motion, no edge detection yet. Frames are shown via
    /// <see cref="HalconBitmap"/> (PictureBox) because HALCON's WinForms control isn't usable
    /// under .NET 10 here.
    ///
    /// Smoothness: a background thread grabs AND converts frames; the UI thread only paints
    /// the newest finished frame (older ones are dropped). This keeps the blocking grab and
    /// the per-pixel conversion off the UI thread, and frames are downscaled to the view size
    /// before conversion — the two things that made the original timer-on-UI version laggy.
    /// </summary>
    public sealed class FrmVision : Form
    {
        private readonly VisionCamera _camera = new();
        private readonly PictureBox _liveBox = new() { SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black };
        private readonly PictureBox _capturedBox = new() { SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black };
        private readonly Button _captureBtn = new() { Text = "Capture Image", Enabled = false };
        private readonly Button _saveBtn = new() { Text = "Save Image", Enabled = false };
        private readonly Button _crosshairBtn = new() { Text = "Crosshair: Off" };
        private readonly Button _invertBtn = new() { Text = "Invert: On" };
        private readonly Label _status = new() { Text = "Opening camera...", AutoSize = true };

        // --- Camera-scale calibration (manual jog + capture; owner supplies motor position) --
        private readonly FrmMain? _owner;
        private readonly ReflectiveMarkDetector _markDetector = new();
        private readonly CameraCalibrator _calibrator = new();
        private volatile bool _sampleRequested;     // UI asked GrabLoop for a calibration sample
        private readonly Button _sampleBtn = new() { Text = "Add Sample", Enabled = false };
        private readonly Button _computeBtn = new() { Text = "Compute && Save A", Enabled = false };
        private readonly Button _clearBtn = new() { Text = "Clear", Enabled = false };
        private readonly TextBox _sampleList = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 8F), BackColor = Color.White };
        private readonly Label _calibResult = new() { BorderStyle = BorderStyle.FixedSingle, Font = new Font("Consolas", 8F), TextAlign = ContentAlignment.TopLeft };

        private bool _showCrosshair;
        private volatile bool _invertView = true;  // 180° flip; camera is mounted inverted
        private volatile bool _captureRequested;   // UI asked GrabLoop for a full-res capture

        private Task? _grabTask;
        private CancellationTokenSource? _cts;
        private readonly object _frameLock = new();
        private Bitmap? _pending;          // newest converted frame not yet shown
        private bool _displayQueued;        // a BeginInvoke(ShowPending) is already in flight
        private volatile int _viewW = 480, _viewH = 440;   // downscale target (live box size)

        public FrmVision(FrmMain owner)
        {
            _owner = owner;
            Text = "Vision - live view";
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9F);
            ClientSize = new Size(1240, 540);
            MinimumSize = new Size(1000, 480);

            var liveLabel = new Label { Text = "Live", Location = new Point(12, 8), AutoSize = true, Font = new Font("Segoe UI", 10F, FontStyle.Bold) };
            var capLabel = new Label { Text = "Captured", Location = new Point(508, 8), AutoSize = true, Font = new Font("Segoe UI", 10F, FontStyle.Bold) };

            _liveBox.Location = new Point(12, 32);
            _liveBox.Size = new Size(480, 440);
            _liveBox.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left;
            _liveBox.Resize += (s, e) => { _viewW = Math.Max(1, _liveBox.Width); _viewH = Math.Max(1, _liveBox.Height); };

            _capturedBox.Location = new Point(508, 32);
            _capturedBox.Size = new Size(480, 440);
            _capturedBox.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left;

            _captureBtn.Location = new Point(12, 484);
            _captureBtn.Size = new Size(120, 40);
            _captureBtn.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _captureBtn.Click += (s, e) => CaptureFrame();

            _saveBtn.Location = new Point(140, 484);
            _saveBtn.Size = new Size(120, 40);
            _saveBtn.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _saveBtn.Click += (s, e) => SaveCaptured();

            _crosshairBtn.Location = new Point(268, 484);
            _crosshairBtn.Size = new Size(120, 40);
            _crosshairBtn.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _crosshairBtn.Click += (s, e) =>
            {
                _showCrosshair = !_showCrosshair;
                _crosshairBtn.Text = _showCrosshair ? "Crosshair: On" : "Crosshair: Off";
                _liveBox.Invalidate();
            };
            _liveBox.Paint += DrawCrosshair;

            _invertBtn.Location = new Point(396, 484);
            _invertBtn.Size = new Size(120, 40);
            _invertBtn.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _invertBtn.Click += (s, e) =>
            {
                _invertView = !_invertView;
                _invertBtn.Text = _invertView ? "Invert: On" : "Invert: Off";
            };

            _status.Location = new Point(524, 496);
            _status.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;

            // ---- Camera-scale calibration column (right) -------------------------
            // Manual workflow: jog the table (main window) to keep the ring in view, then
            // Add Sample — that records (motor X,Y) + the detected ring centre (row,col).
            // After >= 3 samples spanning both axes, Compute & Save solves the pixel->step
            // affine and writes it to the shared CalibrationStore.
            var calibLabel = new Label { Text = "Camera scale calibration", Location = new Point(1000, 8), AutoSize = true, Font = new Font("Segoe UI", 10F, FontStyle.Bold), Anchor = AnchorStyles.Top | AnchorStyles.Right };

            _sampleBtn.Location = new Point(1000, 34);
            _sampleBtn.Size = new Size(228, 30);
            _sampleBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _sampleBtn.Click += (s, e) => RequestSample();

            _clearBtn.Location = new Point(1000, 68);
            _clearBtn.Size = new Size(228, 26);
            _clearBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _clearBtn.Click += (s, e) => ClearSamples();

            _sampleList.Location = new Point(1000, 100);
            _sampleList.Size = new Size(228, 210);
            _sampleList.Anchor = AnchorStyles.Top | AnchorStyles.Right;

            _computeBtn.Location = new Point(1000, 316);
            _computeBtn.Size = new Size(228, 32);
            _computeBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _computeBtn.Click += (s, e) => ComputeAndSave();

            _calibResult.Location = new Point(1000, 354);
            _calibResult.Size = new Size(228, 110);
            _calibResult.Anchor = AnchorStyles.Top | AnchorStyles.Right;

            Controls.Add(liveLabel);
            Controls.Add(capLabel);
            Controls.Add(_liveBox);
            Controls.Add(_capturedBox);
            Controls.Add(_captureBtn);
            Controls.Add(_saveBtn);
            Controls.Add(_crosshairBtn);
            Controls.Add(_invertBtn);
            Controls.Add(_status);
            Controls.Add(calibLabel);
            Controls.Add(_sampleBtn);
            Controls.Add(_clearBtn);
            Controls.Add(_sampleList);
            Controls.Add(_computeBtn);
            Controls.Add(_calibResult);

            Load += (s, e) => StartCamera();
            FormClosing += (s, e) => Teardown();
        }

        private void StartCamera()
        {
            try { _camera.Open(); }
            catch (HOperatorException ex) { _status.Text = "Camera open failed: " + ex.Message; return; }

            _viewW = Math.Max(1, _liveBox.Width);
            _viewH = Math.Max(1, _liveBox.Height);
            _captureBtn.Enabled = true;
            _sampleBtn.Enabled = true;
            _status.Text = "Live.";
            _cts = new CancellationTokenSource();
            _grabTask = Task.Run(() => GrabLoop(_cts.Token));
        }

        // Background: grab + convert as fast as the camera allows, publishing only the newest
        // frame to the UI. Runs off the UI thread so neither the blocking grab nor the pixel
        // copy can stall input/painting.
        private void GrabLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                HObject frame;
                try { frame = _camera.GrabImage(); }
                catch (HOperatorException ex) { PostStatus("Live grab stopped: " + ex.Message); return; }

                // Full-resolution capture: convert this frame at native size (no downscale)
                // before it's disposed. Done here, not on the UI thread, so we never touch the
                // HALCON acquisition handle from two threads.
                if (_captureRequested)
                {
                    _captureRequested = false;
                    Bitmap? full = null;
                    try { full = HalconBitmap.ToBitmap(frame, 0, 0); }
                    catch (HOperatorException) { full = null; }   // skip; user can retry
                    if (full != null)
                    {
                        if (_invertView) full.RotateFlip(RotateFlipType.Rotate180FlipNone);
                        try { BeginInvoke(new Action(() => SetCaptured(full))); }
                        catch (InvalidOperationException) { full.Dispose(); }   // closing
                    }
                }

                // Calibration sample: detect the fiducial on the RAW frame (no flip — pixel
                // coords must be consistent), and hand a raw full-res bitmap to the UI so the
                // user can confirm the detection. Done here so the HALCON handle stays single-
                // threaded, like the capture path above.
                if (_sampleRequested)
                {
                    _sampleRequested = false;
                    bool found;
                    ReflectiveMarkDetector.Mark mark;
                    try { found = _markDetector.TryDetect(frame, out mark); }
                    catch (HOperatorException) { found = false; mark = default; }

                    Bitmap? raw = null;
                    try { raw = HalconBitmap.ToBitmap(frame, 0, 0); }
                    catch (HOperatorException) { raw = null; }
                    if (raw != null)
                    {
                        bool f = found; ReflectiveMarkDetector.Mark m = mark; Bitmap r = raw;
                        try { BeginInvoke(new Action(() => OnSampleGrabbed(f, m, r))); }
                        catch (InvalidOperationException) { raw.Dispose(); }   // closing
                    }
                }

                Bitmap bmp;
                try { bmp = HalconBitmap.ToBitmap(frame, _viewW, _viewH); }
                catch (HOperatorException) { frame.Dispose(); continue; }   // skip a bad frame
                finally { frame.Dispose(); }

                // Camera is mounted inverted: flip on both axes (180°) so the view is upright.
                if (_invertView) bmp.RotateFlip(RotateFlipType.Rotate180FlipNone);

                Bitmap? dropped;
                bool queue;
                lock (_frameLock)
                {
                    dropped = _pending;        // UI never picked this up -> discard it
                    _pending = bmp;
                    queue = !_displayQueued;
                    if (queue) _displayQueued = true;
                }
                dropped?.Dispose();

                if (queue)
                {
                    try { BeginInvoke(new Action(ShowPending)); }
                    catch (InvalidOperationException) { return; }   // handle gone (closing)
                }
            }
        }

        private void ShowPending()
        {
            Bitmap? next;
            lock (_frameLock) { next = _pending; _pending = null; _displayQueued = false; }
            if (next == null) return;
            if (IsDisposed) { next.Dispose(); return; }

            Image? old = _liveBox.Image;
            _liveBox.Image = next;
            if (!ReferenceEquals(old, next)) old?.Dispose();
        }

        // Asks GrabLoop to convert the next frame at full resolution; SetCaptured shows it.
        private void CaptureFrame()
        {
            if (!_camera.IsOpen) return;
            _captureRequested = true;
            _status.Text = "Capturing full-resolution frame...";
        }

        // UI thread: takes ownership of the full-res capture from GrabLoop. The captured pane
        // is SizeMode.Zoom, so it shows the full image scaled to fit; saving keeps full res.
        private void SetCaptured(Bitmap full)
        {
            if (IsDisposed) { full.Dispose(); return; }
            ShowCaptured(full);
            _status.Text = $"Captured {full.Width}x{full.Height} at {DateTime.Now:HH:mm:ss}.";
        }

        // Puts a bitmap in the captured pane, taking ownership (disposes the previous one).
        private void ShowCaptured(Bitmap bmp)
        {
            if (IsDisposed) { bmp.Dispose(); return; }
            Image? old = _capturedBox.Image;
            _capturedBox.Image = bmp;
            if (!ReferenceEquals(old, bmp)) old?.Dispose();
            _saveBtn.Enabled = true;
        }

        // ---- Camera-scale calibration -------------------------------------------------

        // Asks GrabLoop to detect the fiducial in the next frame (the table is held still by
        // the user during a manual sample).
        private void RequestSample()
        {
            if (!_camera.IsOpen) return;
            _sampleRequested = true;
            _status.Text = "Sampling: detecting fiducial...";
        }

        // UI thread: GrabLoop found (or didn't) the fiducial and handed us a raw full-res
        // frame. Pair the detected pixel with the current motor position and store a sample.
        private void OnSampleGrabbed(bool found, ReflectiveMarkDetector.Mark mark, Bitmap raw)
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
        private static void DrawMarkOverlay(Bitmap bmp, ReflectiveMarkDetector.Mark mark)
        {
            using var g = Graphics.FromImage(bmp);
            float cx = (float)mark.Column, cy = (float)mark.Row, r = (float)mark.Radius;
            using var pen = new Pen(Color.Lime, Math.Max(2f, bmp.Width / 400f));
            g.DrawEllipse(pen, cx - r, cy - r, 2 * r, 2 * r);
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

        // Overlays a crosshair through the centre of the live pane. With SizeMode.Zoom the
        // framed image is centred in the box, so box-centre = frame geometric centre. This is
        // a visual reference only — NOT the calibrated optical/Theta centre (see Stage B).
        private void DrawCrosshair(object? sender, PaintEventArgs e)
        {
            if (!_showCrosshair) return;
            int cx = _liveBox.ClientSize.Width / 2;
            int cy = _liveBox.ClientSize.Height / 2;
            using var pen = new Pen(Color.Lime, 1f);
            e.Graphics.DrawLine(pen, cx, 0, cx, _liveBox.ClientSize.Height);
            e.Graphics.DrawLine(pen, 0, cy, _liveBox.ClientSize.Width, cy);
        }

        // Saves the captured frame as a PNG under Desktop\images (created if missing).
        private void SaveCaptured()
        {
            if (_capturedBox.Image == null) { _status.Text = "Capture an image first."; return; }
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "images");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir,
                    "capture_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".png");
                _capturedBox.Image.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                _status.Text = "Saved " + path;
            }
            catch (Exception ex)
            {
                _status.Text = "Save failed: " + ex.Message;
            }
        }

        private void PostStatus(string text)
        {
            try { BeginInvoke(new Action(() => _status.Text = text)); }
            catch (InvalidOperationException) { /* closing */ }
        }

        private void Teardown()
        {
            _cts?.Cancel();
            try { _grabTask?.Wait(500); } catch { /* best effort; camera streams so grab returns fast */ }
            _camera.Dispose();
            lock (_frameLock) { _pending?.Dispose(); _pending = null; }
            _liveBox.Image?.Dispose();
            _capturedBox.Image?.Dispose();
        }
    }
}
