using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NanotecController
{
    // FrmMain — owns all calibration motion and persistence (the FrmCalibration window is
    // pure UI that calls these internal methods): Home All, Move To, Set Min/Max/Home
    // capture, Go Home, and the unified X+Y auto limit-find. FrmMain is the single owner
    // because NanoLib is single-channel and access must be serialized. (Partial of FrmMain.)
    //
    // Self-contained: the calibration store, the fixed home/find speeds and the poll/timeout
    // ceilings are all declared at the top of this file, not in FrmMain.cs.
    public partial class FrmMain
    {
        // --- Tuning constants -----------------------------------------------------
        // Everything the calibration motion is tuned by lives here rather than in FrmMain.cs,
        // so a speed or ceiling can be changed without leaving the file that uses it.

        // Speed for the automatic limit-find (X, Y), in drive velocity units. Kept low so the
        // approach into the switch is gentle; the edge POSITION is captured the moment the bit
        // sets, so physical overshoot doesn't bias the stored limit, and it cancels in the
        // centre calc anyway. It still matters mechanically on X, which (0x3701 = -1) does not
        // quick-stop itself and has a gentler decel ramp than Y, so it coasts further past the
        // switch before this loop's Stop takes effect.
        private const int FIND_LIMIT_SPEED = 5000;
        // Limit-find polling + ceilings.
        private const int FIND_POLL_MS = 15;
        // Per end: fail rather than run forever. Doubles as the arrival ceiling for every
        // preplanned move in the app (Home All, Move To, Go Home, the relative Θ move) — one
        // generous wall-clock bound, since none of them should ever take a minute.
        private const int FIND_TIMEOUT_MS = 60000;
        private const int BACKOFF_TIMEOUT_MS = 5000; // backing off a switch should be quick
        private const long SW_LIMIT_BITS = 0x3;      // 0x60FD bits 0 (neg) + 1 (pos)

        // Fixed velocities for Go Home / Home All (drive velocity units) — NOT the runtime
        // jog sliders, so homing is always at a known, repeatable speed.
        private const int HOME_SPEED_X = 5000;
        private const int HOME_SPEED_Y = 5000;
        private const int HOME_SPEED_Z = 400;

        private static int HomeSpeedFor(AxisId id) => id switch
        {
            AxisId.X => HOME_SPEED_X,
            AxisId.Y => HOME_SPEED_Y,
            AxisId.Z => HOME_SPEED_Z,
            _ => HOME_SPEED_Z,   // conservative default (Theta is never homed)
        };

        // Per-axis travel limits + Home, persisted to disk (survives restarts). Owned here (the
        // Calibration property below is its only public surface) but read by most of the other
        // partials — the vision affine, chuck centre and rotation handedness live in it too.
        // Assigned in the FrmMain ctor, which can log a load failure.
        private readonly CalibrationStore _calib;

        // --- Calibration (chooser → two separate windows) -------------------------
        // The button now offers a choice: the axis travel-limits/home window, or the vision
        // camera-scale + centre-find window (which is fed the main-screen camera).

        private ContextMenuStrip? _calibMenu;
        private FrmCalibration? _calibWindow;
        private FrmVisionProtocols? _visionProtocolsFromCalib;

        private void calibButton_Click(object? sender, EventArgs e)
        {
            _calibMenu ??= BuildCalibMenu();
            _calibMenu.Show(calibButton, new Point(0, calibButton.Height));
        }

        private ContextMenuStrip BuildCalibMenu()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Axes — travel limits && home…", null, (s, e) =>
            {
                if (_calibWindow == null || _calibWindow.IsDisposed)
                    _calibWindow = new FrmCalibration(this);
                _calibWindow.Show();
                _calibWindow.BringToFront();
            });
            menu.Items.Add("Vision — camera scale && centres…", null, (s, e) =>
            {
                ShowVisionProtocols();
            });
            menu.Items.Add(new ToolStripSeparator());
            // The third item is not a window — it RUNS the two auto routines the first two windows
            // expose, back to back, which is the whole cold-start: undefined stage → limits and Home
            // measured → chuck centred.
            menu.Items.Add("Home && centre chuck (auto)", null, async (s, e) =>
            {
                await HomeAndCentreChuckAsync();
            });
            return menu;
        }

        // Opens (or re-focuses) the single vision window instance. Shared by the menu item and the
        // unified chain below, which needs the window up so the operator can read its run log.
        private FrmVisionProtocols ShowVisionProtocols()
        {
            if (_visionProtocolsFromCalib == null || _visionProtocolsFromCalib.IsDisposed)
                _visionProtocolsFromCalib = new FrmVisionProtocols(this, _visionView) { Owner = this };
            _visionProtocolsFromCalib.Show();
            _visionProtocolsFromCalib.BringToFront();
            return _visionProtocolsFromCalib;
        }

        /// <summary>
        /// The unified cold start: the auto X+Y limit-find (which ends by sending both axes to the
        /// Home it just measured), then the automatic chuck centre-find.
        ///
        /// This only ORDERS the two runs — both are the same entry points their own windows call, so
        /// each keeps its own preconditions, confirmation, motion guards and logging. In particular
        /// the centre-find re-checks that X and Y have a Home and refuses if they do not, so a
        /// limit-find that measured nothing cannot roll on into a scan from an undefined start.
        /// </summary>
        private async Task HomeAndCentreChuckAsync()
        {
            if (!CanMoveCalibration) return;

            if (MessageBox.Show(this,
                    "Home & centre chuck — the full automatic sequence.\r\n\r\n" +
                    "1. Find X + Y limits: drives BOTH axes into their end switches, stores Min/Max, " +
                    "then sends them to the new Home (the centre of that travel).\r\n\r\n" +
                    "2. Automatic chuck centre-find: probes the chuck edge in 8 directions and leaves " +
                    "the stage sitting on the fitted centre.\r\n\r\n" +
                    "Step 2 asks for its own confirmation before it moves, and needs the camera " +
                    "streaming, the camera-scale calibration done, and Z focused on the chuck edge.\r\n\r\n" +
                    "Confirm the X/Y path is clear, then proceed?",
                    "Home & centre chuck", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            await FindXyLimitsAsync();

            // A STOP during the limit-find must not be followed by another unrequested traverse —
            // the same rule FindXyLimitsAsync applies to its own auto-home step.
            if (_stopRequested)
            {
                AppendLog("Home & centre: chuck centre-find SKIPPED (the limit-find was stopped).");
                return;
            }

            AppendLog("Home & centre: limit-find done, starting the automatic chuck centre-find...");
            await ShowVisionProtocols().RunAutoCentreFromHostAsync();
        }

        private async void homeAllButton_Click(object? sender, EventArgs e) => await HomeAllAsync();

        // --- Position Map (separate window: XY grid + numeric X/Y/Z + Go) ----------
        // The window (FrmPosition) is pure UI; it reads the position/limits FrmMain exposes
        // in the USER frame and executes through the existing MoveToAsync. The FrmMain side
        // lives here (alongside MoveToAsync and the calibration store it shares).

        private FrmPosition? _posWindow;
        private Button positionButton = null!;

        // Last polled RAW position per axis (written in statusTimer_Tick); read in the USER
        // frame via TryCurrentUser. Cleared with the soft-limit tracking so no stale dot shows.
        private readonly Dictionary<AxisId, long> _lastPos = new();

        /// <summary>Builds the "Position Map..." open-button (replaces the old inline Move-To console).</summary>
        private void BuildPositionButton()
        {
            positionButton = new Button
            {
                Text = "Position Map...",
                Location = new Point(694, 452),
                Size = new Size(168, 40),
                Enabled = false,
            };
            positionButton.Click += positionButton_Click;
            leftPanel.Controls.Add(positionButton);
        }

        private void positionButton_Click(object? sender, EventArgs e)
        {
            if (_posWindow == null || _posWindow.IsDisposed)
                _posWindow = new FrmPosition(this);
            _posWindow.Show();
            _posWindow.BringToFront();
        }

        // User frame ↔ raw drive frame. Y reads/enters inverted (user +Y = raw −Y) so the on-screen
        // position is intuitive; every other axis is identity. SINGLE SOURCE of the Y-sign convention.
        // The one deliberate exception is the vision-jog Y command (FrmMain.Vision.cs), whose sign is
        // empirical. Both directions are the same negation (an involution); the two names document, at
        // each call site, whether a user-frame or a raw-frame value is being produced.
        private static long ToUser(AxisId id, long raw) => id == AxisId.Y ? -raw : raw;
        private static long ToRaw(AxisId id, long user) => id == AxisId.Y ? -user : user;

        /// <summary>Current position of an axis in the USER frame (Y inverted), if polled yet.</summary>
        public bool TryCurrentUser(AxisId id, out long user)
        {
            if (_lastPos.TryGetValue(id, out long raw)) { user = ToUser(id, raw); return true; }
            user = 0;
            return false;
        }

        /// <summary>
        /// Reads X and Y from the drives RIGHT NOW (USER frame), bypassing the <see cref="_lastPos"/>
        /// cache, and refreshes that cache. The cache is fed by statusTimer, which RunDriveOp stops for
        /// the duration of every drive op and only restarts on BusyScope.Dispose — so for at least one
        /// timer period after an awaited move it still holds the PRE-MOVE position. The manual
        /// centre-find never noticed (the operator clicks with the stage long parked), but the auto
        /// centre-find pairs a position with a freshly-grabbed frame: a stale M would build every rim
        /// point E = M + A·(p_cross − p_edge) against the wrong place. False if the link is down, an
        /// op is in progress, or the read fails.
        /// </summary>
        public bool TryReadUserXyNow(out long x, out long y)
        {
            x = y = 0;
            if (!CanCaptureCalibration) return false;
            try
            {
                long rawX = _motion!.GetStatus(AxisId.X).Position;
                long rawY = _motion.GetStatus(AxisId.Y).Position;
                _lastPos[AxisId.X] = rawX;
                _lastPos[AxisId.Y] = rawY;
                x = ToUser(AxisId.X, rawX);
                y = ToUser(AxisId.Y, rawY);
                return true;
            }
            catch (DriveException ex)
            {
                AppendLog($"ERROR: read X/Y position: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// An axis's travel limits in the USER frame, or null if both ends aren't set. The Y
        /// negation flips min/max order, so they're re-sorted before returning.
        /// </summary>
        public (long min, long max)? UserLimits(AxisId id)
        {
            AxisCalibration c = _calib.For(id);
            if (!c.Min.HasValue || !c.Max.HasValue) return null;
            long a = ToUser(id, c.Min.Value);
            long b = ToUser(id, c.Max.Value);
            return (Math.Min(a, b), Math.Max(a, b));
        }

        /// <summary>
        /// Universal home: brings Z to its home FIRST (e.g. retract to a safe height) and
        /// waits for it, THEN moves X and Y to their homes simultaneously. Requires a home
        /// target for all three; aborts otherwise so X/Y never move before Z has retracted.
        /// </summary>
        public async Task HomeAllAsync()
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

            using var busyScope = BeginBusy();
            AppendLog($"Home All: Z → {zT.Value:N0} first, then X → {xT.Value:N0} & Y → {yT.Value:N0} together...");
            bool ok = await RunDriveOp(() =>
            {
                // 1) Z home first. ABORT before moving the table if Z does not actually
                //    arrive, so X/Y never traverse while Z is still down (collision).
                _motion!.RecoverIfQuickStopped(AxisId.Z);   // clear a quick stop so the move runs
                _motion.MoveAbsolute(AxisId.Z, zT.Value, zSpd);
                if (!WaitOrStop(AxisId.Z, FIND_TIMEOUT_MS))
                    throw new DriveException("Z did not reach Home in time - aborting before X/Y move.");
                // 2) X and Y together: issue both moves, then wait for both to finish.
                _motion.RecoverIfQuickStopped(AxisId.X);
                _motion.RecoverIfQuickStopped(AxisId.Y);
                _motion.MoveAbsolute(AxisId.X, xT.Value, xSpd);
                _motion.MoveAbsolute(AxisId.Y, yT.Value, ySpd);
                WaitOrStop(AxisId.X, FIND_TIMEOUT_MS);
                WaitOrStop(AxisId.Y, FIND_TIMEOUT_MS);
            });
            AppendLog(ok ? "Home All complete." : "Home All FAILED - see error above.");
        }

        /// <summary>
        /// Moves the table to manually-entered coordinates. Each axis is optional (blank =
        /// leave it where it is); entered values are validated against that axis's Min/Max
        /// limits and the WHOLE move is rejected if any is out of range. The entered axes
        /// move together. Coordinates are in the same drive units shown as Min/Max.
        /// </summary>
        public async Task MoveToAsync(string xText, string yText, string zText)
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

            using var busyScope = BeginBusy();
            AppendLog($"Move to: {desc}...");
            bool ok = await RunDriveOp(() =>
            {
                foreach ((AxisId id, long pos) t in targets) { _motion!.RecoverIfQuickStopped(t.id); _motion.MoveAbsolute(t.id, t.pos, HomeSpeedFor(t.id)); }
                foreach ((AxisId id, long pos) t in targets) WaitOrStop(t.id, FIND_TIMEOUT_MS);
            });
            AppendLog(ok ? "Move complete." : "Move FAILED - see error above.");
        }

        // Parses one optional coordinate field: blank → null (skip axis), valid → the value;
        // returns false (and logs) on a non-empty, unparseable entry.
        private bool TryCoord(AxisId id, string text, out long? val)
        {
            val = null;
            text = text?.Trim() ?? "";
            if (text.Length == 0) return true;
            if (long.TryParse(text, out long v)) { val = ToRaw(id, v); return true; }   // user → raw (Y inverted) for intuitive entry
            
            AppendLog($"Move: '{text}' is not a valid {id} coordinate.");
            return false;
        }

        /// <summary>The shared per-axis limits/home store (read by the calibration window).</summary>
        public CalibrationStore Calibration => _calib;

        /// <summary>Reading a position needs the link up; capture is a UI-thread SDO read.</summary>
        public bool CanCaptureCalibration => _connection.IsConnected && !_busy && _motion != null;

        /// <summary>Motion (find / go-home) needs the drives enabled and idle.</summary>
        public bool CanMoveCalibration => _drivesEnabled && !_busy && _motion != null;

        /// <summary>Home target for an axis: centre of the limits for X/Y, explicit Home for Z.</summary>
        public long? HomeTargetFor(AxisId id)
        {
            AxisCalibration c = _calib.For(id);
            return id == AxisId.Z ? c.Home : c.Center;
        }

        public void SetCalibrationMin(AxisId id) => CaptureInto(id, isMax: false, isHome: false);
        public void SetCalibrationMax(AxisId id) => CaptureInto(id, isMax: true, isHome: false);
        public void SetCalibrationHome(AxisId id) => CaptureInto(id, isMax: false, isHome: true);

        public void ClearCalibrationMin(AxisId id) => ClearLimit(id, isMax: false);
        public void ClearCalibrationMax(AxisId id) => ClearLimit(id, isMax: true);

        // Removes a stored soft limit (sets it back to "none") and persists. Local edit only —
        // touches no drive. Also drops any soft-limit jog block for the axis, since the limit
        // that produced it is gone.
        private void ClearLimit(AxisId id, bool isMax)
        {
            AxisCalibration c = _calib.For(id);
            if (isMax) { c.Max = null; AppendLog($"{id} Max limit cleared."); }
            else { c.Min = null; AppendLog($"{id} Min limit cleared."); }
            TrySaveCalibration();
            _softLimits.ClearAxis(id);
        }

        private void CaptureInto(AxisId id, bool isMax, bool isHome)
        {
            if (!CanCaptureCalibration) return;
            long pos;
            try { pos = _motion!.GetStatus(id).Position; }
            catch (DriveException ex) { AppendLog($"ERROR: read {id} position: {ex.Message}"); return; }

            AxisCalibration c = _calib.For(id);
            if (isHome) { c.Home = pos; AppendLog($"{id} Home set to {pos:N0}."); }
            else if (isMax) { c.Max = pos; AppendLog($"{id} Max limit set to {pos:N0}."); }
            else { c.Min = pos; AppendLog($"{id} Min limit set to {pos:N0}."); }
            TrySaveCalibration();
        }

        /// <summary>Moves an axis to its Home target (Profile Position) and waits for arrival.</summary>
        public async Task GoHomeAsync(AxisId id)
        {
            if (!CanMoveCalibration) return;
            long? target = HomeTargetFor(id);
            if (target == null) { AppendLog($"{id}: set its limits/Home first."); return; }

            int speed = HomeSpeedFor(id);
            using var busyScope = BeginBusy();
            AppendLog($"Go Home {id} → target {target.Value:N0} at {speed}...");
            long before = 0, after = 0;
            bool reached = false;
            bool ok = await RunDriveOp(() =>
            {
                before = _motion!.GetStatus(id).Position;
                _motion.RecoverIfQuickStopped(id);   // clear a limit-induced quick stop so the move runs
                _motion.MoveAbsolute(id, target.Value, speed);
                reached = WaitOrStop(id, FIND_TIMEOUT_MS);
                after = _motion.GetStatus(id).Position;
            });
            if (!ok)
                AppendLog($"{id} Go Home FAILED - see error above.");
            else
                AppendLog($"{id} Go Home: was {before:N0} → now {after:N0} (target {target.Value:N0}, " +
                          $"off by {after - target.Value:N0}){(reached ? "" : " [target-reached never set]")}" +
                          $"{(before == after ? "  *** axis did not move ***" : "")}.");
        }

        /// <summary>Axes whose travel limits can be found automatically — they have a working
        /// switch at each end. Z is excluded: it has no switch at either end, so its limits stay
        /// manual. The auto-find is one unified run over ALL of these axes at once.</summary>
        private static readonly AxisId[] AutoFindAxes = [AxisId.X, AxisId.Y];

        /// <summary>
        /// Auto-finds the travel limits of X and Y in ONE run, with both axes moving at the
        /// SAME time: each jogs into its own switches, the position at each edge is recorded,
        /// and the pair becomes that axis's Min/Max (Home = centre). Detection is host-side
        /// (poll 0x60FD, then Stop), so it does not depend on the drive's own limit reaction:
        /// Y quick-stops at its switches (0x3701 = 6) while X ignores them (0x3701 = -1) and is
        /// stopped by this loop alone.
        ///
        /// The two axes share the single NanoLib channel, so their bus traffic is strictly
        /// serialized (see <see cref="RunLimitFinds"/>) — only the motors overlap. Each axis is
        /// reported and stored independently: one that fails (no switch seen before its timeout)
        /// does not discard the other's measurement.
        ///
        /// The run finishes by homing every axis it calibrated (see <see cref="HomeAfterFindAsync"/>),
        /// so the chuck ends up centred rather than parked at one end of its travel.
        /// </summary>
        public async Task FindXyLimitsAsync()
        {
            if (!CanMoveCalibration) return;
            using var busyScope = BeginBusy();
            statusTimer.Stop(); joystickTimer.Stop();
            AppendLog($"Finding {string.Join(" + ", AutoFindAxes)} limits together (auto, speed {FIND_LIMIT_SPEED})...");

            var finds = new List<AxisLimitFind>();
            foreach (AxisId id in AutoFindAxes) finds.Add(new AxisLimitFind(this, id));
            var failed = new Dictionary<AxisId, string>();
            bool stopped = false;
            try
            {
                // LongRunning, not Task.Run: this parks the worker for the whole find (up to
                // FIND_TIMEOUT_MS per end), which misleads the pool's injection heuristic.
                await Task.Factory.StartNew(() => RunLimitFinds(finds, failed),
                    System.Threading.CancellationToken.None,
                    TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
                    TaskScheduler.Default);
            }
            catch (OperationCanceledException)
            {
                stopped = true;
                AppendLog("Find limits STOPPED by operator.");
            }
            catch (Exception ex)
            {
                AppendLog($"Find limits FAILED: {ex.Message}");
            }

            // Store whatever was measured. Both ends captured = a usable pair, even if the run was
            // stopped or errored afterwards (during the final back-off); the positions were taken
            // the moment each switch set, so nothing later can invalidate them.
            var calibrated = new List<AxisId>();
            foreach (AxisLimitFind find in finds)
            {
                if (failed.TryGetValue(find.Id, out string? why))
                    AppendLog($"{find.Id} limit-find FAILED: {why}");
                if (find.Ends.Count < 2)
                {
                    AppendLog($"{find.Id}: limits NOT updated ({find.Ends.Count} of 2 ends found).");
                    continue;
                }
                AxisCalibration c = _calib.For(find.Id);
                c.Min = Math.Min(find.Ends[0], find.Ends[1]);
                c.Max = Math.Max(find.Ends[0], find.Ends[1]);
                calibrated.Add(find.Id);
                AppendLog($"{find.Id} limits: Min={c.Min:N0}, Max={c.Max:N0}, Home(centre)={c.Center:N0}.");
            }
            if (calibrated.Count == 0) return;
            TrySaveCalibration();

            // An operator STOP ends the whole calibration: it must never be followed by another
            // unrequested traverse. The limits found before the stop are still saved above.
            // _stopRequested is re-checked for a STOP pressed as the find was finishing, which
            // would otherwise start the home move and abort it a fraction of a second later.
            if (stopped || _stopRequested) { AppendLog("Auto-home skipped (calibration was stopped)."); return; }
            await HomeAfterFindAsync(calibrated);
        }

        /// <summary>
        /// Sends the axes a limit-find has just calibrated to their new Home — the centre of the
        /// limits it measured — so the chuck ends the calibration in the middle of its travel
        /// instead of sitting just off an end switch. Both axes move together.
        ///
        /// Unlike <see cref="HomeAllAsync"/> this does NOT retract Z first: the find has, moments
        /// earlier, traversed the full X/Y travel at this very Z height, so a move back to the
        /// centre of that same travel reaches nothing new. Runs inside the caller's busy scope.
        /// </summary>
        private async Task HomeAfterFindAsync(List<AxisId> axes)
        {
            var targets = new List<(AxisId id, long pos)>();
            foreach (AxisId id in axes)
                if (HomeTargetFor(id) is long t) targets.Add((id, t));
            if (targets.Count == 0) return;

            string desc = "";
            foreach ((AxisId id, long pos) t in targets)
                desc += (desc.Length > 0 ? ", " : "") + $"{t.id}={t.pos:N0}";

            AppendLog($"Auto-home after calibration: {desc}...");
            bool ok = await RunDriveOp(() =>
            {
                foreach ((AxisId id, long pos) t in targets)
                {
                    _motion!.RecoverIfQuickStopped(t.id);   // a switch may have left the axis quick-stopped
                    _motion.MoveAbsolute(t.id, t.pos, HomeSpeedFor(t.id));
                }
                foreach ((AxisId id, long pos) t in targets) WaitOrStop(t.id, FIND_TIMEOUT_MS);
            });
            AppendLog(ok ? "Auto-home complete - chuck is at the centre of travel." : "Auto-home FAILED - see error above.");
        }

        // Operator STOP inside a limit-find poll loop: halt the axis and abandon the find.
        private void ThrowIfStopped(AxisId id)
        {
            if (_stopRequested) { _motion!.Stop(id); throw new OperationCanceledException("Limit-find stopped by operator."); }
        }

        // Background worker: steps every axis's find sequence round-robin on THIS one thread.
        // Each step issues its bus traffic and then yields, so the axes take turns on the single
        // NanoLib channel while their motors run at the same time; one sleep per round keeps
        // every phase's millisecond budget wall-clock accurate for all of them. An axis that
        // throws drops out with its reason recorded and the rest carry on; an operator STOP
        // halts every axis and abandons the whole run.
        private void RunLimitFinds(List<AxisLimitFind> finds, Dictionary<AxisId, string> failed)
        {
            var live = new List<(AxisId Id, IEnumerator<bool> Steps)>();
            foreach (AxisLimitFind find in finds) live.Add((find.Id, find.Run().GetEnumerator()));

            void Drop(int i) { live[i].Steps.Dispose(); live.RemoveAt(i); }

            try
            {
                while (live.Count > 0)
                {
                    for (int i = live.Count - 1; i >= 0; i--)
                    {
                        try
                        {
                            if (!live[i].Steps.MoveNext()) Drop(i);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            failed[live[i].Id] = ex.Message;
                            // The failing step stops its own axis before throwing, but a read that
                            // threw mid-jog would leave it under a velocity command.
                            try { _motion!.Stop(live[i].Id); } catch (DriveException) { }
                            Drop(i);
                        }
                    }
                    if (live.Count > 0) System.Threading.Thread.Sleep(FIND_POLL_MS);
                }
            }
            catch (OperationCanceledException)
            {
                _motion!.StopAll();   // halt every axis, not just the one that noticed the Stop
                throw;
            }
            finally
            {
                foreach ((AxisId _, IEnumerator<bool> steps) in live) steps.Dispose();
            }
        }

        /// <summary>
        /// One axis's limit-find, written as a RESUMABLE sequence so several can run at once over
        /// the single NanoLib channel: every step issues its bus traffic and then yields, handing
        /// the channel to the next axis until <see cref="RunLimitFinds"/> comes round again. A
        /// <c>yield return</c> therefore means "I am waiting on the machine — resume me after the
        /// next poll tick", and each phase's timeout counts those ticks.
        /// </summary>
        private sealed class AxisLimitFind
        {
            private readonly FrmMain _f;

            public AxisLimitFind(FrmMain f, AxisId id) { _f = f; Id = id; }

            public AxisId Id { get; }

            /// <summary>The captured position of each end reached, in the order found (so a run cut
            /// short leaves a short list rather than a bogus pair). Two entries = a usable Min/Max.</summary>
            public List<long> Ends { get; } = new();

            private MultiAxisController Motion => _f._motion!;
            private long LimitBits => Motion.GetDigitalInputs(Id) & SW_LIMIT_BITS;
            private bool OnLimit => LimitBits != 0;

            /// <summary>Jog to one end, recover + back off, jog to the other end, back off again.</summary>
            public IEnumerable<bool> Run()
            {
                foreach (bool tick in ClearAnyActiveLimit()) yield return tick;   // may start parked on a switch
                foreach (bool tick in JogUntilLimit(+1)) yield return tick;
                foreach (bool tick in RecoverAndBackOff(awayDir: -1)) yield return tick;
                foreach (bool tick in JogUntilLimit(-1)) yield return tick;
                foreach (bool tick in RecoverAndBackOff(awayDir: +1)) yield return tick;   // leave it off the switch
            }

            // If the axis already sits on a limit switch when the find starts, JogUntilLimit
            // would never see a NEWLY-set bit and would run its full timeout driving into the
            // switch. Back off first. Polarity is unverified, so we don't know which way is
            // "away": try one direction and, if that drove further in (drive quick-stops, bit
            // stays set), recover and try the other.
            private IEnumerable<bool> ClearAnyActiveLimit()
            {
                if (!OnLimit) yield break;
                _f.AppendLog($"{Id} starts on a limit switch - backing off before find...");
                foreach (int away in (int[])[-1, +1])
                {
                    Motion.EnableAxis(Id);   // exit Quick Stop if a switch parked it there
                    if (!OnLimit) yield break;
                    Motion.JogAt(Id, away, FIND_LIMIT_SPEED);
                    int waited = 0;
                    while (OnLimit && waited < BACKOFF_TIMEOUT_MS)
                    {
                        _f.ThrowIfStopped(Id);
                        yield return true;
                        waited += FIND_POLL_MS;
                    }
                    Motion.Stop(Id);
                    if (!OnLimit) yield break;
                }
                throw new DriveException($"{Id} starts on a limit switch and could not be backed off either way.");
            }

            // Jogs until a limit bit (0x60FD bit 0 or 1) that wasn't already set goes high,
            // then records the position and stops. Direction-agnostic, so the NEG/POS wiring
            // swap doesn't matter. Throws on timeout (no limit seen).
            private IEnumerable<bool> JogUntilLimit(int direction)
            {
                long baseline = LimitBits;
                Motion.JogAt(Id, direction, FIND_LIMIT_SPEED);
                int waited = 0;
                while (waited < FIND_TIMEOUT_MS)
                {
                    _f.ThrowIfStopped(Id);
                    if ((LimitBits & ~baseline) != 0)
                    {
                        Ends.Add(Motion.GetStatus(Id).Position);
                        Motion.Stop(Id);
                        yield break;
                    }
                    yield return true;
                    waited += FIND_POLL_MS;
                }
                Motion.Stop(Id);
                throw new DriveException($"no limit detected within {FIND_TIMEOUT_MS / 1000}s");
            }

            // After a limit hit the drive is in Quick Stop Active; re-enable to Operation
            // Enabled, then jog clear of the switch so the next pass starts off the limit.
            private IEnumerable<bool> RecoverAndBackOff(int awayDir)
            {
                Motion.EnableAxis(Id);
                if (!OnLimit) yield break;

                Motion.JogAt(Id, awayDir, FIND_LIMIT_SPEED);
                int waited = 0;
                while (OnLimit && waited < FIND_TIMEOUT_MS)
                {
                    _f.ThrowIfStopped(Id);
                    yield return true;
                    waited += FIND_POLL_MS;
                }
                Motion.Stop(Id);
            }
        }

        private void TrySaveCalibration()
        {
            try { _calib.Save(); }
            catch (Exception ex) { AppendLog($"WARN: calibration save failed: {ex.Message}"); }
        }
    }
}
