using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MotorControlApp
{
    /// <summary>
    /// Multi-axis motion GUI for the inspection table (X, Y, Z, Θ). Connects to all
    /// four EtherCAT drives, enables/disables them, and offers two manual-control
    /// inputs that both drive the shared <see cref="MultiAxisController"/>:
    ///   • per-axis hold-to-jog buttons (individual control), and
    ///   • a digital joystick (hold the deadman to move).
    ///
    /// Safety model: connecting performs NO motion; drives must be Enabled first.
    /// All jogging is momentary — releasing a button, releasing the joystick deadman,
    /// losing the joystick, or losing window focus halts motion immediately.
    ///
    /// Layout lives in FrmMain.Designer.cs; the four axis rows are built in code from
    /// <see cref="TableAxes.Default"/> since they're identical and data-driven. The
    /// behaviour is split across partial files by concern:
    ///   • FrmMain.Connection.cs  — connect/disconnect, enable/disable, param readout
    ///   • FrmMain.Jog.cs         — per-axis jog buttons, status poll, soft-limit guard
    ///   • FrmMain.Input.cs       — USB + on-screen joystick input mapping
    ///   • FrmMain.Calibration.cs — Home All, Move To, limit capture/find, Go Home
    /// This file holds shared state, the ctor, UI scaffolding, and lifecycle.
    /// </summary>
    public partial class FrmMain : Form
    {
        private readonly MultiAxisConnection _connection = new();
        private readonly Joystick _joystick = new();
        private readonly IProgress<string> _log;

        private MultiAxisController? _motion;
        private bool _drivesEnabled;
        private bool _busy;
        private int _statusFailures;

        private const int EXPECTED_AXES = 4;

        // A software (Npcap) EtherCAT master is not real-time; tolerate a few
        // consecutive failed status reads before declaring the link lost.
        private const int MAX_CONSECUTIVE_READ_FAILURES = 5;

        // The joystick "fast" button multiplier (capped at each axis's slider max).
        private const int FAST_FACTOR = 3;

        // Speed for the automatic limit-find (Y), in drive velocity units. Kept low so the
        // approach into the switch is gentle; Y quick-stops at its switches (0x3701 = 6),
        // and the overshoot cancels in the centre calc anyway.
        private const int FIND_LIMIT_SPEED = 4000;
        // Limit-find polling + ceilings.
        private const int FIND_POLL_MS = 15;
        private const int FIND_TIMEOUT_MS = 60000;   // per end: fail rather than run forever
        private const int BACKOFF_TIMEOUT_MS = 5000; // backing off a switch should be quick
        private const long SW_LIMIT_BITS = 0x3;      // 0x60FD bits 0 (neg) + 1 (pos)

        // Fixed velocities for Go Home / Home All (drive velocity units) — NOT the runtime
        // jog sliders, so homing is always at a known, repeatable speed.
        private const int HOME_SPEED_X = 4000;
        private const int HOME_SPEED_Y = 4000;
        private const int HOME_SPEED_Z = 400;

        private static int HomeSpeedFor(AxisId id) => id switch
        {
            AxisId.X => HOME_SPEED_X,
            AxisId.Y => HOME_SPEED_Y,
            AxisId.Z => HOME_SPEED_Z,
            _ => HOME_SPEED_Z,   // conservative default (Theta is never homed)
        };

        // Per-axis travel limits + Home, persisted to disk (survives restarts). Loaded in
        // the ctor so a read failure (which silently drops the soft limits) can be logged.
        private readonly CalibrationStore _calib;
        private FrmCalibration? _calibWindow;

        // Soft-limit enforcement during jogging: last polled position per axis (to infer
        // travel direction) and which axes are currently parked at a limit (one-shot log).
        private readonly Dictionary<AxisId, long> _prevPos = new();
        private readonly HashSet<AxisId> _atSoftLimit = new();
        // Soft-limit jog gate (polarity-agnostic — kept in COMMAND space, never assuming a
        // command→position sign): _cmdDir = the direction currently commanded per axis;
        // _limitBlockedDir = the direction that tripped a soft limit and is refused until
        // the axis is jogged back into range. This stops a re-pressed/held outward jog from
        // re-lurching for up to a status-poll period — critical for X+ and Z, which have no
        // hardware switch (see limit-switch findings).
        private readonly Dictionary<AxisId, int> _cmdDir = new();
        private readonly Dictionary<AxisId, int> _limitBlockedDir = new();

        private sealed record AxisRow(Button Neg, Button Pos, Label Status, TrackBar Speed, Label SpeedValue);
        private readonly Dictionary<AxisId, AxisRow> _axisRows = new();
        // Last joystick command per axis, for send-on-change (don't flood the soft master).
        private readonly Dictionary<AxisId, (int dir, bool fast)> _lastJoy = new();
        // Last X/Y velocity sent by the on-screen joystick (send-on-change).
        private int _lastVx, _lastVy;

        private static readonly Color LedConnected = Color.LawnGreen;
        private static readonly Color LedDisconnected = Color.Firebrick;
        private static readonly Color LedBusy = Color.Goldenrod;

        public FrmMain()
        {
            InitializeComponent();
            _log = new Progress<string>(AppendLog);
            _calib = CalibrationStore.Load(out string? calibWarning);
            BuildAxisRows();
            BuildPositionButton();
            SetState(connected: false, busy: false, "Disconnected");
            if (calibWarning != null) AppendLog("WARN: " + calibWarning);
        }

        // --- UI scaffolding (the data-driven controls built in code) --------------

        /// <summary>
        /// Creates the per-axis jog rows: name + speed slider (+ live value) + hold-to-move
        /// −/+ buttons (direction only) + status. The slider is that axis's jog speed, used
        /// by BOTH the buttons and the joystick.
        /// </summary>
        private void BuildAxisRows()
        {
            int y = 2;
            foreach (AxisConfig cfg in TableAxes.Default)
            {
                AxisId id = cfg.Id; // capture for the closures below

                var name = new Label
                {
                    Text = cfg.Name,
                    Location = new Point(6, y + 12),
                    AutoSize = true,
                    Font = new Font("Segoe UI", 10F, FontStyle.Bold),
                };
                var speed = new TrackBar
                {
                    Location = new Point(44, y),
                    Size = new Size(160, 40),
                    Minimum = 0,
                    Maximum = cfg.JogVelocityMax,
                    TickStyle = TickStyle.None,
                    SmallChange = 10,
                    LargeChange = 100,
                    Value = Math.Min(Math.Max(cfg.JogVelocityDefault, 0), cfg.JogVelocityMax),
                    Enabled = false,
                };
                var speedValue = new Label { Text = speed.Value.ToString(), Location = new Point(208, y + 12), AutoSize = true };
                var neg = new Button { Text = "◀  −", Location = new Point(252, y + 4), Size = new Size(74, 34), Enabled = false };
                var pos = new Button { Text = "+  ▶", Location = new Point(330, y + 4), Size = new Size(74, 34), Enabled = false };
                var status = new Label { Text = "pos -", Location = new Point(412, y + 12), AutoSize = true, Font = new Font("Consolas", 9F) };

                speed.Scroll += (s, e) => speedValue.Text = speed.Value.ToString();
                neg.MouseDown += (s, e) => StartJog(id, -1);
                neg.MouseUp += (s, e) => StopJog(id);
                pos.MouseDown += (s, e) => StartJog(id, +1);
                pos.MouseUp += (s, e) => StopJog(id);

                axesPanel.Controls.Add(name);
                axesPanel.Controls.Add(speed);
                axesPanel.Controls.Add(speedValue);
                axesPanel.Controls.Add(neg);
                axesPanel.Controls.Add(pos);
                axesPanel.Controls.Add(status);

                _axisRows[id] = new AxisRow(neg, pos, status, speed, speedValue);
                _lastJoy[id] = (0, false);
                y += 42;
            }
        }

        // --- Shared op / timer helpers --------------------------------------------

        /// <summary>Runs a drive op off the UI thread with both timers paused.</summary>
        private async Task<bool> RunDriveOp(Action op)
        {
            statusTimer.Stop();
            joystickTimer.Stop();
            try
            {
                await Task.Run(op);
                return true;
            }
            catch (Exception ex)
            {
                AppendLog($"ERROR: {ex.Message}");
                return false;
            }
        }

        /// <summary>Restarts the status (and joystick, if on) timers when connected.</summary>
        private void RestartTimers()
        {
            if (!_connection.IsConnected) return;
            ResetSoftLimitTracking();   // a move may have happened while paused; rebaseline
            statusTimer.Start();
            if (rbUsb.Checked || rbScreen.Checked) { ResetJoy(); joystickTimer.Start(); }
        }

        // --- Window lifecycle / focus safety --------------------------------------

        /// <summary>
        /// Safety: focus loss halts all motion AND pauses the joystick poll. Stopping the
        /// timer is essential — a Forms.Timer keeps firing while unfocused, so without this
        /// the next tick (~50 ms) would re-command the jog right after StopAll.
        /// </summary>
        private void FrmMain_Deactivate(object? sender, EventArgs e)
        {
            // A running op (enable/disable, go-home, find-limits) owns the drives and has
            // already paused the timers — don't stomp it with a focus-loss StopAll (which
            // would also race the background thread on the single NanoLib channel). The
            // calibration window taking focus is the common trigger here.
            if (_busy) return;
            joystickTimer.Stop();
            if (_motion == null) return;
            try { _motion.StopAll(); } catch { /* best effort */ }
            ResetJoy();
        }

        /// <summary>Resumes joystick polling when the window regains focus (if it was on).</summary>
        private void FrmMain_Activated(object? sender, EventArgs e)
        {
            if (_busy) return;   // a running op manages the timers; don't restart mid-op
            if (_connection.IsConnected && (rbUsb.Checked || rbScreen.Checked))
            {
                ResetJoy();
                joystickTimer.Start();
            }
        }

        private void FrmMain_FormClosing(object? sender, FormClosingEventArgs e)
        {
            statusTimer.Stop();
            joystickTimer.Stop();
            if (_connection.IsConnected)
            {
                try
                {
                    if (_motion != null && _drivesEnabled) _motion.DisableAll();
                }
                catch (ChuckException) { /* already closing */ }
                _connection.Disconnect(_log);
            }
        }

        // --- Shared UI state ------------------------------------------------------

        private void SetState(bool connected, bool busy, string status)
        {
            _busy = busy;
            ledPanel.BackColor = busy ? LedBusy : (connected ? LedConnected : LedDisconnected);
            statusLabel.Text = status;
            RefreshButtons();
        }

        private void RefreshButtons()
        {
            bool conn = _connection.IsConnected;
            connectButton.Enabled = !_busy && !conn;
            disconnectButton.Enabled = !_busy && conn;
            readParamsButton.Enabled = !_busy && conn;
            calibButton.Enabled = !_busy && conn;

            enableButton.Enabled = !_busy && conn && !_drivesEnabled;
            disableButton.Enabled = !_busy && conn && _drivesEnabled;
            homeAllButton.Enabled = !_busy && conn && _drivesEnabled;
            positionButton.Enabled = !_busy && conn;   // open whenever connected; Go is gated inside the window
            bool inputOk = !_busy && conn && _drivesEnabled;
            rbOff.Enabled = inputOk;
            rbUsb.Enabled = inputOk;
            rbScreen.Enabled = inputOk;
            joystickPad.Enabled = inputOk && rbScreen.Checked;

            bool canJog = !_busy && conn && _drivesEnabled;
            foreach (AxisRow row in _axisRows.Values)
            {
                row.Speed.Enabled = conn;   // adjustable whenever connected
                row.Neg.Enabled = canJog;
                row.Pos.Enabled = canJog;
            }
        }

        private void AppendLog(string message)
        {
            logBox.AppendText($"{DateTime.Now:HH:mm:ss}  {message}{Environment.NewLine}");
        }
    }
}
