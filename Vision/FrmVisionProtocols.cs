using System;
using System.Drawing;
using System.Windows.Forms;
using HalconDotNet;

namespace NanotecController
{
    /// <summary>
    /// Vision protocols window: the camera-scale calibration, chuck/wafer centre-find, and the
    /// rotate-about-crosshair setup (Rotate by/to + the one-time handedness Sign test), plus a
    /// captured pane where each detection's overlay lands. It does NOT own the camera — the live
    /// view lives on the main screen; this window is handed the shared <see cref="IVisionFrameSource"/>
    /// (from FrmMain) and enqueues its detection jobs there. All motion is executed/serialized by
    /// <see cref="IMotionHost"/> (FrmMain); this window only reads position and requests moves.
    ///
    /// The interactive jog + hold-to-rotate also live in the main motion cluster (RAW/VISION mode
    /// switch); this window keeps a convenience copy so the operator can nudge the stage while
    /// watching the mirror during calibration. Because those are hold-to-move controls, this window
    /// installs a focus-loss safety stop (Deactivate → VisionStop + StopHoldRotate).
    /// </summary>
    public sealed partial class FrmVisionProtocols : Form
    {
        // The shared live camera (owned by FrmMain). Detection jobs run against its RAW frames;
        // results are marshalled back and overlaid on our captured pane.
        private readonly IVisionFrameSource _view;
        private readonly PictureBox _capturedBox = new() { SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black };
        private readonly Label _status = new() { Text = "Ready.", AutoSize = true };

        // Live-view MIRROR of the main-screen camera (this window owns no camera): the primary
        // control pushes frames here via IVisionFrameSource.FrameDisplayed, so the operator can align
        // a feature to the crosshair without switching to the main window. The crosshair toggle is
        // local to this view; zoom drives the shared camera (so it also changes the main view).
        private readonly VisionViewControl _live = new() { OwnsCamera = false };
        private readonly Button _crosshairBtn = new() { Text = "Crosshair" };
        private readonly ComboBox _zoomBox = new() { DropDownStyle = ComboBoxStyle.DropDownList };

        // Camera-scale calibration (manual jog + capture; owner supplies motor position)
        private readonly IMotionHost? _owner;
        private readonly SolidCircleDetector _markDetector = new();
        private readonly CameraCalibrator _calibrator = new();
        private readonly Button _sampleBtn = new() { Text = "Add Sample", Enabled = false };
        private readonly Button _computeBtn = new() { Text = "Compute && Save A", Enabled = false };
        private readonly Button _clearBtn = new() { Text = "Clear", Enabled = false };
        private readonly TextBox _sampleList = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 8F), BackColor = Color.White };
        private readonly Label _calibResult = new() { BorderStyle = BorderStyle.FixedSingle, Font = new Font("Consolas", 8F), TextAlign = ContentAlignment.TopLeft };

        // --- Wafer centre-find: capture >=3 wafer-rim points (step space), circle-fit, go to centre.
        // Same flow as the chuck centre-find but with WaferEdgeDetector and a separate stored centre.
        private readonly WaferEdgeDetector _waferDetector = new();
        private readonly CentreFinder _waferFinder = new();   // accumulates wafer rim points (user-frame steps)
        private readonly Button _waferEdgeBtn = new() { Text = "Add Wafer Edge", Enabled = false };
        private readonly Button _waferClearBtn = new() { Text = "Clear", Enabled = false };
        private readonly Button _waferCentreBtn = new() { Text = "Compute Centre", Enabled = false };
        private readonly Button _waferGoBtn = new() { Text = "Go to Centre", Enabled = false };
        private readonly Label _waferResult = new() { BorderStyle = BorderStyle.FixedSingle, Font = new Font("Consolas", 8F), TextAlign = ContentAlignment.TopLeft };
        private (long X, long Y)? _waferCentre;

        // --- Chuck centre-find: capture >=3 edge points (step space), circle-fit, go to centre --
        private readonly ChuckEdgeDetector _edgeDetector = new();
        private readonly CentreFinder _chuckFinder = new();   // accumulates chuck edge points (user-frame steps)
        private readonly Button _edgeBtn = new() { Text = "Add Edge", Enabled = false };
        private readonly Button _edgeClearBtn = new() { Text = "Clear Edges", Enabled = false };
        private readonly Button _centreBtn = new() { Text = "Compute Centre", Enabled = false };
        private readonly Button _goCentreBtn = new() { Text = "Go to Centre", Enabled = false };
        private readonly TextBox _edgeList = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 8F), BackColor = Color.White };
        private readonly Label _centreResult = new() { BorderStyle = BorderStyle.FixedSingle, Font = new Font("Consolas", 8F), TextAlign = ContentAlignment.TopLeft };
        private (long X, long Y)? _chuckCentre;   // last computed/loaded centre (user frame)

        // --- Rotate about crosshair (setup): relative ("Rotate by"), absolute ("Rotate to"), and the
        // one-time handedness "Sign test". All gated/executed by FrmMain (serialized motion). The
        // interactive hold-to-rotate lives in the main motion cluster's VISION mode now.
        private readonly NumericUpDown _rotBy = new() { Minimum = -360, Maximum = 360, Value = 90, DecimalPlaces = 1, Increment = 5, Enabled = false };
        private readonly Button _rotByBtn = new() { Text = "Rotate by°", Enabled = false };
        private readonly NumericUpDown _rotTo = new() { Minimum = 0, Maximum = 360, Value = 0, DecimalPlaces = 1, Increment = 5, Enabled = false };
        private readonly Button _rotToBtn = new() { Text = "Rotate to°", Enabled = false };
        private readonly Label _signLabel = new() { AutoSize = true };
        private readonly Button _signTestBtn = new() { Text = "Sign test", Enabled = false };

        // --- Vision motion controls (a convenience copy of the main-screen VISION mode, so the
        // operator can jog / rotate while looking at this window during calibration): the drift-
        // corrected X/Y jog (puck + d-pad) and hold-to-rotate about the crosshair. Everything runs
        // through IMotionHost (FrmMain serializes all motion). Rotate SPEED is set on the main window
        // (VISION-mode Θ slider → RotateThetaSpeed); this window just triggers the moves. ---
        private readonly NumericUpDown _vSpeed = new() { Minimum = 0, Maximum = 6000, Value = 1000, Increment = 100, Enabled = false };
        private readonly JoystickPad _vPad = new() { Enabled = false };
        private readonly System.Windows.Forms.Timer _vPadTimer = new() { Interval = 50 };
        private int _vLastVx, _vLastVy;
        private readonly Button _vUp = new() { Text = "▲", Font = new Font("Segoe UI Symbol", 11F), Enabled = false };
        private readonly Button _vDown = new() { Text = "▼", Font = new Font("Segoe UI Symbol", 11F), Enabled = false };
        private readonly Button _vLeft = new() { Text = "◀", Font = new Font("Segoe UI Symbol", 11F), Enabled = false };
        private readonly Button _vRight = new() { Text = "▶", Font = new Font("Segoe UI Symbol", 11F), Enabled = false };
        private readonly Button _rotHoldCcwBtn = new() { Text = "⟲ Hold", Font = new Font("Segoe UI Symbol", 11F), Enabled = false };
        private readonly Button _rotHoldCwBtn = new() { Text = "Hold ⟳", Font = new Font("Segoe UI Symbol", 11F), Enabled = false };

        public FrmVisionProtocols(IMotionHost owner, IVisionFrameSource view)
        {
            _owner = owner;
            _view = view;
            Text = "Vision - calibration & centre-find";
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9F);
            ClientSize = new Size(1490, 680);
            MinimumSize = new Size(1200, 720);

            // ---- Live view (mirror) + captured pane (top row) --------------------
            // Left: a live MIRROR of the main-screen camera (crosshair toggle + shared zoom) so the
            // operator can align features here. Right: the captured pane showing each detection's
            // overlay (crosshair + edge/mark).
            var liveLabel = new Label { Text = "Live", Location = new Point(12, 8), AutoSize = true, Font = new Font("Segoe UI", 10F, FontStyle.Bold) };

            _crosshairBtn.Location = new Point(58, 6);
            _crosshairBtn.Size = new Size(108, 24);
            _crosshairBtn.Click += (s, e) =>
            {
                _live.ShowCrosshair = !_live.ShowCrosshair;
                SetToggle(_crosshairBtn, _live.ShowCrosshair);
            };

            var zoomLabel = new Label { Text = "Zoom:", Location = new Point(178, 10), AutoSize = true };
            _zoomBox.Location = new Point(226, 6);
            _zoomBox.Size = new Size(56, 24);
            foreach (int z in VisionViewControl.ZoomFactors) _zoomBox.Items.Add($"{z}x");
            _zoomBox.SelectedIndex = Math.Max(0, Array.IndexOf(VisionViewControl.ZoomFactors, _view.ZoomFactor));
            _zoomBox.SelectedIndexChanged += (s, e) =>
            {
                if (_zoomBox.SelectedIndex >= 0) _view.ZoomFactor = VisionViewControl.ZoomFactors[_zoomBox.SelectedIndex];
            };

            _live.Location = new Point(12, 32);
            _live.Size = new Size(480, 440);
            _live.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left;
            _live.ShowCrosshair = true;   // on by default here — the point of this view is alignment
            SetToggle(_crosshairBtn, _live.ShowCrosshair);
            _live.TickScaleProvider = () => VisionViewControl.MmPerPixel(_owner!.Calibration);   // 1 mm marks here too

            var capLabel = new Label { Text = "Captured (detection overlay)", Location = new Point(500, 8), AutoSize = true, Font = new Font("Segoe UI", 10F, FontStyle.Bold) };
            _capturedBox.Location = new Point(500, 32);
            _capturedBox.Size = new Size(488, 440);
            _capturedBox.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left;

            _status.Location = new Point(500, 484);
            _status.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;

            // ---- Wafer centre-find (bottom strip) --------------------------------
            // Jog so the wafer EDGE crosses the crosshair (main window, VISION mode), Add Wafer Edge at
            // several spots around the rim (detects via WaferEdgeDetector, converts to step space), then
            // Compute Centre circle-fits them. Mirrors the chuck centre-find; result overlays on the pane.
            var waferLabel = new Label { Text = "Wafer centre-find", Location = new Point(12, 528), AutoSize = true, Font = new Font("Segoe UI", 9F, FontStyle.Bold), Anchor = AnchorStyles.Bottom | AnchorStyles.Left };

            _waferEdgeBtn.Location = new Point(12, 550);
            _waferEdgeBtn.Size = new Size(128, 30);
            _waferEdgeBtn.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _waferEdgeBtn.Click += (s, e) =>
            {
                if (!_view.IsCameraOpen) return;
                _view.RequestFrame(frame =>
                {
                    HOperatorSet.GetImageSize(frame, out HTuple fw, out HTuple fh);
                    double crossRow = fh.D / 2.0, crossCol = fw.D / 2.0;
                    bool found;
                    WaferEdgeDetector.EdgePoint edge;
                    double[] cRows = Array.Empty<double>(), cCols = Array.Empty<double>();
                    try
                    {
                        found = _waferDetector.TryDetect(frame, crossRow, crossCol, out edge, out HObject? contour);
                        if (found && contour != null)
                        {
                            try { HOperatorSet.GetContourXld(contour, out HTuple rows, out HTuple cols); cRows = rows.ToDArr(); cCols = cols.ToDArr(); }
                            catch (HOperatorException) { /* keep the point even if the contour read fails */ }
                        }
                        contour?.Dispose();
                    }
                    catch (HOperatorException) { found = false; edge = default; }
                    _view.PostFrameBitmap(frame, flip: false, raw => OnWaferGrabbed(found, edge, cRows, cCols, crossRow, crossCol, raw));
                });
                _status.Text = "Detecting wafer edge...";
            };

            _waferClearBtn.Location = new Point(146, 550);
            _waferClearBtn.Size = new Size(56, 30);
            _waferClearBtn.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _waferClearBtn.Click += (s, e) => ClearWaferPoints();

            _waferCentreBtn.Location = new Point(208, 550);
            _waferCentreBtn.Size = new Size(128, 30);
            _waferCentreBtn.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _waferCentreBtn.Click += (s, e) => ComputeWaferCentre();

            _waferGoBtn.Location = new Point(342, 550);
            _waferGoBtn.Size = new Size(108, 30);
            _waferGoBtn.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _waferGoBtn.Click += async (s, e) => await GoToWaferCentreAsync();

            _waferResult.Location = new Point(458, 528);
            _waferResult.Size = new Size(232, 52);
            _waferResult.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;

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

            // ---- Chuck centre-find column (far right) ----------------------------
            // Jog so the chuck EDGE is in view (main window), Add Edge (detects the edge point nearest
            // the crosshair, converts to step space via the calibration). After >=3 around the rim,
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

            // ---- Rotate about crosshair — setup (under the centre-find column) ---
            // Needs the camera-scale calibration + a chuck centre. "Sign test" fixes the image
            // handedness once; "Rotate by/to" pin the crosshair point while Θ turns. Interactive
            // hold-to-rotate is on the main window (VISION mode, Θ arrows).
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

            // ---- Drift-corrected vision jog (under the calibration column) -------
            // The puck / d-pad command screen-space motion (right = +col, up = −row) mapped through
            // the pixel→step affine, so the feature under the crosshair tracks the input regardless of
            // the table's orientation. Speed here scales the puck; the d-pad uses it directly. Needs
            // the camera-scale calibration (the maths reports via _status if it's missing/degenerate).
            var vLabel = new Label { Text = "Vision jog (drift-corrected)", Location = new Point(1000, 490), AutoSize = true, Font = new Font("Segoe UI", 10F, FontStyle.Bold), Anchor = AnchorStyles.Top | AnchorStyles.Right };
            var vSpeedLabel = new Label { Text = "Speed:", Location = new Point(1000, 518), AutoSize = true, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            _vSpeed.Location = new Point(1052, 515);
            _vSpeed.Size = new Size(80, 24);
            _vSpeed.Anchor = AnchorStyles.Top | AnchorStyles.Right;

            _vPad.Location = new Point(1000, 544);
            _vPad.Size = new Size(112, 112);
            _vPad.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _vPadTimer.Tick += (s, e) => VisionPadTick();

            void VisionPadBtn(Button b, int x, int y, int sx, int sy)
            {
                b.Location = new Point(x, y);
                b.Size = new Size(36, 32);
                b.Anchor = AnchorStyles.Top | AnchorStyles.Right;
                b.MouseDown += (s, e) => VisionJog(sx, sy);
                b.MouseUp += (s, e) => _owner?.VisionStop();
            }

            static void SetToggle(Button b, bool active)
            {
                if (active) b.BackColor = Color.LightGreen;
                else { b.UseVisualStyleBackColor = true; b.BackColor = SystemColors.Control; }
            }
            VisionPadBtn(_vUp, 1160, 548, 0, +1);
            VisionPadBtn(_vLeft, 1124, 584, -1, 0);
            VisionPadBtn(_vRight, 1196, 584, +1, 0);
            VisionPadBtn(_vDown, 1160, 620, 0, -1);

            // Hold-to-rotate about the crosshair (same as the main window's VISION Θ arrows). MouseDown
            // starts a continuous turn at RotateThetaSpeed (set on the main window); MouseUp — or losing
            // focus (Deactivate) — stops it.
            _rotHoldCcwBtn.Location = new Point(1248, 612);
            _rotHoldCcwBtn.Size = new Size(112, 30);
            _rotHoldCcwBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _rotHoldCcwBtn.MouseDown += (s, e) => { _ = _owner!.HoldRotateAsync(-1); };
            _rotHoldCcwBtn.MouseUp += (s, e) => _owner!.StopHoldRotate();
            _rotHoldCwBtn.Location = new Point(1364, 612);
            _rotHoldCwBtn.Size = new Size(112, 30);
            _rotHoldCwBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _rotHoldCwBtn.MouseDown += (s, e) => { _ = _owner!.HoldRotateAsync(+1); };
            _rotHoldCwBtn.MouseUp += (s, e) => _owner!.StopHoldRotate();

            Controls.Add(liveLabel);
            Controls.Add(_crosshairBtn);
            Controls.Add(zoomLabel);
            Controls.Add(_zoomBox);
            Controls.Add(_live);
            Controls.Add(capLabel);
            Controls.Add(_capturedBox);
            Controls.Add(_status);
            Controls.Add(calibLabel);
            Controls.Add(_sampleBtn);
            Controls.Add(_clearBtn);
            Controls.Add(_sampleList);
            Controls.Add(_computeBtn);
            Controls.Add(_calibResult);
            Controls.Add(waferLabel);
            Controls.Add(_waferEdgeBtn);
            Controls.Add(_waferClearBtn);
            Controls.Add(_waferCentreBtn);
            Controls.Add(_waferGoBtn);
            Controls.Add(_waferResult);
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
            Controls.Add(vLabel);
            Controls.Add(vSpeedLabel);
            Controls.Add(_vSpeed);
            Controls.Add(_vPad);
            Controls.Add(_vUp);
            Controls.Add(_vDown);
            Controls.Add(_vLeft);
            Controls.Add(_vRight);
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
            // Likewise the saved wafer centre.
            if (_owner?.Calibration.WaferCenterX is long wxLoaded && _owner.Calibration.WaferCenterY is long wyLoaded)
            {
                _waferCentre = (wxLoaded, wyLoaded);
                _waferGoBtn.Enabled = true;
                _waferResult.Text = $"Saved wafer centre:\r\nX={wxLoaded}  Y={wyLoaded}";
            }

            // The camera is already streaming on the main screen; gate on its live state and
            // follow open/close (e.g. a Retry on the main toolbar) while this window is open.
            _view.CameraStateChanged += OnCameraStateChanged;
            OnCameraStateChanged();
            _view.FrameDisplayed += _live.PushFrame;   // mirror the main-screen live feed into _live

            // Safety: the vision jog / hold-rotate are hold-to-move. If this window loses focus mid-hold
            // (alt-tab, a dialog) the MouseUp may never arrive, so stop all continuous motion on deactivate.
            Deactivate += (s, e) => { _owner?.VisionStop(); _owner?.StopHoldRotate(); };

            FormClosing += (s, e) => Teardown();
        }

        // Camera opened or closed on the main screen: gate the protocol actions that need live frames.
        private void OnCameraStateChanged()
        {
            if (IsDisposed) return;
            bool open = _view.IsCameraOpen;
            _sampleBtn.Enabled = open;
            _edgeBtn.Enabled = open;
            _waferEdgeBtn.Enabled = open;
            _rotBy.Enabled = _rotByBtn.Enabled = open;
            _rotTo.Enabled = _rotToBtn.Enabled = _signTestBtn.Enabled = open;

            // Vision motion controls: live only while the camera streams. The puck poll runs only then.
            _vSpeed.Enabled = _vPad.Enabled = open;
            _vUp.Enabled = _vDown.Enabled = _vLeft.Enabled = _vRight.Enabled = open;
            _rotHoldCcwBtn.Enabled = _rotHoldCwBtn.Enabled = open;
            if (open) _vPadTimer.Start(); else { _vPadTimer.Stop(); _owner?.VisionStop(); }
        }

        // Puts a bitmap in the captured pane, taking ownership (disposes the previous one).
        private void ShowCaptured(Bitmap bmp)
        {
            if (IsDisposed) { bmp.Dispose(); return; }
            Image? old = _capturedBox.Image;
            _capturedBox.Image = bmp;
            if (!ReferenceEquals(old, bmp)) old?.Dispose();
        }

        // Camera-scale calibration → FrmVisionProtocols.Calibration.cs
        // Chuck + wafer centre-find → FrmVisionProtocols.CentreFind.cs
        // Rotate-about-crosshair UI (sign label + sign test) → FrmVisionProtocols.Rotation.cs

        private void Teardown()
        {
            _vPadTimer.Stop();
            _owner?.VisionStop();
            _owner?.StopHoldRotate();
            _view.CameraStateChanged -= OnCameraStateChanged;
            _view.FrameDisplayed -= _live.PushFrame;
            _capturedBox.Image?.Dispose();
        }
    }
}
