using System;
using System.Drawing;
using System.Windows.Forms;

namespace NanotecController
{
    /// <summary>
    /// Vision protocols window: camera-scale calibration, chuck/wafer centre-find, notch find, and the
    /// rotate-about-crosshair setup, plus a captured pane where each detection's overlay lands. It does
    /// NOT own the camera — it is handed the shared <see cref="IVisionFrameSource"/> and enqueues
    /// detection jobs there. All motion is serialized by <see cref="IMotionHost"/>.
    ///
    /// The convenience copy of the jog / hold-to-rotate controls is hold-to-move, so this window
    /// installs a focus-loss safety stop (Deactivate → VisionStop + StopHoldRotate).
    /// </summary>
    public sealed partial class FrmVisionProtocols : Form
    {
        #region Shared view and captured pane

        /// <summary>The shared live camera, owned by FrmMain. Detection jobs run against its RAW
        /// frames; results are marshalled back and overlaid on the captured pane.</summary>
        private readonly IVisionFrameSource _view;
        private readonly PictureBox _capturedBox = new() { SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black };
        private readonly Label _status = new() { Text = "Ready.", AutoSize = true };

        /// <summary>Live-view MIRROR of the main-screen camera — the primary control pushes frames here,
        /// so a feature can be aligned to the crosshair without switching windows. The crosshair toggle
        /// is local to this view; zoom drives the shared camera, so it changes the main view too.</summary>
        private readonly VisionViewControl _live = new() { OwnsCamera = false };
        private readonly Button _crosshairBtn = new() { Text = "Crosshair" };
        private readonly ComboBox _zoomBox = new() { DropDownStyle = ComboBoxStyle.DropDownList };

        #endregion

        #region Camera-scale calibration

        private readonly IMotionHost _owner;
        private readonly SolidCircleDetector _markDetector = new();
        private readonly CameraCalibrator _calibrator = new();
        private readonly Button _sampleBtn = new() { Text = "Add Sample", Enabled = false };
        private readonly Button _computeBtn = new() { Text = "Compute && Save A", Enabled = false };
        private readonly Button _clearBtn = new() { Text = "Clear", Enabled = false };
        private readonly TextBox _sampleList = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 8F), BackColor = Color.White };
        private readonly Label _calibResult = new() { BorderStyle = BorderStyle.FixedSingle, Font = new Font("Consolas", 8F), TextAlign = ContentAlignment.TopLeft };

        #endregion

        #region Auto wafer centre-find (Θ scan)

        // The stage parks on one reachable spot on the rim and Θ turns the wafer underneath the
        // camera, the rim being far larger than the X/Y travel. Wafer Ø sizes the approach jump and
        // sanity-checks the fitted radius. Logic lives in FrmVisionProtocols.AutoWafer.cs.
        private readonly WaferEdgeDetector _waferDetector = new();
        private readonly NumericUpDown _waferDia = new() { Minimum = 10, Maximum = 600, Value = 200, DecimalPlaces = 1, Increment = 10 };
        private readonly NumericUpDown _waferSamples = new() { Minimum = 3, Maximum = 72, Value = 24, Increment = 1 };
        private readonly Button _waferRunBtn = new() { Text = "Auto Wafer Centre (Θ)", Enabled = false };
        private readonly Button _waferCancelBtn = new() { Text = "Cancel", Enabled = false };
        private readonly Button _waferGoBtn = new() { Text = "Go to Centre", Enabled = false };
        private readonly Label _waferResult = new() { BorderStyle = BorderStyle.FixedSingle, Font = new Font("Consolas", 8F), TextAlign = ContentAlignment.TopLeft };
        private readonly TextBox _waferLog = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 8F), BackColor = Color.White };

        #endregion

        #region Notch find (FrmVisionProtocols.NotchFind.cs)

        private readonly Button _notchRunBtn = new() { Text = "Find Notch (Θ sweep)", Enabled = false };
        private readonly Button _notchCancelBtn = new() { Text = "Cancel", Enabled = false };
        private readonly NumericUpDown _notchDatum = new() { Minimum = 0, Maximum = 360, Value = 0, DecimalPlaces = 1, Increment = 5 };
        private readonly NumericUpDown _notchThreshold = new() { Minimum = 0.05M, Maximum = 1.00M, Value = 0.30M, DecimalPlaces = 2, Increment = 0.05M };
        private readonly Button _notchGoBtn = new() { Text = "Rotate to datum", Enabled = false };
        private readonly Button _notchCheckBtn = new() { Text = "Check notch angle", Enabled = false };
        private readonly TextBox _notchLog = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 8F), BackColor = Color.White };

        #endregion

        #region Chuck centre-find (manual: >=3 edge points, circle-fit, go to centre)

        private readonly ChuckEdgeDetector _edgeDetector = new();
        private readonly CentreFinder _chuckFinder = new();   // accumulates chuck edge points (user-frame steps)
        private readonly Button _edgeBtn = new() { Text = "Add Edge", Enabled = false };
        private readonly Button _edgeAtCrossBtn = new() { Text = "Add at Crosshair", Enabled = false };
        private readonly Button _edgeClearBtn = new() { Text = "Clear Edges", Enabled = false };
        private readonly Button _edgeDeleteBtn = new() { Text = "Delete Selected", Enabled = false };
        private readonly Button _centreBtn = new() { Text = "Compute Centre", Enabled = false };
        private readonly Button _goCentreBtn = new() { Text = "Go to Centre", Enabled = false };
        private readonly ListBox _edgeList = new() { Font = new Font("Consolas", 8F), BackColor = Color.White, IntegralHeight = false };
        private readonly Label _centreResult = new() { BorderStyle = BorderStyle.FixedSingle, Font = new Font("Consolas", 8F), TextAlign = ContentAlignment.TopLeft };
        private (long X, long Y)? _chuckCentre;   // last computed/loaded centre (user frame)

        #endregion

        #region Auto chuck centre-find (FrmVisionProtocols.AutoCentre.cs)

        // The same points and the same fit, collected by the stage instead of by hand. Max R is the
        // MAX SEARCH RADIUS in steps — an operational limit, not a measurement of the feature, so it
        // is NOT seeded from the last fit.
        private readonly NumericUpDown _autoRadius = new() { Minimum = 0, Maximum = 100000000, Increment = 1000, ThousandsSeparator = true, Value = 10000 };
        private readonly Button _autoRunBtn = new() { Text = "Auto Centre-Find", Enabled = false };
        private readonly Button _autoCancelBtn = new() { Text = "Cancel", Enabled = false };
        private readonly TextBox _autoLog = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 8F), BackColor = Color.White };

        #endregion

        #region Rotate about crosshair (setup)

        // Relative "Rotate by", absolute "Rotate to", and the one-time handedness "Sign test". All
        // gated and executed by FrmMain. The interactive hold-to-rotate lives in the main VISION mode.
        private readonly NumericUpDown _rotBy = new() { Minimum = -360, Maximum = 360, Value = 90, DecimalPlaces = 1, Increment = 5, Enabled = false };
        private readonly Button _rotByBtn = new() { Text = "Rotate by°", Enabled = false };
        private readonly NumericUpDown _rotTo = new() { Minimum = 0, Maximum = 360, Value = 0, DecimalPlaces = 1, Increment = 5, Enabled = false };
        private readonly Button _rotToBtn = new() { Text = "Rotate to°", Enabled = false };
        private readonly Label _signLabel = new() { AutoSize = true };
        private readonly Button _signTestBtn = new() { Text = "Sign test", Enabled = false };

        #endregion

        #region Vision motion controls

        // A convenience copy of the main-screen VISION mode, so the operator can jog while watching
        // this window during calibration. Everything runs through IMotionHost. Rotate SPEED is set on
        // the main window; this window only triggers the moves.
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

        #endregion

        #region Construction and layout

        public FrmVisionProtocols(IMotionHost owner, IVisionFrameSource view)
        {
            _owner = owner;
            _view = view;
            Text = "Vision - calibration & centre-find";
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9F);
            // Height carries the notch-find row under the wafer group (y 664..750); the bottom strip
            // is anchored to the bottom, so everything above it keeps its place.
            ClientSize = new Size(1490, 762);
            MinimumSize = new Size(1200, 802);

            // Live view (mirror) + captured pane (top row)
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
            _live.TickScaleProvider = () => VisionViewControl.MmPerPixel(_owner.Calibration);   // 1 mm marks here too

            var capLabel = new Label { Text = "Captured (detection overlay)", Location = new Point(500, 8), AutoSize = true, Font = new Font("Segoe UI", 10F, FontStyle.Bold) };
            _capturedBox.Location = new Point(500, 32);
            _capturedBox.Size = new Size(488, 440);
            _capturedBox.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left;

            _status.Location = new Point(500, 484);
            _status.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;

            // Auto wafer centre-find, Θ scan (bottom strip)
            // Fully automatic: the stage finds one reachable spot on the rim, then Θ turns the wafer a
            // full revolution underneath the camera while the rim point is re-measured at each angle.
            // The log pane is the run's transcript — which angles found an edge, and where.
            var waferLabel = new Label { Text = "Auto wafer centre-find (Θ scan)", Location = new Point(12, 506), AutoSize = true, Font = new Font("Segoe UI", 9F, FontStyle.Bold), Anchor = AnchorStyles.Bottom | AnchorStyles.Left };

            // These labels are AutoSize, so their width is whatever the text measures under the current
            // font/DPI. Placing the spinners from the measured width keeps them clear of the text; a
            // fixed offset only holds for the exact metrics it was tuned against.
            int After(Label l, int gap = 8) => l.Left + TextRenderer.MeasureText(l.Text, l.Font).Width + gap;

            var waferDiaLabel = new Label { Text = "Wafer Ø (mm):", Location = new Point(12, 535), AutoSize = true, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            _waferDia.Size = new Size(74, 22);
            _waferDia.Location = new Point(After(waferDiaLabel), 532);
            _waferDia.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;

            var waferSamplesLabel = new Label { Text = "Samples:", Location = new Point(_waferDia.Right + 18, 535), AutoSize = true, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            _waferSamples.Size = new Size(56, 22);
            _waferSamples.Location = new Point(After(waferSamplesLabel), 532);
            _waferSamples.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;

            // Button widths are sized to the caption for the same reason as the labels above — the
            // previous fixed widths were narrower than the text and clipped it to an ellipsis.
            _waferRunBtn.Size = new Size(172, 28);
            _waferRunBtn.Location = new Point(12, 560);
            _waferRunBtn.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _waferRunBtn.Click += async (s, e) => await RunWaferScanAsync();

            _waferCancelBtn.Size = new Size(76, 28);
            _waferCancelBtn.Location = new Point(_waferRunBtn.Right + 8, 560);
            _waferCancelBtn.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _waferCancelBtn.Click += (s, e) => CancelWaferScan();

            _waferGoBtn.Size = new Size(104, 28);
            _waferGoBtn.Location = new Point(_waferCancelBtn.Right + 8, 560);
            _waferGoBtn.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _waferGoBtn.Click += async (s, e) => await GoToWaferCentreAsync();

            _waferLog.Location = new Point(12, 594);
            _waferLog.Size = new Size(340, 62);
            _waferLog.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;

            // Starts clear of the widened button row; the right edge (690) still stops short of the
            // auto-chuck column at 700.
            _waferResult.Location = new Point(_waferGoBtn.Right + 12, 532);
            _waferResult.Size = new Size(298, 124);
            _waferResult.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;

            // Notch find (bottom strip, below the wafer group)
            // Needs the wafer centre-find first: the sweep follows the rim using the stored offset.
            // Θ turns continuously for up to a revolution (~112 s — Θ's speed cap is the floor) while
            // frames stream past a coarse detector; on a hit it stops and re-measures a stationary
            // frame. "Rotate to datum" is separate on purpose, so a search never turns the wafer.
            var notchLabel = new Label { Text = "Notch find (Θ sweep)", Location = new Point(12, 664), AutoSize = true, Font = new Font("Segoe UI", 9F, FontStyle.Bold), Anchor = AnchorStyles.Bottom | AnchorStyles.Left };

            // Exposed because the right value depends on how much the continuous sweep blurs the rim,
            // which cannot be known off-hardware. Higher = fussier. Plain rim measures 0.01-0.05 mm
            // and the notch 0.54 mm, so there is a lot of room between them.
            var notchThreshLabel = new Label { Text = "Trigger (mm):", Location = new Point(After(notchLabel, 16), 667), AutoSize = true, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            _notchThreshold.Size = new Size(64, 22);
            _notchThreshold.Location = new Point(After(notchThreshLabel), 664);
            _notchThreshold.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;

            // Right of the trigger spinner and clear of the log pane. Measures the stored angle
            // against the wafer instead of against the eye: it turns the notch to the camera and
            // re-measures it there, so a datum that looks wrong can be judged as a number.
            _notchCheckBtn.Location = new Point(_notchThreshold.Right + 8, 662);
            _notchCheckBtn.Size = new Size(150, 26);
            _notchCheckBtn.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _notchCheckBtn.Click += async (s, e) => await RunNotchCheckAsync();

            _notchRunBtn.Size = new Size(164, 28);
            _notchRunBtn.Location = new Point(12, 690);
            _notchRunBtn.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _notchRunBtn.Click += async (s, e) => await RunNotchFindAsync();

            _notchCancelBtn.Size = new Size(76, 28);
            _notchCancelBtn.Location = new Point(_notchRunBtn.Right + 8, 690);
            _notchCancelBtn.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _notchCancelBtn.Click += (s, e) => CancelNotchFind();

            var notchDatumLabel = new Label { Text = "Datum°:", Location = new Point(_notchCancelBtn.Right + 10, 695), AutoSize = true, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            _notchDatum.Size = new Size(64, 22);
            _notchDatum.Location = new Point(After(notchDatumLabel), 692);
            _notchDatum.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;

            _notchGoBtn.Size = new Size(128, 28);
            _notchGoBtn.Location = new Point(_notchDatum.Right + 8, 690);
            _notchGoBtn.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _notchGoBtn.Click += async (s, e) => await RotateNotchToDatumAsync();

            // Starts clear of the widest row above it (the datum row); the right edge (992) still lines
            // up with the auto-chuck log.
            _notchLog.Location = new Point(_notchGoBtn.Right + 11, 664);
            _notchLog.Size = new Size(440, 86);
            _notchLog.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;

            // Auto chuck centre-find (bottom strip, right of the wafer group)
            // Rough-centre the chuck by hand, enter the nominal radius, and the stage probes outward in
            // 8 directions collecting the rim points the manual "Add Edge" flow collects by jogging.
            // The log pane is the run's transcript — which directions found an edge, and where.
            var autoLabel = new Label { Text = "Auto chuck centre-find", Location = new Point(700, 506), AutoSize = true, Font = new Font("Segoe UI", 9F, FontStyle.Bold), Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            var autoRadiusLabel = new Label { Text = "Max R (steps):", Location = new Point(700, 532), AutoSize = true, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };

            _autoRadius.Size = new Size(110, 22);
            _autoRadius.Location = new Point(After(autoRadiusLabel), 529);
            _autoRadius.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;

            _autoRunBtn.Location = new Point(700, 558);
            _autoRunBtn.Size = new Size(140, 28);
            _autoRunBtn.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _autoRunBtn.Click += async (s, e) => await RunAutoCentreAsync();

            _autoCancelBtn.Location = new Point(846, 558);
            _autoCancelBtn.Size = new Size(96, 28);
            _autoCancelBtn.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _autoCancelBtn.Click += (s, e) => CancelAutoCentre();

            _autoLog.Location = new Point(700, 592);
            _autoLog.Size = new Size(292, 62);
            _autoLog.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;

            // Camera-scale calibration column (right)
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

            // Chuck centre-find column (far right)
            // Jog so the chuck EDGE is in view (main window), Add Edge (detects the edge point nearest
            // the crosshair, converts to step space via the calibration). After >=3 around the rim,
            // Compute Centre circle-fits them; Go to Centre drives there.
            var centreLabel = new Label { Text = "Chuck centre-find", Location = new Point(1248, 8), AutoSize = true, Font = new Font("Segoe UI", 10F, FontStyle.Bold), Anchor = AnchorStyles.Top | AnchorStyles.Right };

            // Two ways to add a rim point: "Add Edge" runs the detector; "Add at Crosshair" takes the
            // current motor position directly (jog the edge onto the crosshair by eye first).
            _edgeBtn.Location = new Point(1248, 34);
            _edgeBtn.Size = new Size(112, 30);
            _edgeBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _edgeBtn.Click += (s, e) => RequestEdge();

            _edgeAtCrossBtn.Location = new Point(1364, 34);
            _edgeAtCrossBtn.Size = new Size(112, 30);
            _edgeAtCrossBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _edgeAtCrossBtn.Click += (s, e) => AddEdgeAtCrosshair();

            _edgeList.Location = new Point(1248, 68);
            _edgeList.Size = new Size(228, 178);
            _edgeList.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _edgeList.SelectedIndexChanged += (s, e) => _edgeDeleteBtn.Enabled = _edgeList.SelectedIndex >= 0;

            // Clear all points, or delete just the selected one (before computing the centre).
            _edgeClearBtn.Location = new Point(1248, 250);
            _edgeClearBtn.Size = new Size(112, 26);
            _edgeClearBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _edgeClearBtn.Click += (s, e) => ClearEdges();

            _edgeDeleteBtn.Location = new Point(1364, 250);
            _edgeDeleteBtn.Size = new Size(112, 26);
            _edgeDeleteBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _edgeDeleteBtn.Click += (s, e) => DeleteSelectedEdge();

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

            // Rotate about crosshair — setup (under the centre-find column)
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
            _rotByBtn.Click += async (s, e) => await _owner.RotateAboutCrosshairAsync((double)_rotBy.Value);

            _rotTo.Location = new Point(1248, 546);
            _rotTo.Size = new Size(100, 24);
            _rotTo.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _rotToBtn.Location = new Point(1352, 544);
            _rotToBtn.Size = new Size(124, 28);
            _rotToBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _rotToBtn.Click += async (s, e) => await _owner.RotateToAngleAsync((double)_rotTo.Value);

            _signLabel.Location = new Point(1248, 580);
            _signLabel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _signTestBtn.Location = new Point(1352, 576);
            _signTestBtn.Size = new Size(124, 28);
            _signTestBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _signTestBtn.Click += async (s, e) => await SignTestAsync();

            // Drift-corrected vision jog (under the calibration column)
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
                b.MouseUp += (s, e) => _owner.VisionStop();
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
            _rotHoldCcwBtn.MouseDown += (s, e) => { _ = _owner.HoldRotateAsync(-1); };
            _rotHoldCcwBtn.MouseUp += (s, e) => _owner.StopHoldRotate();
            _rotHoldCwBtn.Location = new Point(1364, 612);
            _rotHoldCwBtn.Size = new Size(112, 30);
            _rotHoldCwBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _rotHoldCwBtn.MouseDown += (s, e) => { _ = _owner.HoldRotateAsync(+1); };
            _rotHoldCwBtn.MouseUp += (s, e) => _owner.StopHoldRotate();

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
            Controls.Add(waferDiaLabel);
            Controls.Add(_waferDia);
            Controls.Add(waferSamplesLabel);
            Controls.Add(_waferSamples);
            Controls.Add(_waferRunBtn);
            Controls.Add(_waferCancelBtn);
            Controls.Add(_waferGoBtn);
            Controls.Add(_waferLog);
            Controls.Add(_waferResult);
            Controls.Add(notchLabel);
            Controls.Add(notchThreshLabel);
            Controls.Add(_notchThreshold);
            Controls.Add(_notchRunBtn);
            Controls.Add(_notchCancelBtn);
            Controls.Add(notchDatumLabel);
            Controls.Add(_notchDatum);
            Controls.Add(_notchGoBtn);
            Controls.Add(_notchCheckBtn);
            Controls.Add(_notchLog);
            Controls.Add(autoLabel);
            Controls.Add(autoRadiusLabel);
            Controls.Add(_autoRadius);
            Controls.Add(_autoRunBtn);
            Controls.Add(_autoCancelBtn);
            Controls.Add(_autoLog);
            Controls.Add(centreLabel);
            Controls.Add(_edgeBtn);
            Controls.Add(_edgeAtCrossBtn);
            Controls.Add(_edgeClearBtn);
            Controls.Add(_edgeDeleteBtn);
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
            if (_owner.Calibration.ChuckCenterX is long cxLoaded && _owner.Calibration.ChuckCenterY is long cyLoaded)
            {
                _chuckCentre = (cxLoaded, cyLoaded);
                _goCentreBtn.Enabled = true;
                _centreResult.Text = $"Saved centre:\r\nX={cxLoaded}  Y={cyLoaded}";
            }
            // Likewise a saved wafer scan. The offset — not the snapshot centre — is what makes
            // "Go to Centre" usable, since the target is recomputed for the current Θ.
            if (_owner.Calibration.WaferOffsetX is long woxLoaded && _owner.Calibration.WaferOffsetY is long woyLoaded)
                _waferResult.Text = $"Saved wafer scan:\r\noffset X={woxLoaded}  Y={woyLoaded}\r\n" +
                                    $"R={_owner.Calibration.WaferRadius?.ToString() ?? "?"} steps";

            // The camera is already streaming on the main screen; gate on its live state and
            // follow open/close (e.g. a Retry on the main toolbar) while this window is open.
            _view.CameraStateChanged += OnCameraStateChanged;
            OnCameraStateChanged();
            _view.FrameDisplayed += _live.PushFrame;   // mirror the main-screen live feed into _live

            // Safety: the vision jog / hold-rotate are hold-to-move. If this window loses focus mid-hold
            // (alt-tab, a dialog) the MouseUp may never arrive, so stop all continuous motion on deactivate.
            Deactivate += (s, e) => { _owner.VisionStop(); _owner.StopHoldRotate(); };

            FormClosing += (s, e) => Teardown();
        }

        // Camera opened or closed on the main screen: gate the protocol actions that need live frames.
        private void OnCameraStateChanged()
        {
            if (IsDisposed) return;
            bool open = _view.IsCameraOpen;
            _sampleBtn.Enabled = open;
            // The chuck buttons (_edgeBtn, _edgeAtCrossBtn, _centreBtn, …) are gated in RefreshEdgeUi
            // instead, which also has to honour an auto centre-find in progress. RefreshAutoUi routes
            // through it, so both conditions are applied in one place.
            RefreshAutoUi();
            RefreshWaferUi();
            RefreshNotchUi();
            _rotBy.Enabled = _rotByBtn.Enabled = open;
            _rotTo.Enabled = _rotToBtn.Enabled = _signTestBtn.Enabled = open;

            // Vision motion controls: live only while the camera streams. The puck poll runs only then.
            _vSpeed.Enabled = _vPad.Enabled = open;
            _vUp.Enabled = _vDown.Enabled = _vLeft.Enabled = _vRight.Enabled = open;
            _rotHoldCcwBtn.Enabled = _rotHoldCwBtn.Enabled = open;
            if (open) _vPadTimer.Start(); else { _vPadTimer.Stop(); _owner.VisionStop(); }
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
        // Chuck centre-find (manual) → FrmVisionProtocols.CentreFind.cs
        // Chuck centre-find (automatic) → FrmVisionProtocols.AutoCentre.cs
        // Wafer centre-find (Θ scan) → FrmVisionProtocols.AutoWafer.cs
        // Rotate-about-crosshair UI (sign label + sign test) → FrmVisionProtocols.Rotation.cs

        private void Teardown()
        {
            _vPadTimer.Stop();
            _owner.VisionStop();
            _owner.StopHoldRotate();
            _view.CameraStateChanged -= OnCameraStateChanged;
            _view.FrameDisplayed -= _live.PushFrame;
            _capturedBox.Image?.Dispose();
        }

        #endregion
    }
}
