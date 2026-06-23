using System;
using System.Collections.Generic;
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

        // Drift-corrected "vision jog": commands X+Y together (via the calibration) so the
        // on-screen motion is purely horizontal/vertical. Hold-to-jog like the main d-pad.
        private readonly NumericUpDown _vSpeed = new() { Minimum = 0, Maximum = 6000, Value = 1000, Increment = 100, Enabled = false };
        private readonly Button _vUp = new() { Text = "▲", Font = new Font("Segoe UI Symbol", 12F), Enabled = false };
        private readonly Button _vDown = new() { Text = "▼", Font = new Font("Segoe UI Symbol", 12F), Enabled = false };
        private readonly Button _vLeft = new() { Text = "◀", Font = new Font("Segoe UI Symbol", 12F), Enabled = false };
        private readonly Button _vRight = new() { Text = "▶", Font = new Font("Segoe UI Symbol", 12F), Enabled = false };

        // --- Chuck centre-find: capture >=3 edge points (step space), circle-fit, go to centre --
        private readonly ChuckEdgeDetector _edgeDetector = new();
        private readonly List<(double X, double Y)> _edgePoints = new();   // user-frame step coords
        private volatile bool _edgeRequested;
        private readonly Button _edgeBtn = new() { Text = "Add Edge", Enabled = false };
        private readonly Button _edgeClearBtn = new() { Text = "Clear Edges", Enabled = false };
        private readonly Button _centreBtn = new() { Text = "Compute Centre", Enabled = false };
        private readonly Button _goCentreBtn = new() { Text = "Go to Centre", Enabled = false };
        private readonly TextBox _edgeList = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 8F), BackColor = Color.White };
        private readonly Label _centreResult = new() { BorderStyle = BorderStyle.FixedSingle, Font = new Font("Consolas", 8F), TextAlign = ContentAlignment.TopLeft };
        private (long X, long Y)? _chuckCentre;   // last computed/loaded centre (user frame)

        // --- Rotate about crosshair: combine Θ + X/Y so the point under the crosshair stays
        // pinned while the chuck turns. Relative ("Rotate by"), absolute ("Rotate to"), and the
        // one-time handedness "Sign test". All gated/executed by FrmMain (serialized motion).
        private readonly NumericUpDown _rotBy = new() { Minimum = -360, Maximum = 360, Value = 90, DecimalPlaces = 1, Increment = 5, Enabled = false };
        private readonly Button _rotByBtn = new() { Text = "Rotate by°", Enabled = false };
        private readonly NumericUpDown _rotTo = new() { Minimum = 0, Maximum = 360, Value = 0, DecimalPlaces = 1, Increment = 5, Enabled = false };
        private readonly Button _rotToBtn = new() { Text = "Rotate to°", Enabled = false };
        private readonly Label _signLabel = new() { AutoSize = true };
        private readonly Button _signTestBtn = new() { Text = "Sign test", Enabled = false };
        // Hold-to-rotate: rotates about the crosshair while the button is held (like a jog button).
        private readonly Button _rotHoldCcwBtn = new() { Text = "⟲ Hold", Font = new Font("Segoe UI Symbol", 11F), Enabled = false };
        private readonly Button _rotHoldCwBtn = new() { Text = "Hold ⟳", Font = new Font("Segoe UI Symbol", 11F), Enabled = false };

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
            ClientSize = new Size(1490, 640);
            MinimumSize = new Size(1200, 680);

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

            // ---- Drift-corrected vision jog (uses the calibration) ---------------
            // Each press drives X+Y together so the live view moves purely along one screen
            // axis. Accounts for the display Invert state. Hold-to-jog (MouseDown/MouseUp).
            var vLabel = new Label { Text = "Vision jog (drift-corrected)", Location = new Point(1000, 470), AutoSize = true, Font = new Font("Segoe UI", 10F, FontStyle.Bold), Anchor = AnchorStyles.Top | AnchorStyles.Right };
            var vSpeedLabel = new Label { Text = "Speed:", Location = new Point(1000, 496), AutoSize = true, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            _vSpeed.Location = new Point(1060, 493);
            _vSpeed.Size = new Size(80, 24);
            _vSpeed.Anchor = AnchorStyles.Top | AnchorStyles.Right;

            void VisionPad(Button b, int x, int y, int sx, int sy)
            {
                b.Location = new Point(x, y);
                b.Size = new Size(46, 36);
                b.Anchor = AnchorStyles.Top | AnchorStyles.Right;
                b.MouseDown += (s, e) => VisionJog(sx, sy);
                b.MouseUp += (s, e) => _owner?.VisionStop();
            }
            VisionPad(_vUp, 1090, 522, 0, +1);
            VisionPad(_vLeft, 1044, 560, -1, 0);
            VisionPad(_vRight, 1136, 560, +1, 0);
            VisionPad(_vDown, 1090, 598, 0, -1);

            // ---- Chuck centre-find column (far right) ----------------------------
            // Jog so the chuck EDGE is in view, Add Edge (detects the edge point nearest the
            // crosshair, converts to step space via the calibration). After >=3 around the rim,
            // Compute Centre circle-fits them; Go to Centre drives there.
            var centreLabel = new Label { Text = "Chuck centre-find", Location = new Point(1248, 8), AutoSize = true, Font = new Font("Segoe UI", 10F, FontStyle.Bold), Anchor = AnchorStyles.Top | AnchorStyles.Right };

            _edgeBtn.Location = new Point(1248, 34);
            _edgeBtn.Size = new Size(228, 30);
            _edgeBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _edgeBtn.Click += (s, e) => RequestEdge();

            _edgeClearBtn.Location = new Point(1248, 68);
            _edgeClearBtn.Size = new Size(228, 26);
            _edgeClearBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _edgeClearBtn.Click += (s, e) => ClearEdges();

            _edgeList.Location = new Point(1248, 100);
            _edgeList.Size = new Size(228, 210);
            _edgeList.Anchor = AnchorStyles.Top | AnchorStyles.Right;

            _centreBtn.Location = new Point(1248, 316);
            _centreBtn.Size = new Size(228, 32);
            _centreBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _centreBtn.Click += (s, e) => ComputeCentre();

            _goCentreBtn.Location = new Point(1248, 354);
            _goCentreBtn.Size = new Size(228, 32);
            _goCentreBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _goCentreBtn.Click += async (s, e) => await GoToCentreAsync();

            _centreResult.Location = new Point(1248, 392);
            _centreResult.Size = new Size(228, 90);
            _centreResult.Anchor = AnchorStyles.Top | AnchorStyles.Right;

            // ---- Rotate about crosshair (under the centre-find column) -----------
            // Needs the camera-scale calibration + a chuck centre. "Sign test" fixes the
            // image handedness once; "Rotate by/to" then pin the crosshair point while Θ turns.
            var rotLabel = new Label { Text = "Rotate about crosshair", Location = new Point(1248, 490), AutoSize = true, Font = new Font("Segoe UI", 10F, FontStyle.Bold), Anchor = AnchorStyles.Top | AnchorStyles.Right };

            _rotBy.Location = new Point(1248, 516);
            _rotBy.Size = new Size(100, 24);
            _rotBy.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _rotByBtn.Location = new Point(1352, 514);
            _rotByBtn.Size = new Size(124, 28);
            _rotByBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _rotByBtn.Click += async (s, e) => await _owner!.RotateAboutCrosshairAsync((double)_rotBy.Value);

            _rotTo.Location = new Point(1248, 546);
            _rotTo.Size = new Size(100, 24);
            _rotTo.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _rotToBtn.Location = new Point(1352, 544);
            _rotToBtn.Size = new Size(124, 28);
            _rotToBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _rotToBtn.Click += async (s, e) => await _owner!.RotateToAngleAsync((double)_rotTo.Value);

            _signLabel.Location = new Point(1248, 580);
            _signLabel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _signTestBtn.Location = new Point(1352, 576);
            _signTestBtn.Size = new Size(124, 28);
            _signTestBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _signTestBtn.Click += async (s, e) => await SignTestAsync();

            // Hold-to-rotate: MouseDown starts the compensated rotation in that direction, MouseUp
            // (or focus loss) stops it. CCW = Θ direction −1, CW = +1 (swap visually if reversed).
            _rotHoldCcwBtn.Location = new Point(1248, 608);
            _rotHoldCcwBtn.Size = new Size(112, 30);
            _rotHoldCcwBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _rotHoldCcwBtn.MouseDown += (s, e) => { _ = _owner!.HoldRotateAsync(-1); };
            _rotHoldCcwBtn.MouseUp += (s, e) => _owner!.StopHoldRotate();

            _rotHoldCwBtn.Location = new Point(1364, 608);
            _rotHoldCwBtn.Size = new Size(112, 30);
            _rotHoldCwBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _rotHoldCwBtn.MouseDown += (s, e) => { _ = _owner!.HoldRotateAsync(+1); };
            _rotHoldCwBtn.MouseUp += (s, e) => _owner!.StopHoldRotate();

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
            Controls.Add(vLabel);
            Controls.Add(vSpeedLabel);
            Controls.Add(_vSpeed);
            Controls.Add(_vUp);
            Controls.Add(_vDown);
            Controls.Add(_vLeft);
            Controls.Add(_vRight);
            Controls.Add(centreLabel);
            Controls.Add(_edgeBtn);
            Controls.Add(_edgeClearBtn);
            Controls.Add(_edgeList);
            Controls.Add(_centreBtn);
            Controls.Add(_goCentreBtn);
            Controls.Add(_centreResult);
            Controls.Add(rotLabel);
            Controls.Add(_rotBy);
            Controls.Add(_rotByBtn);
            Controls.Add(_rotTo);
            Controls.Add(_rotToBtn);
            Controls.Add(_signLabel);
            Controls.Add(_signTestBtn);
            Controls.Add(_rotHoldCcwBtn);
            Controls.Add(_rotHoldCwBtn);
            RefreshSignLabel();

            // Pick up a previously-saved chuck centre so Go to Centre works across restarts.
            if (_owner?.Calibration.ChuckCenterX is long cxLoaded && _owner.Calibration.ChuckCenterY is long cyLoaded)
            {
                _chuckCentre = (cxLoaded, cyLoaded);
                _goCentreBtn.Enabled = true;
                _centreResult.Text = $"Saved centre:\r\nX={cxLoaded}  Y={cyLoaded}";
            }

            Load += (s, e) => StartCamera();
            FormClosing += (s, e) => Teardown();
            // Safety: if this window loses focus mid-hold (e.g. alt-tab), stop the vision jog AND
            // any hold-rotate (a held button's MouseUp may not fire when focus is stolen).
            Deactivate += (s, e) => { _owner?.VisionStop(); _owner?.StopHoldRotate(); };
        }

        private void StartCamera()
        {
            try { _camera.Open(); }
            catch (HOperatorException ex) { _status.Text = "Camera open failed: " + ex.Message; return; }

            _viewW = Math.Max(1, _liveBox.Width);
            _viewH = Math.Max(1, _liveBox.Height);
            _captureBtn.Enabled = true;
            _sampleBtn.Enabled = true;
            _edgeBtn.Enabled = true;
            _vSpeed.Enabled = true;
            _vUp.Enabled = _vDown.Enabled = _vLeft.Enabled = _vRight.Enabled = true;
            _rotBy.Enabled = _rotByBtn.Enabled = true;
            _rotTo.Enabled = _rotToBtn.Enabled = _signTestBtn.Enabled = true;
            _rotHoldCcwBtn.Enabled = _rotHoldCwBtn.Enabled = true;
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

                // Centre-find edge: detect the chuck edge nearest the crosshair (raw frame
                // centre), on the raw frame. Crosshair = frame centre in raw pixels.
                if (_edgeRequested)
                {
                    _edgeRequested = false;
                    HOperatorSet.GetImageSize(frame, out HTuple fw, out HTuple fh);
                    double crossRow = fh.D / 2.0, crossCol = fw.D / 2.0;
                    bool found;
                    ChuckEdgeDetector.EdgePoint edge;
                    try { found = _edgeDetector.TryDetect(frame, crossRow, crossCol, out edge); }
                    catch (HOperatorException) { found = false; edge = default; }

                    Bitmap? raw = null;
                    try { raw = HalconBitmap.ToBitmap(frame, 0, 0); }
                    catch (HOperatorException) { raw = null; }
                    if (raw != null)
                    {
                        bool f = found; ChuckEdgeDetector.EdgePoint ed = edge; Bitmap r = raw;
                        double cr = crossRow, cc = crossCol;
                        try { BeginInvoke(new Action(() => OnEdgeGrabbed(f, ed, cr, cc, r))); }
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

        // ---- Chuck centre-find --------------------------------------------------------

        private void RequestEdge()
        {
            if (!_camera.IsOpen) return;
            _edgeRequested = true;
            _status.Text = "Detecting chuck edge...";
        }

        // UI thread: GrabLoop found (or not) the chuck-edge point nearest the crosshair. Convert
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

            double dRow = crossRow - edge.Row, dCol = crossCol - edge.Column;
            double ex = mx + a.Xr * dRow + a.Xc * dCol;
            double ey = my + a.Yr * dRow + a.Yc * dCol;
            _edgePoints.Add((ex, ey));

            DrawEdgeOverlay(raw, edge, crossRow, crossCol);
            ShowCaptured(raw);
            RefreshEdgeUi();
            _status.Text = $"Edge {_edgePoints.Count}: px=(r {edge.Row:F0}, c {edge.Column:F0}) → step=({ex:F0}, {ey:F0})";
        }

        // Draws the frame-centre crosshair (green) and the detected edge point (yellow circle).
        private static void DrawEdgeOverlay(Bitmap bmp, ChuckEdgeDetector.EdgePoint edge, double crossRow, double crossCol)
        {
            using var g = Graphics.FromImage(bmp);
            float w = Math.Max(2f, bmp.Width / 400f), half = bmp.Width / 30f, rad = bmp.Width / 60f;
            using (var cpen = new Pen(Color.Lime, w))
            {
                g.DrawLine(cpen, (float)crossCol, (float)crossRow - half, (float)crossCol, (float)crossRow + half);
                g.DrawLine(cpen, (float)crossCol - half, (float)crossRow, (float)crossCol + half, (float)crossRow);
            }
            using var epen = new Pen(Color.Yellow, w);
            g.DrawEllipse(epen, (float)edge.Column - rad, (float)edge.Row - rad, 2 * rad, 2 * rad);
        }

        private void ClearEdges()
        {
            _edgePoints.Clear();
            RefreshEdgeUi();
            _status.Text = "Edge points cleared.";
        }

        private void RefreshEdgeUi()
        {
            var sb = new StringBuilder();
            int i = 1;
            foreach ((double X, double Y) p in _edgePoints)
                sb.AppendLine($"{i++,2}: X={p.X,9:F0} Y={p.Y,9:F0}");
            _edgeList.Text = sb.ToString();
            _centreBtn.Enabled = _edgePoints.Count >= 3;
            _edgeClearBtn.Enabled = _edgePoints.Count > 0;
        }

        // Circle-fits the edge points (step space); the centre is the motor position that puts
        // the chuck centre on the crosshair. Persists it to the shared store.
        private void ComputeCentre()
        {
            if (!CircleFit.TryFit(_edgePoints, out CircleFit.Result fit, out string? err))
            {
                _centreResult.Text = "Fit failed:\r\n" + err;
                return;
            }
            long cx = (long)Math.Round(fit.CenterX), cy = (long)Math.Round(fit.CenterY);
            _chuckCentre = (cx, cy);
            if (_owner != null)
            {
                _owner.Calibration.ChuckCenterX = cx;
                _owner.Calibration.ChuckCenterY = cy;
                try { _owner.Calibration.Save(); }
                catch (Exception ex) { _centreResult.Text = $"Computed but SAVE failed:\r\n{ex.Message}"; return; }
            }
            _goCentreBtn.Enabled = true;
            _centreResult.Text =
                $"Centre (N={_edgePoints.Count}):\r\nX={cx}  Y={cy}\r\n" +
                $"R={fit.Radius:F0}  fit RMS={fit.RmsError:F0} steps";
            _status.Text = "Chuck centre computed and saved.";
        }

        // Drives the table so the chuck centre lands on the view centre. Absolute move via
        // FrmMain.MoveToAsync (bounds-checked, Y-frame handled). Confirms first — auto move.
        private async Task GoToCentreAsync()
        {
            if (_owner == null || _chuckCentre == null) return;
            long cx = _chuckCentre.Value.X, cy = _chuckCentre.Value.Y;
            DialogResult ans = MessageBox.Show(this,
                $"Move the chuck centre to the view centre?\r\nTarget: X={cx}, Y={cy}  (Z unchanged).",
                "Go to Centre", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (ans != DialogResult.Yes) return;
            _status.Text = "Moving to chuck centre...";
            await _owner.MoveToAsync(cx.ToString(), cy.ToString(), "");
            _status.Text = "Go to centre: move issued (see main-window log).";
        }

        private void RefreshSignLabel()
            => _signLabel.Text = _owner?.RotationSign is int s ? $"Handedness: {s:+0;-0}" : "Handedness: not set";

        // Fixes the image handedness empirically: rotate a small angle about the crosshair with
        // the current assumed sign, ask whether the crosshair point stayed pinned, then rotate
        // back (same sign → exact restore) and persist the confirmed/flipped sign. If the point
        // swung away instead of staying put, the sign was wrong and gets flipped.
        private async Task SignTestAsync()
        {
            if (_owner == null) return;
            const double testDeg = 6.0;
            if (MessageBox.Show(this,
                    $"Sign test rotates ~{testDeg}° about the crosshair and back.\r\n" +
                    "Watch the point under the crosshair.\r\nProceed?",
                    "Sign test", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            int assumed = _owner.RotationSign ?? +1;   // the sign used for BOTH the test and the restore
            await _owner.RotateAboutCrosshairAsync(+testDeg);

            DialogResult pinned = MessageBox.Show(this,
                "Did the point under the crosshair STAY pinned?\r\n\r\n" +
                "Yes = handedness correct\r\nNo = it swung away (will flip)",
                "Sign test", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);

            // Reversing with the SAME stored sign is the exact geometric inverse, so this returns
            // to the start pose regardless of the answer. Do it BEFORE changing the sign.
            await _owner.RotateAboutCrosshairAsync(-testDeg);

            if (pinned == DialogResult.Cancel) { _status.Text = "Sign test cancelled (no change)."; return; }
            int chosen = pinned == DialogResult.Yes ? assumed : -assumed;
            _owner.SetRotationSign(chosen);
            RefreshSignLabel();
            _status.Text = $"Sign test done: handedness {chosen:+0;-0}.";
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

        // Drift-corrected jog: convert a desired SCREEN direction (sx: right+, sy: up+) into a
        // motor command via the calibration so the on-screen motion is pure along that axis.
        // Screen->raw-pixel mapping accounts for the 180° display Invert; the motor vector is
        // A·(Δrow,Δcol), scaled so the larger component equals the chosen speed.
        private void VisionJog(int sx, int sy)
        {
            PixelStepAffine? a = _owner?.Calibration.PixelStep;
            if (a == null) { _status.Text = "Vision jog needs the camera-scale calibration first."; return; }

            // Controls are tied to the camera's NATIVE (raw) orientation — the frame the
            // calibration was measured in: screen right = +col, screen up = -row. The Invert
            // toggle is a DISPLAY-only flip and deliberately does NOT change control direction.
            double dCol = sx;
            double dRow = -sy;

            // User-frame motor velocity that produces that pixel motion (A = steps per pixel).
            double vxUser = a.Xr * dRow + a.Xc * dCol;
            double vyUser = a.Yr * dRow + a.Yc * dCol;

            double m = Math.Max(Math.Abs(vxUser), Math.Abs(vyUser));
            if (m < 1e-9) { _status.Text = "Calibration is degenerate; recalibrate."; return; }

            double speed = (double)_vSpeed.Value;
            int vx = (int)Math.Round(vxUser / m * speed);
            int vy = (int)Math.Round(vyUser / m * speed);
            _owner!.VisionJogUser(vx, vy);
            _status.Text = $"Vision jog: user-vel ({vx}, {vy}).";
        }

        private void Teardown()
        {
            _owner?.VisionStop();
            _cts?.Cancel();
            try { _grabTask?.Wait(500); } catch { /* best effort; camera streams so grab returns fast */ }
            _camera.Dispose();
            lock (_frameLock) { _pending?.Dispose(); _pending = null; }
            _liveBox.Image?.Dispose();
            _capturedBox.Image?.Dispose();
        }
    }
}
