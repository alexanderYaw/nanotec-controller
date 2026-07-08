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

        // VISION-mode stick→screen mapping. In VISION jog mode the stick does NOT drive raw X/Y;
        // its deflection is read as a SCREEN-space direction (right+/up+) and fed through the
        // pixel→step affine (like the puck/d-pad) so that pushing the stick purely along X moves
        // the chuck along the COMPENSATED (camera) X axis, cancelling the camera↔stage rotation.
        // These signs map each pot's raw deflection to a screen direction — flip on the bench if
        // pushing right/up steers the wrong way on screen. They are independent of the raw
        // AnalogAxes signs above (which tune raw drive motion, a different frame).
        private const int VISION_STICK_X = +1;   // pot-X deflection → screen right+
        private const int VISION_STICK_Y = +1;   // pot-Y deflection → screen up+

        // Last velocity commanded per axis (send-on-change, so a held stick isn't re-commanded).
        private readonly Dictionary<AxisId, int> _lastAnalogVel = new();

        // Per-axis centre, auto-captured as the MEAN of the first AI_CENTRE_SAMPLES polls after the
        // source is selected (the stick is spring-centred, so that IS the centre). Averaging guards
        // against a stale/glitchy first read. Until an axis is finalised here it can't command motion.
        private readonly Dictionary<AxisId, int>  _aiMid = new();
        private readonly Dictionary<AxisId, long> _aiCentreSum   = new();
        private readonly Dictionary<AxisId, int>  _aiCentreCount = new();

        // One poll of the analog joystick (joystickTimer tick when the Joystick source is selected).
        // No deadman (see class header): with the drives enabled, deflecting past the deadband moves,
        // centring stops. Reads BOTH pots (X and Y drives' analogue input 1), keeps their centres
        // auto-captured, then dispatches on jog mode: RAW = per-axis drive velocity (unchanged);
        // VISION = the stick's deflection as a screen direction fed through the pixel→step affine, so
        // a pure-X push moves the chuck along the compensated (camera) X axis — the same drift-corrected
        // path the on-screen puck (VisionPadTick) and d-pad (VisionJog) use.
        private void TickAnalogJoystick()
        {
            if (_motion == null) return;
            bool enabled = _drivesEnabled && !_busy;

            // Read both pots + keep auto-centring (both finish on the same tick — they start together
            // at ResetJoy). Bail on a read error; wait until both centres are captured before moving.
            int devX, devY;
            try
            {
                bool okX = TryReadDeflection(AxisId.X, out devX);
                bool okY = TryReadDeflection(AxisId.Y, out devY);
                if (!okX || !okY) { joystickStatusLabel.Text = "Joystick: calibrating centre…"; return; }
            }
            catch (DriveException)
            {
                joystickStatusLabel.Text = "Joystick: read FAILED";
                StopAnalogAxes();                                 // stop raw X/Y + clear its send-on-change cache
                _visionLastVx = _visionLastVy = 0;                // and the vision cache (so a resume re-commands)
                return;
            }

            if (_jogMode == JogMode.Vision) TickAnalogVision(devX, devY, enabled);
            else                            TickAnalogRaw(devX, devY, enabled);
        }

        // Reads one pot, updates its auto-centre, and returns the signed deflection (raw − centre).
        // Returns false while the centre is still being averaged. Throws DriveException on a read error.
        private bool TryReadDeflection(AxisId id, out int dev)
        {
            dev = 0;
            int raw = _motion!.GetAnalogInput1(id);
            CaptureCentre(id, raw);
            if (!_aiMid.TryGetValue(id, out int mid)) return false;
            dev = raw - mid;
            return true;
        }

        // RAW mode: each pot drives its own drive axis at a velocity proportional to its deflection,
        // full deflection = that axis's jog-speed slider. (Unchanged behaviour, per-axis + quantised.)
        private void TickAnalogRaw(int devX, int devY, bool enabled)
        {
            bool anyMoving = false;
            foreach ((AxisId id, int sign) in AnalogAxes)
            {
                int dev = id == AxisId.X ? devX : devY;
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

            joystickStatusLabel.Text = anyMoving ? "Joystick: moving" : "Joystick: idle";
        }

        // VISION mode: the stick's deflection is a SCREEN-space direction (right+/up+); it goes through
        // the pixel→step affine so the chuck follows that screen direction (compensated for the camera
        // rotation), speed scaled by deflection × the dedicated vision speed slider. Send-on-change,
        // sharing _visionLastVx/Vy + VisionJogUser/VisionStop with the puck path (only one is live).
        private void TickAnalogVision(int devX, int devY, bool enabled)
        {
            double sx = StickDeflection(devX) * VISION_STICK_X;   // screen right+
            double sy = StickDeflection(devY) * VISION_STICK_Y;   // screen up+
            double vmag = Math.Min(1.0, Math.Sqrt(sx * sx + sy * sy));

            PixelStepAffine? a = _calib.PixelStep;
            int vx = 0, vy = 0;
            if (enabled && a != null && vmag > 0)                 // per-axis deadband already zeroed jitter
                VisionJogMath.TryUserVelocity(a, sx, -sy, vmag * _visionSpeed.Value, out vx, out vy);

            if (vx != _visionLastVx || vy != _visionLastVy)       // send-on-change
            {
                _visionLastVx = vx; _visionLastVy = vy;
                if (vx == 0 && vy == 0) VisionStop();
                else VisionJogUser(vx, vy);
            }

            joystickStatusLabel.Text = a == null ? "Joystick: needs camera-scale calibration"
                                     : (vx != 0 || vy != 0) ? "Joystick: moving (vision)"
                                                            : "Joystick: idle";
        }

        // Normalised signed deflection for one pot: 0 inside the deadband, ±1 at full swing (AI_SPAN).
        private static double StickDeflection(int dev)
            => Math.Abs(dev) <= AI_DEADBAND ? 0.0 : Math.Clamp((double)dev / AI_SPAN, -1.0, 1.0);

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
