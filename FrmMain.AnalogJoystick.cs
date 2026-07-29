using System;
using System.Collections.Generic;

namespace NanotecController
{
    // FrmMain — analog joystick wired DIRECTLY into the Nanotec drives' I/O (not a USB HID device).
    // Ported from the vendor demo (Station/Joystick.cs), then corrected to the measured hardware:
    // the stick's X/Y pots read on the X and Y drives' analogue input 1 (0x3220:01), and the knob's
    // twist pot reads on the Z DRIVE's analogue input 1 (measured 2026-07-09) but commands the Θ axis.
    // Deflection past a small deadband commands a proportional velocity jog; centring the stick (or
    // loss of focus) stops. Twist → Θ: in RAW jog mode it jogs Θ at a proportional velocity; in VISION
    // jog mode it rotates the chuck ABOUT THE CROSSHAIR (the tuned HoldRotate follower), so the point
    // under the crosshair stays put — the "compensated rotation" the user asked for.
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

        // Axes the joystick drives. Each entry is (axis to COMMAND, drive whose analogue input 1 supplies
        // the pot, wiring sign). Read drive ≠ command axis for the twist: the knob's twist pot is wired
        // into the Z DRIVE's analogue input 1 (measured 2026-07-09: twist-only test swung Z's 0x3220:01
        // by ~±90 while every other channel stayed at noise), but it drives the Θ axis. The X/Y pots read
        // on their own drives. (The Θ drive's own AI1 is the dead channel that sits at ~6.)
        private static readonly (AxisId cmd, AxisId pot, int sign)[] AnalogAxes =
        {
            (AxisId.X,     AxisId.X, +1),   // inverted 2026-07-08 (was −1) — X ran backwards on the bench
            (AxisId.Y,     AxisId.Y, +1),
            (AxisId.Theta, AxisId.Z, +1),   // twist pot lives on the Z drive's AI1; sign flips if Θ turns the wrong way
        };

        // VISION-mode stick→screen mapping. In VISION jog mode the stick does NOT drive raw X/Y;
        // its deflection is read as a SCREEN-space direction (right+/up+) and fed through the
        // pixel→step affine (like the puck/d-pad) so that pushing the stick purely along X moves
        // the chuck along the COMPENSATED (camera) X axis, cancelling the camera↔stage rotation.
        // These signs map each pot's raw deflection to a screen direction — flip on the bench if
        // pushing right/up steers the wrong way on screen. They are independent of the raw
        // AnalogAxes signs above (which tune raw drive motion, a different frame).
        private const int VISION_STICK_X = -1;   // pot-X deflection → screen right+ (inverted on the bench 2026-07-09)
        private const int VISION_STICK_Y = -1;   // pot-Y deflection → screen up+  (inverted on the bench 2026-07-09)

        // VISION-mode twist → rotate about the crosshair. The twist starts the tuned HoldRotate
        // controller (Θ spins while X/Y follows to pin the crosshair); this sign maps the twist
        // direction to the Θ rotate direction — flip on the bench if twisting one way rotates the
        // other. TWIST_RELEASE_POLL_MS = how often the rotate loop re-reads the twist to detect the
        // knob returning to centre (throttled because that read runs inside the rotate hot loop).
        private const int VISION_TWIST_SIGN     = +1;
        private const int TWIST_RELEASE_POLL_MS = 120;

        // Last velocity commanded per axis (send-on-change, so a held stick isn't re-commanded).
        private readonly Dictionary<AxisId, int> _lastAnalogVel = new();

        // Per-axis centre, auto-captured as the MEAN of the first AI_CENTRE_SAMPLES polls after the
        // source is selected (the stick is spring-centred, so that IS the centre). Averaging guards
        // against a stale/glitchy first read. Until an axis is finalised here it can't command motion.
        private readonly Dictionary<AxisId, int>  _aiMid = new();
        private readonly Dictionary<AxisId, long> _aiCentreSum   = new();
        private readonly Dictionary<AxisId, int>  _aiCentreCount = new();

        // Reject a centre capture whose samples spread more than this — the knob was being moved
        // during the window, so the mean would be biased. A biased twist centre is the perpetual-
        // rotation bug: rest then reads as a fixed deflection the release predicate can never clear.
        // Tune on the bench: a twist swings the pot ~±90, rest noise is a few counts — set between.
        private const int AI_CENTRE_MAX_SPREAD = 12;
        private readonly Dictionary<AxisId, (int min, int max)> _aiCentreRange = new();

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

            ProbeAnalogInputs();   // TEMP: Θ-wiring verification (remove once the twist pot's drive is confirmed)

            // Read every pot + keep auto-centring (they all finish on the same tick — they start together
            // at ResetJoy). Bail on a read error; wait until all centres are captured before moving.
            var dev = new Dictionary<AxisId, int>();
            try
            {
                foreach ((AxisId cmd, AxisId pot, int _) in AnalogAxes)
                {
                    if (!TryReadDeflection(cmd, pot, out int d))
                    {
                        joystickStatusLabel.Text = "Joystick: centring — leave the stick alone";
                        _visionView.CenteringOverlay = true;   // warn on the live view until the centre is captured
                        return;
                    }
                    dev[cmd] = d;
                }
                _visionView.CenteringOverlay = false;   // all axes centred — clear the warning
            }
            catch (DriveException)
            {
                joystickStatusLabel.Text = "Joystick: read FAILED";
                StopAnalogAxes();                                 // stop raw axes + clear their send-on-change cache
                _visionLastVx = _visionLastVy = 0;                // and the vision cache (so a resume re-commands)
                return;
            }

            if (_jogMode == JogMode.Vision) TickAnalogVision(dev, enabled);
            else                            TickAnalogRaw(dev, enabled);
        }

        // Reads one pot (analogue input 1 of the drive it's wired to), updates the centre of the axis
        // it commands, and returns the signed deflection (raw − centre). Read drive ≠ command axis for Θ
        // (twist pot on the Z drive). Returns false while the centre is still being averaged. Throws on a
        // read error.
        private bool TryReadDeflection(AxisId cmd, AxisId pot, out int dev)
        {
            dev = 0;
            int raw = _motion!.GetAnalogInput1(pot);
            CaptureCentre(cmd, raw);
            if (!_aiMid.TryGetValue(cmd, out int mid)) return false;
            dev = raw - mid;
            return true;
        }

        // RAW mode: each pot drives its command axis (X, Y, and Θ from the twist) at a velocity
        // proportional to its deflection, full deflection = that axis's jog-speed slider. Per-axis,
        // quantised, send-on-change.
        private void TickAnalogRaw(Dictionary<AxisId, int> dev, bool enabled)
        {
            bool anyMoving = false;
            foreach ((AxisId cmd, AxisId _, int sign) in AnalogAxes)
            {
                int d = dev[cmd];
                int vel = 0;
                if (enabled && Math.Abs(d) > AI_DEADBAND)
                {
                    int maxSpeed = _axisRows[cmd].Speed.Value;            // full deflection = this axis's jog-speed slider
                    vel = sign * (maxSpeed * d / AI_SPAN);
                    vel = Math.Clamp(vel, -maxSpeed, maxSpeed);
                    int quantum = Math.Max(1, maxSpeed / AI_SPEED_STEPS); // scale the quantum with the range so low speeds stay proportional
                    vel = vel / quantum * quantum;                        // quantise → fewer re-commands on jitter
                    if (InvertDir(cmd, 1) < 0) vel = -vel;                // movement-inversion toggle (X/Y/Θ)
                }

                ApplyAnalogVel(cmd, vel);
                if (vel != 0) anyMoving = true;
            }

            joystickStatusLabel.Text = anyMoving ? "Joystick: moving" : "Joystick: idle";
        }

        // VISION mode. The stick's X/Y deflection is a SCREEN-space direction (right+/up+) fed through
        // the pixel→step affine so the chuck follows that screen direction (compensated for the camera
        // rotation), speed scaled by deflection × the dedicated vision speed slider. The TWIST rotates
        // the chuck ABOUT THE CROSSHAIR: it starts the tuned HoldRotate controller (Θ spins while X/Y
        // follows to pin the point under the crosshair) — the "compensated rotation" option (b). Twist
        // takes precedence over the X/Y screen jog (you do one or the other).
        private void TickAnalogVision(Dictionary<AxisId, int> dev, bool enabled)
        {
            // Twist → rotate about the crosshair. HoldRotateAsync runs the whole rotation on a
            // background thread (RunDriveOp stops the joystick timer meanwhile); since the joystick has
            // no MouseUp to end it, we inject a predicate that stops the rotation when the knob
            // re-centers. It resumes joystick polling once the rotation settles. One at a time.
            int twist = dev[AxisId.Theta];
            if (enabled && Math.Abs(twist) > AI_DEADBAND && !_holdRotating && !_busy)
            {
                if (!CanRotate)
                    joystickStatusLabel.Text = "Joystick: twist needs a chuck centre + camera calibration";
                else
                {
                    VisionStop();                       // hand X/Y to the rotate follower
                    _visionLastVx = _visionLastVy = 0;
                    joystickStatusLabel.Text = "Joystick: rotating about crosshair";
                    _ = HoldRotateAsync(VISION_TWIST_SIGN * Math.Sign(twist), TwistReleasedPredicate());
                }
                return;                                 // twist takes precedence over the X/Y screen jog
            }

            double sx = StickDeflection(dev[AxisId.X]) * VISION_STICK_X;   // screen right+
            double sy = StickDeflection(dev[AxisId.Y]) * VISION_STICK_Y;   // screen up+
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

        // Builds the stop-predicate the twist-driven HoldRotate polls: true once the twist knob returns
        // to centre. The knob's pot is on the Z drive's AI1; the read runs inside the rotate loop on the
        // background thread, so it is throttled (TWIST_RELEASE_POLL_MS) to add little SDO load, and a
        // failed read stops the rotation (fail-safe). Captures the centre so it never touches the shared
        // _aiMid dict from the background thread.
        private Func<bool> TwistReleasedPredicate()
        {
            int centre = _aiMid.TryGetValue(AxisId.Theta, out int c) ? c : AI_MID;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            long lastMs = -TWIST_RELEASE_POLL_MS;   // negative (not MinValue) so the FIRST poll fires and now-lastMs can't overflow
            bool released = false;
            return () =>
            {
                long now = clock.ElapsedMilliseconds;
                if (now - lastMs < TWIST_RELEASE_POLL_MS) return released;   // throttle in the hot loop
                lastMs = now;
                try { released = Math.Abs(_motion!.GetAnalogInput1(AxisId.Z) - centre) <= AI_DEADBAND; }
                catch (DriveException) { released = true; }
                return released;
            };
        }

        // Normalised signed deflection for one pot: 0 inside the deadband, ±1 at full swing (AI_SPAN).
        private static double StickDeflection(int dev)
            => Math.Abs(dev) <= AI_DEADBAND ? 0.0 : Math.Clamp((double)dev / AI_SPAN, -1.0, 1.0);

        // Auto-centre: average the first AI_CENTRE_SAMPLES readings, then freeze the centre for this axis.
        // Restarts the window if the samples spread (knob moving) so a mid-twist read can't bias the centre.
        private void CaptureCentre(AxisId id, int raw)
        {
            if (_aiMid.ContainsKey(id)) return;

            var (min, max) = _aiCentreRange.TryGetValue(id, out var r) ? r : (raw, raw);
            min = Math.Min(min, raw); max = Math.Max(max, raw);
            if (max - min > AI_CENTRE_MAX_SPREAD)   // knob moved during the window → discard, restart from here
            {
                _aiCentreSum[id] = raw; _aiCentreCount[id] = 1; _aiCentreRange[id] = (raw, raw);
                return;
            }

            long sum = (_aiCentreSum.TryGetValue(id, out long s) ? s : 0) + raw;
            int count = (_aiCentreCount.TryGetValue(id, out int c) ? c : 0) + 1;
            _aiCentreSum[id] = sum; _aiCentreCount[id] = count; _aiCentreRange[id] = (min, max);
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

        // --- TEMP Θ-wiring probe ---------------------------------------------------------
        // Logs BOTH analogue inputs (0x3220:01 and 0x3220:02) of all four drives about once a
        // second so twisting the joystick knob reveals which channel the twist pot is wired to.
        // AI1 showed nothing on twist, so the twist pot is likely on AI2 (or a drive without it
        // reads "—"). X and Y already move on stick push; the twist should move exactly one cell.
        // Once the Θ pot's channel is confirmed, DELETE this method and its call in TickAnalogJoystick.
        private int _aiProbeTick;
        // Running min/max per (drive, analog-input) since the last source-select, so a twist that
        // moves a channel only a little still shows up as a span. Cleared in ResetJoy (ResetAiSpans).
        private readonly Dictionary<(AxisId, byte), (int min, int max)> _aiSpan = new();
        private void ProbeAnalogInputs()
        {
            // Sample every tick (50 ms) so brief deflections are caught in the span.
            foreach (AxisId id in new[] { AxisId.X, AxisId.Y, AxisId.Z, AxisId.Theta })
                for (byte sub = 0x01; sub <= 0x02; sub++)
                {
                    if (!TryReadAi(id, sub, out int v)) continue;
                    var key = (id, sub);
                    if (_aiSpan.TryGetValue(key, out var mm)) _aiSpan[key] = (Math.Min(mm.min, v), Math.Max(mm.max, v));
                    else                                      _aiSpan[key] = (v, v);
                }

            if (++_aiProbeTick % 20 != 0) return;   // print ~1 Hz
            for (byte sub = 0x01; sub <= 0x02; sub++)
                AppendLog($"AI{sub} span — X:{SpanText(AxisId.X, sub)}  Y:{SpanText(AxisId.Y, sub)}  " +
                          $"Z:{SpanText(AxisId.Z, sub)}  Θ:{SpanText(AxisId.Theta, sub)}");
        }

        // "min..max(Δspan)" for one channel, or "—" if it hasn't read. A channel wired to whatever
        // you're moving shows a large Δ; a flat channel shows Δ0.
        private string SpanText(AxisId id, byte sub)
            => _aiSpan.TryGetValue((id, sub), out var mm) ? $"{mm.min}..{mm.max}(Δ{mm.max - mm.min})" : "—";

        private bool TryReadAi(AxisId id, byte sub, out int value)
        {
            try { value = (short)_motion!.GetObject(id, 0x3220, sub); return true; }
            catch (DriveException) { value = 0; return false; }
        }

        private void ResetAiSpans() => _aiSpan.Clear();

        // Stops any axis the analog joystick was driving and clears its last-command cache.
        private void StopAnalogAxes()
        {
            foreach ((AxisId cmd, AxisId _, int _) in AnalogAxes)
            {
                if (_motion != null && _lastAnalogVel.TryGetValue(cmd, out int v) && v != 0)
                    try { _motion.Stop(cmd); } catch (DriveException) { }
                _lastAnalogVel[cmd] = 0;
            }
        }
    }
}
