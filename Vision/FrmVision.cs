using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using HalconDotNet;

namespace NanotecController
{
    /// <summary>
    /// Vision window: hosts the live view (<see cref="VisionViewControl"/>, which owns the
    /// camera and grab thread) plus the capture pane and the calibration / centre-find /
    /// rotate-about-crosshair protocol columns. This form is the toolbar + protocol surface;
    /// all frame plumbing lives in the control, reached via RequestFrame / PostFrameBitmap.
    /// </summary>
    public sealed partial class FrmVision : Form
    {
        private readonly VisionViewControl _view = new();
        private readonly PictureBox _capturedBox = new() { SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black };
        private readonly Button _captureBtn = new() { Text = "Capture Image", Enabled = false };
        private readonly Button _saveBtn = new() { Text = "Save Image", Enabled = false };
        private readonly Button _crosshairBtn = new() { Text = "Crosshair: Off" };
        private readonly Button _invertBtn = new() { Text = "Invert: On" };
        private readonly Button _monoBtn = new() { Text = "Mono: Off" };
        private readonly Label _status = new() { Text = "Opening camera...", AutoSize = true };
        private readonly Label _fpsLabel = new() { AutoSize = true, ForeColor = Color.DimGray };
        private readonly ComboBox _zoomBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Enabled = false };

        // Camera-scale calibration (manual jog + capture; owner supplies motor position)
        private readonly IMotionHost? _owner;
        private readonly SolidCircleDetector _markDetector = new();
        private readonly CameraCalibrator _calibrator = new();
        private readonly Button _sampleBtn = new() { Text = "Add Sample", Enabled = false };
        private readonly Button _computeBtn = new() { Text = "Compute && Save A", Enabled = false };
        private readonly Button _clearBtn = new() { Text = "Clear", Enabled = false };
        private readonly TextBox _sampleList = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 8F), BackColor = Color.White };
        private readonly Label _calibResult = new() { BorderStyle = BorderStyle.FixedSingle, Font = new Font("Consolas", 8F), TextAlign = ContentAlignment.TopLeft };

        // Drift-corrected "vision jog": commands X+Y together (via the calibration) so the
        // on-screen motion is purely horizontal/vertical. Hold-to-jog like the main d-pad.
        private readonly NumericUpDown _vSpeed = new() { Minimum = 0, Maximum = 6000, Value = 1000, Increment = 100, Enabled = false };
        // Analog joystick for the drift-corrected jog: drag the puck (angle + distance) → the
        // owner is commanded the drift-corrected X/Y velocity scaled by the deflection. Polled on
        // a timer (the pad only reports state), send-on-change so an idle/centred puck is silent.
        private readonly JoystickPad _vPad = new() { Enabled = false };
        private readonly System.Windows.Forms.Timer _vPadTimer = new() { Interval = 50 };
        private int _vLastVx, _vLastVy;
        // Discrete d-pad alongside the puck (pure-axis jog at the full Speed setting).
        private readonly Button _vUp = new() { Text = "▲", Font = new Font("Segoe UI Symbol", 11F), Enabled = false };
        private readonly Button _vDown = new() { Text = "▼", Font = new Font("Segoe UI Symbol", 11F), Enabled = false };
        private readonly Button _vLeft = new() { Text = "◀", Font = new Font("Segoe UI Symbol", 11F), Enabled = false };
        private readonly Button _vRight = new() { Text = "▶", Font = new Font("Segoe UI Symbol", 11F), Enabled = false };

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
        private readonly TrackBar _rotSpeedSlider = new() { Minimum = 50, Maximum = 2000, Value = 800, TickFrequency = 50, Enabled = false };
        private readonly Label _rotSpeedLabel = new() { Text = "Speed: 800", AutoSize = true, Anchor = AnchorStyles.Top | AnchorStyles.Right };

        public FrmVision(IMotionHost owner)
        {
            _owner = owner;
            Text = "Vision - live view";
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9F);
            ClientSize = new Size(1490, 640);
            MinimumSize = new Size(1200, 680);

            var liveLabel = new Label { Text = "Live", Location = new Point(12, 8), AutoSize = true, Font = new Font("Segoe UI", 10F, FontStyle.Bold) };
            _fpsLabel.Location = new Point(52, 11);

            var zoomLabel = new Label { Text = "Zoom:", Location = new Point(352, 10), AutoSize = true };
            _zoomBox.Location = new Point(410, 4);
            _zoomBox.Size = new Size(56, 24);
            foreach (int z in VisionViewControl.ZoomFactors) _zoomBox.Items.Add($"{z}x");
            _zoomBox.SelectedIndex = Array.IndexOf(VisionViewControl.ZoomFactors, _view.ZoomFactor);
            _zoomBox.SelectedIndexChanged += (s, e) =>
            {
                if (_zoomBox.SelectedIndex >= 0) _view.ZoomFactor = VisionViewControl.ZoomFactors[_zoomBox.SelectedIndex];
            };
            var capLabel = new Label { Text = "Captured", Location = new Point(508, 8), AutoSize = true, Font = new Font("Segoe UI", 10F, FontStyle.Bold) };

            _view.Location = new Point(12, 32);
            _view.Size = new Size(480, 440);
            _view.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left;
            _view.StatusChanged += text => _status.Text = text;
            _view.FpsChanged += text => _fpsLabel.Text = text;
            _view.CameraStateChanged += OnCameraStateChanged;
            // Lazy so a calibration edit (affine or steps/mm) re-scales the ticks live.
            _view.TickScaleProvider = () => _owner != null ? VisionViewControl.MmPerPixel(_owner.Calibration) : null;

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
                _view.ShowCrosshair = !_view.ShowCrosshair;
                _crosshairBtn.Text = _view.ShowCrosshair ? "Crosshair: On" : "Crosshair: Off";
            };

            _invertBtn.Location = new Point(396, 484);
            _invertBtn.Size = new Size(120, 40);
            _invertBtn.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _invertBtn.Click += (s, e) =>
            {
                _view.InvertView = !_view.InvertView;
                _invertBtn.Text = _view.InvertView ? "Invert: On" : "Invert: Off";
            };

            _monoBtn.Location = new Point(524, 484);
            _monoBtn.Size = new Size(120, 40);
            _monoBtn.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _monoBtn.Click += (s, e) =>
            {
                _view.MonoView = !_view.MonoView;
                _monoBtn.Text = _view.MonoView ? "Mono: On" : "Mono: Off";
            };

            _status.Location = new Point(652, 496);
            _status.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;

            // ---- Wafer centre-find (bottom strip) --------------------------------
            // Jog so the wafer EDGE crosses the crosshair, Add Wafer Edge at several spots around the
            // rim (detects via WaferEdgeDetector, converts to step space), then Compute Centre
            // circle-fits them. Mirrors the chuck centre-find; the result overlays on the captured pane.
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

            // ---- Drift-corrected vision jog (uses the calibration) ---------------
            // Drag the joystick puck: X+Y are driven together so the live view moves along the
            // puck direction, with speed proportional to deflection × the Speed control. Spring-
            // return puck → release = stop. Polled on _vPadTimer (send-on-change).
            var vLabel = new Label { Text = "Vision jog (drift-corrected)", Location = new Point(1000, 470), AutoSize = true, Font = new Font("Segoe UI", 10F, FontStyle.Bold), Anchor = AnchorStyles.Top | AnchorStyles.Right };
            var vSpeedLabel = new Label { Text = "Speed:", Location = new Point(1000, 496), AutoSize = true, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            _vSpeed.Location = new Point(1060, 493);
            _vSpeed.Size = new Size(80, 24);
            _vSpeed.Anchor = AnchorStyles.Top | AnchorStyles.Right;

            _vPad.Location = new Point(1000, 510);
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
            VisionPadBtn(_vUp, 1160, 514, 0, +1);
            VisionPadBtn(_vLeft, 1124, 548, -1, 0);
            VisionPadBtn(_vRight, 1196, 548, +1, 0);
            VisionPadBtn(_vDown, 1160, 582, 0, -1);

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

            // Rotation speed control: slider to adjust ROTATE_THETA_SPEED dynamically.
            var rotSpeedText = new Label { Text = "Rotation speed", Location = new Point(1248, 646), AutoSize = true, Font = new Font("Segoe UI", 9F), Anchor = AnchorStyles.Top | AnchorStyles.Right };
            _rotSpeedSlider.Location = new Point(1248, 668);
            _rotSpeedSlider.Size = new Size(228, 45);
            _rotSpeedSlider.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _rotSpeedSlider.ValueChanged += (s, e) =>
            {
                _rotSpeedLabel.Text = $"Speed: {_rotSpeedSlider.Value}";
                if (_owner != null) _owner.RotateThetaSpeed = _rotSpeedSlider.Value;
            };

            _rotSpeedLabel.Location = new Point(1352, 720);
            _rotSpeedLabel.Anchor = AnchorStyles.Top | AnchorStyles.Right;

            Controls.Add(liveLabel);
            Controls.Add(_fpsLabel);
            Controls.Add(zoomLabel);
            Controls.Add(_zoomBox);
            Controls.Add(capLabel);
            Controls.Add(_view);
            Controls.Add(_capturedBox);
            Controls.Add(_captureBtn);
            Controls.Add(_saveBtn);
            Controls.Add(_crosshairBtn);
            Controls.Add(_invertBtn);
            Controls.Add(_monoBtn);
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
            Controls.Add(_vPad);
            Controls.Add(_vUp);
            Controls.Add(_vDown);
            Controls.Add(_vLeft);
            Controls.Add(_vRight);
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
            Controls.Add(_rotHoldCcwBtn);
            Controls.Add(_rotHoldCwBtn);
            Controls.Add(rotSpeedText);
            Controls.Add(_rotSpeedSlider);
            Controls.Add(_rotSpeedLabel);
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

            Load += (s, e) => _view.StartCamera();
            FormClosing += (s, e) => Teardown();
            // Safety: if this window loses focus mid-hold (e.g. alt-tab), stop the vision jog AND
            // any hold-rotate (a held button's MouseUp may not fire when focus is stolen).
            Deactivate += (s, e) => { _owner?.VisionStop(); _owner?.StopHoldRotate(); };
        }

        // Camera opened or closed: gate everything that needs live frames (or, for the motion
        // features, that only makes sense while the operator can see the live view).
        private void OnCameraStateChanged()
        {
            bool open = _view.IsCameraOpen;
            _captureBtn.Enabled = open;
            _sampleBtn.Enabled = open;
            _edgeBtn.Enabled = open;
            _waferEdgeBtn.Enabled = open;
            _vSpeed.Enabled = open;
            _vPad.Enabled = open;
            _vUp.Enabled = _vDown.Enabled = _vLeft.Enabled = _vRight.Enabled = open;
            _rotBy.Enabled = _rotByBtn.Enabled = open;
            _rotTo.Enabled = _rotToBtn.Enabled = _signTestBtn.Enabled = open;
            _rotHoldCcwBtn.Enabled = _rotHoldCwBtn.Enabled = open;
            _rotSpeedSlider.Enabled = open;
            _zoomBox.Enabled = open;
            if (open) _vPadTimer.Start(); else _vPadTimer.Stop();
        }

        // Asks the view to convert the next frame at full resolution; SetCaptured shows it.
        private void CaptureFrame()
        {
            if (!_view.IsCameraOpen) return;
            _view.CaptureFullRes(SetCaptured);
            _status.Text = "Capturing full-resolution frame...";
        }

        // UI thread: takes ownership of the full-res capture. The captured pane is
        // SizeMode.Zoom, so it shows the full image scaled to fit; saving keeps full res.
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

        // Camera-scale calibration → FrmVision.Calibration.cs
        // Chuck + wafer centre-find → FrmVision.CentreFind.cs

        // Rotate-about-crosshair UI (sign label + sign test) → FrmVision.Rotation.cs

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

        // Drift-corrected vision jog (puck poll + discrete d-pad) → FrmVision.Jog.cs

        private void Teardown()
        {
            _vPadTimer.Stop();
            _owner?.VisionStop();
            _view.StopCamera();
            _capturedBox.Image?.Dispose();
        }
    }
}
