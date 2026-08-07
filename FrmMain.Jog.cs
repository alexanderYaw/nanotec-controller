using System;
using System.Windows.Forms;

namespace NanotecController
{
    // FrmMain — per-axis hold-to-jog buttons, the live status poll, and the soft-limit
    // guard (stop-when-jogging-past-a-stored-limit + the same-direction jog block).
    // (Partial of FrmMain.)
    public partial class FrmMain
    {
        #region Per-axis hold-to-jog (individual control)
        // Quick SDO writes done on the UI thread so press/release ordering is exact.

        // Movement-inversion toggle (visual-centering): when on, flips the commanded jog
        // direction for the in-plane / rotary axes so the controls match the inverted camera
        // view. Applied at every manual-jog entry point (d-pad, USB joystick, on-screen puck)
        // BEFORE the soft-limit logic, so the SoftLimitTracker direction state stays in true
        // command space. Z is excluded (depth axis, unaffected by an in-plane view flip).
        private bool _invertMovement;

        private int InvertDir(AxisId id, int dir)
            => (_invertMovement && (id == AxisId.X || id == AxisId.Y || id == AxisId.Theta)) ? -dir : dir;

        private void StartJog(AxisId id, int direction)
        {
            if (!ManualInputAllowed) return;
            direction = InvertDir(id, direction);
            if (_softLimits.IsBlocked(id, direction))
            {
                AppendLog($"{id} at soft limit - jog {(direction > 0 ? "+" : "−")} blocked (jog back into range first).");
                return;
            }
            int speed = _axisRows[id].Speed.Value;
            try
            {
                // If a limit hit left the axis in Quick-Stop-Active, clear it first so the
                // jog can drive it off the switch (a plain jog/controlword 0x0F won't recover
                // it on this drive — same reason the auto limit-find re-enables). This runs
                // on the UI thread, so there is a brief (~1 s) hitch while it re-enables, but
                // only when actually quick-stopped; a normal jog is just one extra status read.
                if (_motion.RecoverIfQuickStopped(id))
                    AppendLog($"{id} was in Quick Stop (limit) - re-enabled.");
                _motion.JogAt(id, direction, speed);
                _softLimits.RecordCommand(id, direction);
            }
            catch (DriveException ex)
            {
                AppendLog($"ERROR: jog {id} failed: {ex.Message}");
            }
        }

        private void StopJog(AxisId id)
        {
            if (_motion == null) return;
            try { _motion.Stop(id); _softLimits.RecordCommand(id, 0); }
            catch (DriveException ex) { AppendLog($"ERROR: stop {id} failed: {ex.Message}"); }
        }

        /// <summary>
        /// Commands one axis at a signed velocity (raw drive units; sign = direction) and records the
        /// direction for the soft-limit tracker. With <paramref name="honorSoftLimit"/>, a velocity that
        /// would push further past a parked soft limit is squashed to a stop. Zero stops the axis. The
        /// single velocity-mode path for the joystick puck, vision jog, and on-screen vector — so the
        /// JogAt/Stop + RecordCommand bookkeeping can't drift between them. (The rotate follow-loop uses
        /// CommandFollow instead: it must let a DriveException propagate to abort the rotation, whereas
        /// this swallows + logs so a UI jog never crashes the form.)
        /// </summary>
        private void CommandAxisVelocity(AxisId id, int signedVel, bool honorSoftLimit)
        {
            if (honorSoftLimit && signedVel != 0 && _softLimits.IsBlocked(id, Math.Sign(signedVel)))
                signedVel = 0;
            try
            {
                if (signedVel == 0) { _motion!.Stop(id); _softLimits.RecordCommand(id, 0); }
                else { _motion!.SetJogVelocity(id, Math.Sign(signedVel), Math.Abs(signedVel)); _softLimits.RecordCommand(id, Math.Sign(signedVel)); }
            }
            catch (DriveException ex) { AppendLog($"ERROR: command {id}: {ex.Message}"); }
        }

        #endregion

        #region Live status poll
        /// <summary>
        /// Consumes one <see cref="DrivePoller"/> sample on the UI thread: axis readouts, the
        /// soft-limit guard, and the analog joystick. Bails while an op is running — a sample
        /// queued just before the pause must not issue a soft-limit Stop into it.
        /// </summary>
        private void OnDriveSample(DriveSample s)
        {
            if (_motion == null || _busy) return;

            if (s.Fresh)
            {
                if (s.Error != null)
                {
                    _statusFailures++;
                    if (_statusFailures >= MAX_CONSECUTIVE_READ_FAILURES)
                    {
                        PausePolling();
                        AppendLog($"Lost contact with a drive: {s.Error}");
                        return;
                    }
                }
                else
                {
                    _statusFailures = 0;
                    foreach ((AxisId id, long raw) in s.Positions)
                    {
                        _lastPos[id] = raw;   // cache raw; Position Map reads it in the user frame
                        long shown = ToUser(id, raw);   // user frame (Y inverted) for an intuitive readout
                        (string state, bool fault, bool quickStopped) =
                            s.States.TryGetValue(id, out var st) ? st : ("-", false, false);
                        _axisRows[id].Status.Text = $"{shown,12:N0}   {state}{(fault ? "  [Fault]" : "")}";
                        EnforceSoftLimits(id, raw);
                        AutoRecoverQuickStop(id, quickStopped);
                    }
                }
            }

            if (s.Analog != null || s.AnalogError) TickAnalogJoystick(s);
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
            SoftLimitTracker.Decision d = _softLimits.Evaluate(id, pos, _calib, _drivesEnabled);
            if (d.Stop) { try { _motion!.Stop(id); } catch (DriveException) { } }
            if (d.Log != null) AppendLog(d.Log);
        }

        // Axes recovered from a quick stop and not yet seen out of it. The poller refreshes the
        // state at a fraction of the position rate and carries the last value forward in between,
        // so without this latch the same stale "Quick Stop" would re-trigger the recovery for up
        // to a couple of position polls after it has already worked.
        private readonly HashSet<AxisId> _quickStopRecovered = new();

        /// <summary>
        /// Clears a limit-switch quick stop as soon as the poller sees one, so the axis is jogged
        /// off the switch instead of sitting dead in Quick-Stop-Active. Only Y's switches actually
        /// quick-stop the drive (0x3701 = 6); X ignores its switches and Z has none, so before this
        /// only Y could get stuck — and only on the velocity paths (analog joystick, puck, vision
        /// jog), which unlike <see cref="StartJog"/> never called RecoverIfQuickStopped.
        ///
        /// The re-enable leaves the axis halted and disarmed, so it cannot resume driving into the
        /// switch; the direction that tripped it is then refused until the operator reverses, or a
        /// held stick would re-command straight back in and hammer the switch once per poll.
        /// </summary>
        private void AutoRecoverQuickStop(AxisId id, bool quickStopped)
        {
            if (!quickStopped) { _quickStopRecovered.Remove(id); return; }
            if (!_drivesEnabled || _motion == null || !_quickStopRecovered.Add(id)) return;

            try
            {
                // Re-checks the statusword under the channel lock, so a carried-forward sample for
                // an axis something else already recovered costs one read and blocks no direction.
                if (!_motion.RecoverIfQuickStopped(id)) return;
                bool blocked = _softLimits.BlockCommandedDirection(id);
                AppendLog($"{id} hit a limit switch (Quick Stop) - re-enabled" +
                          (blocked ? "; jog back the other way." : "."));
            }
            catch (DriveException ex)
            {
                _quickStopRecovered.Remove(id);   // let the next poll try again
                AppendLog($"ERROR: {id} quick-stop recovery failed: {ex.Message}");
            }
        }

        /// <summary>Clears soft-limit tracking so a stale position delta can't trigger a false stop.</summary>
        private void ResetSoftLimitTracking()
        {
            _softLimits.Reset();
            _lastPos.Clear();
            _quickStopRecovered.Clear();
        }

        #endregion
    }
}
