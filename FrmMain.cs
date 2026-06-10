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
    /// <see cref="TableAxes.Default"/> since they're identical and data-driven.
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

        // "Move To" console (built in code), enabled with motion.
        private TextBox _moveXBox = null!, _moveYBox = null!, _moveZBox = null!;
        private Button _moveButton = null!;

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
            BuildMoveToConsole();
            SetState(connected: false, busy: false, "Disconnected");
            if (calibWarning != null) AppendLog("WARN: " + calibWarning);
        }

        /// <summary>
        /// Builds the "Move To" console (X/Y/Z coordinate fields + Move button) in the
        /// free area below the on-screen joystick. Each field is optional; the move is
        /// validated against the per-axis limits in <see cref="MoveToAsync"/>.
        /// </summary>
        private void BuildMoveToConsole()
        {
            var group = new GroupBox
            {
                Text = "Move To (drive units)",
                Location = new Point(694, 452),
                Size = new Size(168, 156),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
            };

            TextBox Field(string label, int y)
            {
                group.Controls.Add(new Label { Text = label, Location = new Point(12, y + 3), AutoSize = true });
                var tb = new TextBox { Location = new Point(36, y), Size = new Size(118, 26) };
                group.Controls.Add(tb);
                return tb;
            }

            _moveXBox = Field("X", 28);
            _moveYBox = Field("Y", 60);
            _moveZBox = Field("Z", 92);

            _moveButton = new Button
            {
                Text = "Move",
                Location = new Point(36, 122),
                Size = new Size(118, 28),
                Enabled = false,
            };
            _moveButton.Click += async (s, e) => await MoveToAsync(_moveXBox.Text, _moveYBox.Text, _moveZBox.Text);
            group.Controls.Add(_moveButton);

            Controls.Add(group);
        }

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

        // --- Connection -----------------------------------------------------------

        private async void connectButton_Click(object? sender, EventArgs e) => await ConnectAsync();
        private async void disconnectButton_Click(object? sender, EventArgs e) => await DisconnectAsync();

        private async Task ConnectAsync()
        {
            SetState(connected: false, busy: true, "Scanning buses...");
            logBox.Clear();

            IReadOnlyList<string> buses = await Task.Run(() => _connection.ListBuses(_log));
            if (buses.Count == 0)
            {
                AppendLog("No network buses found - check Npcap and the EtherCAT NIC.");
                SetState(connected: false, busy: false, "No buses found");
                return;
            }

            // Let the user choose which bus to connect to (modal; -1 = cancelled).
            int choice = BusPicker.Choose(this, buses);
            if (choice < 0)
            {
                AppendLog("Connection cancelled.");
                SetState(connected: false, busy: false, "Disconnected");
                return;
            }

            SetState(connected: false, busy: true, "Connecting...");
            bool ok = await Task.Run(() => _connection.Connect(choice, EXPECTED_AXES, _log));
            if (!ok)
            {
                SetState(connected: false, busy: false, "Connection FAILED");
                return;
            }

            try
            {
                _motion = new MultiAxisController(_connection, TableAxes.Default);
            }
            catch (Exception ex)
            {
                AppendLog($"Axis-mapping error: {ex.Message}");
                AppendLog("Disconnecting - not all expected axes are present.");
                await Task.Run(() => _connection.Disconnect(_log));
                _motion = null;
                SetState(connected: false, busy: false, "Connection FAILED");
                return;
            }

            _drivesEnabled = false;
            _statusFailures = 0;
            rbOff.Checked = true;
            SetState(connected: true, busy: false, $"Connected ({_connection.Handles.Count} axes)");
            statusTimer.Start();
        }

        private async Task DisconnectAsync()
        {
            statusTimer.Stop();
            joystickTimer.Stop();
            rbOff.Checked = true;
            SetState(connected: _connection.IsConnected, busy: true, "Disconnecting...");

            if (_motion != null && _drivesEnabled)
                await Task.Run(() => _motion.DisableAll());
            _drivesEnabled = false;

            await Task.Run(() => _connection.Disconnect(_log));
            _motion = null;

            foreach (AxisRow row in _axisRows.Values) row.Status.Text = "-";
            ResetSoftLimitTracking();
            SetState(connected: false, busy: false, "Disconnected");
        }

        // --- Enable / disable (all axes) ------------------------------------------

        private async void enableButton_Click(object? sender, EventArgs e)
        {
            if (_motion == null) return;
            _busy = true;
            RefreshButtons();
            AppendLog("Enabling all drives (holding torque, no motion)...");

            bool ok = await RunDriveOp(() => _motion.EnableAll());
            _drivesEnabled = ok;
            AppendLog(ok ? "All drives ENABLED." : "Enable FAILED - see error above.");

            _busy = false;
            RestartTimers();
            RefreshButtons();
        }

        private async void disableButton_Click(object? sender, EventArgs e)
        {
            if (_motion == null) return;
            rbOff.Checked = true;
            _busy = true;
            RefreshButtons();

            await RunDriveOp(() => _motion.DisableAll());
            _drivesEnabled = false;
            AppendLog("All drives DISABLED.");

            _busy = false;
            RestartTimers();
            RefreshButtons();
        }

        // --- Per-axis hold-to-jog (individual control) ----------------------------
        // Quick SDO writes done on the UI thread so press/release ordering is exact.

        private void StartJog(AxisId id, int direction)
        {
            if (_motion == null || !_drivesEnabled || _busy) return;
            if (IsJogBlocked(id, direction))
            {
                AppendLog($"{id} at soft limit - jog {(direction > 0 ? "+" : "−")} blocked (jog back into range first).");
                return;
            }
            int speed = _axisRows[id].Speed.Value;
            try
            {
                _motion.JogAt(id, direction, speed);
                _cmdDir[id] = direction;
                AppendLog($"Jog {id} {(direction > 0 ? "+" : "−")} at {speed}.");
            }
            catch (ChuckException ex)
            {
                AppendLog($"ERROR: jog {id} failed: {ex.Message}");
            }
        }

        private void StopJog(AxisId id)
        {
            if (_motion == null) return;
            try { _motion.Stop(id); _cmdDir[id] = 0; }
            catch (ChuckException ex) { AppendLog($"ERROR: stop {id} failed: {ex.Message}"); }
        }

        /// <summary>
        /// True if jogging <paramref name="dir"/> would push the axis further past the soft
        /// limit it is already parked against. The blocked direction is recorded in command
        /// space at the moment the limit tripped, so this never assumes a command→position
        /// polarity; jogging the opposite way (back into range) is always allowed.
        /// </summary>
        private bool IsJogBlocked(AxisId id, int dir)
            => dir != 0 && _limitBlockedDir.TryGetValue(id, out int b) && b == dir;

        // --- Joystick -------------------------------------------------------------

        private void inputSourceChanged(object? sender, EventArgs e)
        {
            // Source switch (Off / USB / On-screen, mutually exclusive): stop whatever
            // was moving, then configure the new source. Radios auto-exclude, so this
            // fires twice on a switch; recomputing from current state keeps it correct.
            StopJoyAxes();

            bool usb = rbUsb.Checked, screen = rbScreen.Checked;
            joystickPad.Enabled = screen && _drivesEnabled && !_busy;

            if (usb || screen)
            {
                ResetJoy();
                joystickTimer.Start();
                joystickStatusLabel.Text = usb ? "USB: idle" : "On-screen: idle";
                AppendLog(usb
                    ? "Input: USB joystick (hold button 1 = deadman; button 2 = fast)."
                    : "Input: on-screen joystick (drag the puck; release = stop).");
            }
            else
            {
                joystickTimer.Stop();
                joystickStatusLabel.Text = "Input: off";
            }
        }

        private void joystickTimer_Tick(object? sender, EventArgs e)
        {
            if (rbScreen.Checked) { TickOnScreen(); return; }   // on-screen puck path

            JoystickState st = _joystick.Poll();

            if (!st.Connected)
            {
                joystickStatusLabel.Text = "Joystick: NOT FOUND";
                StopJoyAxes(); // safety: a vanished joystick must not leave an axis running
                return;
            }
            joystickStatusLabel.Text = $"Joystick: {(st.Deadman ? "DEADMAN held" : "idle")}{(st.Fast ? " | FAST" : "")}";

            bool allow = st.Deadman && _drivesEnabled && !_busy && _motion != null;

            ApplyJoy(AxisId.X, allow ? st.X : 0, st.Fast);
            ApplyJoy(AxisId.Y, allow ? st.Y : 0, st.Fast);
            ApplyJoy(AxisId.Z, allow ? st.Z : 0, st.Fast);
            ApplyJoy(AxisId.Theta, allow ? st.R : 0, st.Fast);
        }

        /// <summary>
        /// On-screen joystick: map the puck's analog vector (angle + magnitude) to X/Y
        /// velocities. Rim (|component| = 1) = that axis's slider speed; centre = stop.
        /// Releasing the puck re-centres it → (0,0) → stop. No deadman — holding the
        /// mouse is the intent; releasing halts.
        /// </summary>
        private void TickOnScreen()
        {
            bool allow = _drivesEnabled && !_busy && _motion != null;
            PointF v = allow ? joystickPad.Vector : PointF.Empty;
            int vx = (int)Math.Round(v.X * _axisRows[AxisId.X].Speed.Value);
            int vy = (int)Math.Round(v.Y * _axisRows[AxisId.Y].Speed.Value);
            ApplyVector(vx, vy);
            joystickStatusLabel.Text = (vx != 0 || vy != 0) ? $"On-screen: {vx}, {vy}" : "On-screen: idle";
        }

        /// <summary>
        /// Commands one axis from the joystick, but only when the command changes
        /// (send-on-change). Speed comes from that axis's slider; the Fast button
        /// multiplies it (capped at the slider max).
        /// </summary>
        private void ApplyJoy(AxisId id, int dir, bool fast)
        {
            if (dir != 0 && IsJogBlocked(id, dir)) dir = 0; // soft limit -> treat as stop
            (int dir, bool fast) last = _lastJoy[id];
            if (last.dir == dir && (dir == 0 || last.fast == fast)) return; // unchanged
            try
            {
                if (dir == 0)
                {
                    _motion!.Stop(id);
                    _cmdDir[id] = 0;
                }
                else
                {
                    int speed = _axisRows[id].Speed.Value;
                    if (fast) speed = Math.Min(speed * FAST_FACTOR, _axisRows[id].Speed.Maximum);
                    _motion!.JogAt(id, dir, speed);
                    _cmdDir[id] = dir;
                }
            }
            catch (ChuckException ex) { AppendLog($"ERROR: joystick {id}: {ex.Message}"); }
            _lastJoy[id] = (dir, fast);
        }

        // --- XY velocity-vector jog (on-screen joystick) --------------------------

        /// <summary>
        /// Commands the XY pair as a velocity vector (on-screen joystick), send-on-change.
        /// Vx/Vy are signed drive-velocity units; the geometric heading is exact only if
        /// X and Y share the same units/scale (true once factor-group scaling is wired in).
        /// </summary>
        private void ApplyVector(int vx, int vy)
        {
            try
            {
                if (vx != _lastVx) { CommandVel(AxisId.X, vx); _lastVx = vx; }
                if (vy != _lastVy) { CommandVel(AxisId.Y, vy); _lastVy = vy; }
            }
            catch (ChuckException ex) { AppendLog($"ERROR: on-screen jog: {ex.Message}"); }
        }

        private void CommandVel(AxisId id, int v)
        {
            if (v != 0 && IsJogBlocked(id, Math.Sign(v))) v = 0; // soft limit -> treat as stop
            if (v == 0) { _motion!.Stop(id); _cmdDir[id] = 0; }
            else { _motion!.JogAt(id, Math.Sign(v), Math.Abs(v)); _cmdDir[id] = Math.Sign(v); }
        }

        /// <summary>Stops any axis the joystick was driving and clears its last-command cache.</summary>
        private void StopJoyAxes()
        {
            if (_motion != null)
            {
                foreach (AxisId id in _motion.Axes)
                {
                    if (_lastJoy[id].dir != 0)
                        try { _motion.Stop(id); } catch (ChuckException) { }
                }
                if (_lastVx != 0) try { _motion.Stop(AxisId.X); } catch (ChuckException) { }
                if (_lastVy != 0) try { _motion.Stop(AxisId.Y); } catch (ChuckException) { }
            }
            ResetJoy();
        }

        private void ResetJoy()
        {
            foreach (AxisId id in new List<AxisId>(_lastJoy.Keys)) _lastJoy[id] = (0, false);
            _lastVx = _lastVy = 0;
        }

        // --- Parameter readout (power-cycle verification) -------------------------
        // Read-only sweep of every connected drive's key objects. Writes nothing, so
        // it can't disturb the values it reads. Reads from the live connection.

        private async void readParamsButton_Click(object? sender, EventArgs e)
        {
            if (!_connection.IsConnected) return;

            _busy = true;
            RefreshButtons();
            AppendLog("=== Read drive parameters (read-only; writes nothing) ===");

            statusTimer.Stop();
            joystickTimer.Stop();
            await Task.Run(() =>
            {
                try
                {
                    for (int i = 0; i < _connection.Handles.Count; i++)
                    {
                        DeviceIdentity id = _connection.Devices[i];
                        string axis = TableAxes.NameForBusPosition(id.BusPosition);
                        _log.Report($"--- [pos {id.BusPosition}] AXIS {axis}  ({id.Name})  serial={id.Serial}  fw={id.Firmware} ---");

                        _log.Report("  Protection / motor limits:");
                        foreach (ParameterReadout p in DriveDiagnostics.ReadLimits(_connection.Accessor!, _connection.Handles[i]))
                            _log.Report("    " + p);

                        _log.Report("  Units & scaling (defines position/velocity units):");
                        foreach (ParameterReadout p in DriveDiagnostics.ReadUnitsScaling(_connection.Accessor!, _connection.Handles[i]))
                            _log.Report("    " + p);
                    }
                }
                catch (Exception ex)
                {
                    _log.Report($"ERROR during parameter read: {ex.Message}");
                }
            });
            AppendLog("=== Done. Re-run after a power cycle and compare to confirm NV persistence. ===");

            _busy = false;
            RestartTimers();
            RefreshButtons();
        }

        // --- Live status poll -----------------------------------------------------

        private void statusTimer_Tick(object? sender, EventArgs e)
        {
            if (_motion == null) return;
            try
            {
                foreach (AxisId id in _motion.Axes)
                {
                    ChuckController.ChuckStatus st = _motion.GetStatus(id);
                    _axisRows[id].Status.Text = $"{st.Position,12:N0}   {st.State}{(st.HasFault ? "  [FAULT]" : "")}";
                    EnforceSoftLimits(id, st.Position);
                }
                _statusFailures = 0;
            }
            catch (ChuckException ex)
            {
                _statusFailures++;
                if (_statusFailures >= MAX_CONSECUTIVE_READ_FAILURES)
                {
                    statusTimer.Stop();
                    joystickTimer.Stop();
                    AppendLog($"Lost contact with a drive: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Stops an axis that is jogging past one of its stored digital limits. Direction is
        /// inferred from the position delta (so it's independent of motor/encoder polarity):
        /// a stop fires only when the axis is at/beyond a limit AND still moving further out,
        /// so jogging back into range is always allowed. Send-on-change keeps it stopped
        /// (a held button/stick won't re-command) until the user reverses. Runs at the
        /// ~200 ms status-poll rate, so there is some overshoot — the physical switches
        /// (where present) remain the real safety; this is a soft guard.
        /// </summary>
        private void EnforceSoftLimits(AxisId id, long pos)
        {
            bool hasPrev = _prevPos.TryGetValue(id, out long prev);
            _prevPos[id] = pos;
            if (!_drivesEnabled || !hasPrev) { _atSoftLimit.Remove(id); _limitBlockedDir[id] = 0; return; }

            AxisCalibration cal = _calib.For(id);
            long delta = pos - prev;
            bool outMax = cal.Max.HasValue && pos >= cal.Max.Value;
            bool outMin = cal.Min.HasValue && pos <= cal.Min.Value;

            if ((outMax && delta > 0) || (outMin && delta < 0))
            {
                try { _motion!.Stop(id); } catch (ChuckException) { }
                // Refuse further jogs in the SAME command direction that pushed it out, so a
                // held/re-pressed control can't re-lurch past the limit each poll. Reversing
                // (back into range) clears the block below.
                if (_cmdDir.TryGetValue(id, out int d) && d != 0) _limitBlockedDir[id] = d;
                _cmdDir[id] = 0;
                if (_atSoftLimit.Add(id))   // log once per approach, not every poll
                    AppendLog($"{id} soft {(outMax ? "Max" : "Min")} limit reached - jog stopped at {pos:N0}.");
            }
            else if (!outMax && !outMin)
            {
                _atSoftLimit.Remove(id);    // safely back inside the range
                _limitBlockedDir[id] = 0;   // re-allow both directions
            }
        }

        /// <summary>Clears soft-limit tracking so a stale position delta can't trigger a false stop.</summary>
        private void ResetSoftLimitTracking()
        {
            _prevPos.Clear();
            _atSoftLimit.Clear();
            _cmdDir.Clear();
            _limitBlockedDir.Clear();
        }

        // --- Calibration (separate window) ----------------------------------------
        // FrmMain owns the calibration store, all motion, and timer coordination; the
        // window (FrmCalibration) is pure UI that calls the internal methods below.

        private void calibButton_Click(object? sender, EventArgs e)
        {
            if (_calibWindow == null || _calibWindow.IsDisposed)
                _calibWindow = new FrmCalibration(this);
            _calibWindow.Show();
            _calibWindow.BringToFront();
        }

        private async void homeAllButton_Click(object? sender, EventArgs e) => await HomeAllAsync();

        /// <summary>
        /// Universal home: brings Z to its home FIRST (e.g. retract to a safe height) and
        /// waits for it, THEN moves X and Y to their homes simultaneously. Requires a home
        /// target for all three; aborts otherwise so X/Y never move before Z has retracted.
        /// </summary>
        internal async Task HomeAllAsync()
        {
            if (!CanMoveCalibration) return;
            long? zT = HomeTargetFor(AxisId.Z);
            long? xT = HomeTargetFor(AxisId.X);
            long? yT = HomeTargetFor(AxisId.Y);
            if (zT == null || xT == null || yT == null)
            {
                AppendLog("Home All: set Home for X, Y and Z first " +
                          $"(missing:{(xT == null ? " X" : "")}{(yT == null ? " Y" : "")}{(zT == null ? " Z" : "")}).");
                return;
            }

            int zSpd = HomeSpeedFor(AxisId.Z);
            int xSpd = HomeSpeedFor(AxisId.X);
            int ySpd = HomeSpeedFor(AxisId.Y);

            _busy = true; RefreshButtons();
            AppendLog($"Home All: Z → {zT.Value:N0} first, then X → {xT.Value:N0} & Y → {yT.Value:N0} together...");
            bool ok = await RunDriveOp(() =>
            {
                // 1) Z home first. ABORT before moving the table if Z does not actually
                //    arrive, so X/Y never traverse while Z is still down (collision).
                _motion!.MoveAbsolute(AxisId.Z, zT.Value, zSpd);
                if (!_motion.WaitForMotionComplete(AxisId.Z, FIND_TIMEOUT_MS))
                    throw new ChuckException("Z did not reach Home in time - aborting before X/Y move.");
                // 2) X and Y together: issue both moves, then wait for both to finish.
                _motion.MoveAbsolute(AxisId.X, xT.Value, xSpd);
                _motion.MoveAbsolute(AxisId.Y, yT.Value, ySpd);
                _motion.WaitForMotionComplete(AxisId.X, FIND_TIMEOUT_MS);
                _motion.WaitForMotionComplete(AxisId.Y, FIND_TIMEOUT_MS);
            });
            AppendLog(ok ? "Home All complete." : "Home All FAILED - see error above.");
            _busy = false; RestartTimers(); RefreshButtons();
        }

        /// <summary>
        /// Moves the table to manually-entered coordinates. Each axis is optional (blank =
        /// leave it where it is); entered values are validated against that axis's Min/Max
        /// limits and the WHOLE move is rejected if any is out of range. The entered axes
        /// move together. Coordinates are in the same drive units shown as Min/Max.
        /// </summary>
        internal async Task MoveToAsync(string xText, string yText, string zText)
        {
            if (!CanMoveCalibration) return;

            bool okX = TryCoord(AxisId.X, xText, out long? xTarget);
            bool okY = TryCoord(AxisId.Y, yText, out long? yTarget);
            bool okZ = TryCoord(AxisId.Z, zText, out long? zTarget);
            if (!okX || !okY || !okZ) return;   // bad number(s) already logged

            var targets = new List<(AxisId id, long pos)>();
            var errors = new List<string>();
            void Plan(AxisId id, long? val)
            {
                if (val == null) return;
                AxisCalibration cal = _calib.For(id);
                if (cal.Min.HasValue && val.Value < cal.Min.Value)
                    errors.Add($"{id} {val.Value:N0} < Min {cal.Min.Value:N0}");
                else if (cal.Max.HasValue && val.Value > cal.Max.Value)
                    errors.Add($"{id} {val.Value:N0} > Max {cal.Max.Value:N0}");
                else
                {
                    targets.Add((id, val.Value));
                    if (!cal.Min.HasValue || !cal.Max.HasValue)
                        AppendLog($"Note: {id} has no full limit range set - move not bounds-checked.");
                }
            }
            Plan(AxisId.X, xTarget); Plan(AxisId.Y, yTarget); Plan(AxisId.Z, zTarget);

            if (errors.Count > 0)
            {
                AppendLog("Move cancelled - out of range: " + string.Join("; ", errors));
                return;
            }
            if (targets.Count == 0) { AppendLog("Move: enter at least one coordinate."); return; }

            string desc = "";
            foreach ((AxisId id, long pos) t in targets)
                desc += (desc.Length > 0 ? ", " : "") + $"{t.id}={t.pos:N0}";

            _busy = true; RefreshButtons();
            AppendLog($"Move to: {desc}...");
            bool ok = await RunDriveOp(() =>
            {
                foreach ((AxisId id, long pos) t in targets) _motion!.MoveAbsolute(t.id, t.pos, HomeSpeedFor(t.id));
                foreach ((AxisId id, long pos) t in targets) _motion!.WaitForMotionComplete(t.id, FIND_TIMEOUT_MS);
            });
            AppendLog(ok ? "Move complete." : "Move FAILED - see error above.");
            _busy = false; RestartTimers(); RefreshButtons();
        }

        // Parses one optional coordinate field: blank → null (skip axis), valid → the value;
        // returns false (and logs) on a non-empty, unparseable entry.
        private bool TryCoord(AxisId id, string text, out long? val)
        {
            val = null;
            text = text?.Trim() ?? "";
            if (text.Length == 0) return true;
            if (long.TryParse(text, out long v)) { val = v; return true; }
            AppendLog($"Move: '{text}' is not a valid {id} coordinate.");
            return false;
        }

        /// <summary>The shared per-axis limits/home store (read by the calibration window).</summary>
        internal CalibrationStore Calibration => _calib;

        /// <summary>Reading a position needs the link up; capture is a UI-thread SDO read.</summary>
        internal bool CanCaptureCalibration => _connection.IsConnected && !_busy && _motion != null;

        /// <summary>Motion (find / go-home) needs the drives enabled and idle.</summary>
        internal bool CanMoveCalibration => _drivesEnabled && !_busy && _motion != null;

        /// <summary>Home target for an axis: centre of the limits for X/Y, explicit Home for Z.</summary>
        internal long? HomeTargetFor(AxisId id)
        {
            AxisCalibration c = _calib.For(id);
            return id == AxisId.Z ? c.Home : c.Center;
        }

        internal void SetCalibrationMin(AxisId id) => CaptureInto(id, isMax: false, isHome: false);
        internal void SetCalibrationMax(AxisId id) => CaptureInto(id, isMax: true, isHome: false);
        internal void SetCalibrationHome(AxisId id) => CaptureInto(id, isMax: false, isHome: true);

        private void CaptureInto(AxisId id, bool isMax, bool isHome)
        {
            if (!CanCaptureCalibration) return;
            long pos;
            try { pos = _motion!.GetStatus(id).Position; }
            catch (ChuckException ex) { AppendLog($"ERROR: read {id} position: {ex.Message}"); return; }

            AxisCalibration c = _calib.For(id);
            if (isHome) { c.Home = pos; AppendLog($"{id} Home set to {pos:N0}."); }
            else if (isMax) { c.Max = pos; AppendLog($"{id} Max limit set to {pos:N0}."); }
            else { c.Min = pos; AppendLog($"{id} Min limit set to {pos:N0}."); }
            TrySaveCalibration();
        }

        /// <summary>Moves an axis to its Home target (Profile Position) and waits for arrival.</summary>
        internal async Task GoHomeAsync(AxisId id)
        {
            if (!CanMoveCalibration) return;
            long? target = HomeTargetFor(id);
            if (target == null) { AppendLog($"{id}: set its limits/Home first."); return; }

            int speed = HomeSpeedFor(id);
            _busy = true; RefreshButtons();
            AppendLog($"Go Home {id} → target {target.Value:N0} at {speed}...");
            long before = 0, after = 0;
            bool reached = false;
            bool ok = await RunDriveOp(() =>
            {
                before = _motion!.GetStatus(id).Position;
                _motion.MoveAbsolute(id, target.Value, speed);
                reached = _motion.WaitForMotionComplete(id, FIND_TIMEOUT_MS);
                after = _motion.GetStatus(id).Position;
            });
            if (!ok)
                AppendLog($"{id} Go Home FAILED - see error above.");
            else
                AppendLog($"{id} Go Home: was {before:N0} → now {after:N0} (target {target.Value:N0}, " +
                          $"off by {after - target.Value:N0}){(reached ? "" : " [target-reached never set]")}" +
                          $"{(before == after ? "  *** axis did not move ***" : "")}.");
            _busy = false; RestartTimers(); RefreshButtons();
        }

        /// <summary>
        /// Auto-finds an axis's two travel limits by jogging into each switch, recording
        /// the position at the edge, and taking the pair as Min/Max (Home = centre).
        /// Only Y is wired to this today (two working switches that quick-stop).
        /// </summary>
        internal async Task FindLimitsAsync(AxisId id)
        {
            if (!CanMoveCalibration) return;
            _busy = true; RefreshButtons();
            statusTimer.Stop(); joystickTimer.Stop();
            AppendLog($"Finding {id} limits (auto, speed {FIND_LIMIT_SPEED})...");
            try
            {
                (long a, long b) = await Task.Run(() => FindLimitsCore(id));
                AxisCalibration c = _calib.For(id);
                c.Min = Math.Min(a, b);
                c.Max = Math.Max(a, b);
                TrySaveCalibration();
                AppendLog($"{id} limits: Min={c.Min:N0}, Max={c.Max:N0}, Home(centre)={c.Center:N0}.");
            }
            catch (Exception ex)
            {
                AppendLog($"Find {id} limits FAILED: {ex.Message}");
            }
            _busy = false; RestartTimers(); RefreshButtons();
        }

        // Background worker: jog to one end, recover + back off, jog to the other end.
        private (long endA, long endB) FindLimitsCore(AxisId id)
        {
            ClearAnyActiveLimit(id);              // axis may start parked on a switch
            long endA = JogUntilLimit(id, +1);
            RecoverAndBackOff(id, awayDir: -1);
            long endB = JogUntilLimit(id, -1);
            RecoverAndBackOff(id, awayDir: +1);   // leave the axis off the switch
            return (endA, endB);
        }

        // If the axis already sits on a limit switch when the find starts, JogUntilLimit
        // would never see a NEWLY-set bit and would run its full timeout driving into the
        // switch. Back off first. Polarity is unverified, so we don't know which way is
        // "away": try one direction and, if that drove further in (drive quick-stops, bit
        // stays set), recover and try the other.
        private void ClearAnyActiveLimit(AxisId id)
        {
            if ((_motion!.GetDigitalInputs(id) & SW_LIMIT_BITS) == 0) return;
            AppendLog($"{id} starts on a limit switch - backing off before find...");
            foreach (int away in new[] { -1, +1 })
            {
                _motion[id].EnableDrive(true);   // exit Quick Stop if a switch parked it there
                if ((_motion.GetDigitalInputs(id) & SW_LIMIT_BITS) == 0) return;
                _motion.JogAt(id, away, FIND_LIMIT_SPEED);
                int waited = 0;
                while ((_motion.GetDigitalInputs(id) & SW_LIMIT_BITS) != 0 && waited < BACKOFF_TIMEOUT_MS)
                {
                    System.Threading.Thread.Sleep(FIND_POLL_MS);
                    waited += FIND_POLL_MS;
                }
                _motion.Stop(id);
                if ((_motion.GetDigitalInputs(id) & SW_LIMIT_BITS) == 0) return;
            }
            throw new ChuckException($"{id} starts on a limit switch and could not be backed off either way.");
        }

        // Jogs until a limit bit (0x60FD bit 0 or 1) that wasn't already set goes high,
        // then captures the position and stops. Direction-agnostic, so the NEG/POS wiring
        // swap doesn't matter. Throws on timeout (no limit seen).
        private long JogUntilLimit(AxisId id, int direction)
        {
            long baseline = _motion!.GetDigitalInputs(id) & SW_LIMIT_BITS;
            _motion.JogAt(id, direction, FIND_LIMIT_SPEED);
            int waited = 0;
            while (waited < FIND_TIMEOUT_MS)
            {
                long now = _motion.GetDigitalInputs(id) & SW_LIMIT_BITS;
                if ((now & ~baseline) != 0)
                {
                    long pos = _motion.GetStatus(id).Position;
                    _motion.Stop(id);
                    return pos;
                }
                System.Threading.Thread.Sleep(FIND_POLL_MS);
                waited += FIND_POLL_MS;
            }
            _motion.Stop(id);
            throw new ChuckException($"no limit detected within {FIND_TIMEOUT_MS / 1000}s");
        }

        // After a limit hit the drive is in Quick Stop Active; re-enable to Operation
        // Enabled, then jog clear of the switch so the next pass starts off the limit.
        private void RecoverAndBackOff(AxisId id, int awayDir)
        {
            _motion![id].EnableDrive(true);
            if ((_motion.GetDigitalInputs(id) & SW_LIMIT_BITS) == 0) return;

            _motion.JogAt(id, awayDir, FIND_LIMIT_SPEED);
            int waited = 0;
            while ((_motion.GetDigitalInputs(id) & SW_LIMIT_BITS) != 0 && waited < FIND_TIMEOUT_MS)
            {
                System.Threading.Thread.Sleep(FIND_POLL_MS);
                waited += FIND_POLL_MS;
            }
            _motion.Stop(id);
        }

        private void TrySaveCalibration()
        {
            try { _calib.Save(); }
            catch (Exception ex) { AppendLog($"WARN: calibration save failed: {ex.Message}"); }
        }

        // --- Helpers --------------------------------------------------------------

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
            _moveButton.Enabled = !_busy && conn && _drivesEnabled;
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
    }
}
