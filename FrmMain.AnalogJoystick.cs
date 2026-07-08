using System;
using System.Collections.Generic;

namespace NanotecController
{
    // FrmMain — analog joystick wired DIRECTLY into the Nanotec drives' I/O (not a USB HID device).
    // Ported from the vendor demo (Station/Joystick.cs), then corrected to the measured hardware
    // (2026-07-08): the stick's two pots read on the X and Y drives' analogue input 1 (0x3220:01).
    // Deflection past a small deadband commands a proportional velocity jog; centring the stick (or
    // loss of focus) stops. The knob's twist is NOT wired to any drive AI, so Θ is not driven here.
    // Polled on joystickTimer (50 ms, UI thread), which is paused during drive ops, so the reads
    // never contend with a background op on the NanoLib channel.
    //
    // NO deadman (decision 2026-07-08): the machine's candidate deadman button (Input 4) is configured
    // as the CiA-402 interlock on the X and Z drives, so pressing it FAULTS them — it's a stop/interlock,
    // not a hold-to-run enable. So the joystick does not use it; moving needs only the drives enabled and
    // the stick deflected. Do NOT re-add a hardware deadman on that input without first fixing the
    // drive-side interlock config (see [[analog-joystick]] memory). The centre is still auto-captured,
    // which guards against a mismatched fixed centre that once caused a jump. (Partial of FrmMain.)
    public partial class FrmMain
    {
        // --- Analog-joystick calibration (measured on hardware 2026-07-08) -----------
        private const int AI_MID            = 251;    // fallback centre only — real centre is auto-captured per axis (_aiMid)
        private const int AI_SPAN           = 75;     // centre → full-deflection swing (measured dev ≈ ±77–81)
        private const int AI_DEADBAND       = 10;     // ignore |reading − mid| below this (centre jitter)
        private const int AI_SPEED_STEPS    = 30;     // discrete speed levels across the range (quantise → fewer re-commands on jitter)
        private const int AI_CENTRE_SAMPLES = 5;      // average the first N polls for the centre (robust to a stale first read)
        // Full-deflection speed is NOT a constant — it's each axis's user jog-speed slider (_axisRows[id].Speed.Value).

        // Axes the joystick drives, each with a wiring sign (flip if that axis runs backwards). The
        // stick's two pots read on the X and Y drives' analogue input 1. The knob's twist is NOT wired
        // to any drive AI (Θ removed 2026-07-08 — twisting changed nothing), and Z was never on the stick.
        private static readonly (AxisId id, int sign)[] AnalogAxes =
        {
            (AxisId.X, +1),   // inverted 2026-07-08 (was −1) — X ran backwards on the bench
            (AxisId.Y, +1),
        };

        // Last velocity commanded per axis (send-on-change, so a held stick isn't re-commanded).
        private readonly Dictionary<AxisId, int> _lastAnalogVel = new();

        // Per-axis centre, auto-captured as the MEAN of the first AI_CENTRE_SAMPLES polls after the
        // source is selected (the stick is spring-centred, so that IS the centre). Averaging guards
        // against a stale/glitchy first read. Until an axis is finalised here it can't command motion.
        private readonly Dictionary<AxisId, int>  _aiMid = new();
        private readonly Dictionary<AxisId, long> _aiCentreSum   = new();
        private readonly Dictionary<AxisId, int>  _aiCentreCount = new();

        // One poll of the analog joystick (joystickTimer tick when the Joystick source is selected).
        // No deadman (see class header): with the drives enabled, each driven axis (X, Y) is commanded
        // from its pot deflection past the deadband; a centred stick reads dev≈0 → stop.
        private void TickAnalogJoystick()
        {
            if (_motion == null) return;
            bool enabled = _drivesEnabled && !_busy;

            bool centresReady = true, anyMoving = false;
            foreach ((AxisId id, int sign) in AnalogAxes)
            {
                int raw;
                try { raw = _motion.GetAnalogInput1(id); }
                catch (DriveException)
                {
                    joystickStatusLabel.Text = "Joystick: read FAILED";
                    StopAnalogAxes();   // safety: don't leave an axis running on a stale command
                    return;
                }

                CaptureCentre(id, raw);
                if (!_aiMid.TryGetValue(id, out int mid)) { centresReady = false; ApplyAnalogVel(id, 0); continue; }

                int dev = raw - mid;
                int vel = 0;
                if (enabled && Math.Abs(dev) > AI_DEADBAND)
                {
                    int maxSpeed = _axisRows[id].Speed.Value;             // full deflection = this axis's jog-speed slider
                    vel = sign * (maxSpeed * dev / AI_SPAN);
                    vel = Math.Clamp(vel, -maxSpeed, maxSpeed);
                    int quantum = Math.Max(1, maxSpeed / AI_SPEED_STEPS); // scale the quantum with the range so low speeds stay proportional
                    vel = vel / quantum * quantum;                        // quantise → fewer re-commands on jitter
                    if (InvertDir(id, 1) < 0) vel = -vel;                 // movement-inversion toggle (X/Y)
                }

                ApplyAnalogVel(id, vel);
                if (vel != 0) anyMoving = true;
            }

            joystickStatusLabel.Text = !centresReady ? "Joystick: calibrating centre…"
                                     : anyMoving      ? "Joystick: moving"
                                                      : "Joystick: idle";
        }

        // Auto-centre: average the first AI_CENTRE_SAMPLES readings, then freeze the centre for this axis.
        private void CaptureCentre(AxisId id, int raw)
        {
            if (_aiMid.ContainsKey(id)) return;
            long sum = (_aiCentreSum.TryGetValue(id, out long s) ? s : 0) + raw;
            int count = (_aiCentreCount.TryGetValue(id, out int c) ? c : 0) + 1;
            _aiCentreSum[id] = sum;
            _aiCentreCount[id] = count;
            if (count >= AI_CENTRE_SAMPLES)
            {
                int mid = (int)(sum / count);
                _aiMid[id] = mid;
                AppendLog($"Joystick {id}: AI centre = {mid} (mean of {count} polls, 0x3220:01).");
            }
        }

        // Send-on-change velocity command for one axis (soft-limit-aware), mirroring the puck path.
        private void ApplyAnalogVel(AxisId id, int vel)
        {
            if (vel != 0 && _softLimits.IsBlocked(id, Math.Sign(vel))) vel = 0;   // soft limit → stop
            if (_lastAnalogVel.TryGetValue(id, out int last) && last == vel) return;
            _lastAnalogVel[id] = vel;
            CommandAxisVelocity(id, vel, honorSoftLimit: false);   // block already applied above
        }

        // Stops any axis the analog joystick was driving and clears its last-command cache.
        private void StopAnalogAxes()
        {
            foreach ((AxisId id, int _) in AnalogAxes)
            {
                if (_motion != null && _lastAnalogVel.TryGetValue(id, out int v) && v != 0)
                    try { _motion.Stop(id); } catch (DriveException) { }
                _lastAnalogVel[id] = 0;
            }
        }
    }
}
