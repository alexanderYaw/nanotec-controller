using System;
using System.Threading;
using Nlc; // NanoLib namespace

namespace NanotecController
{
    /// <summary>
    /// Raised when a NanoLib operation reports an error or the drive fails to
    /// reach an expected CiA 402 state. Callers should treat this as "the
    /// hardware link or drive is not in a usable state" and shut down safely.
    /// </summary>
    public class DriveException : Exception
    {
        public DriveException(string message) : base(message) { }
    }

    /// <summary>
    /// Drives ONE axis (any of X / Y / Z / Θ) over NanoLib via the CiA 402 state machine:
    /// enable/disable, profile-velocity jog, profile-position moves, homing, and status reads.
    /// One instance per connected drive; <see cref="MultiAxisController"/> holds the set and is
    /// what the rest of the app talks to. (Formerly "ChuckController" — it is not chuck-specific.)
    /// </summary>
    public class AxisDriver
    {
        private readonly NanoLibAccessor _accessor;
        private readonly DeviceHandle _deviceHandle;

        // Hardcoded Object Dictionary Indices for CiA 402 Motion
        private readonly OdIndex OD_Controlword  = new OdIndex(0x6040, 0x00);
        private readonly OdIndex OD_Statusword   = new OdIndex(0x6041, 0x00);
        private readonly OdIndex OD_ModesOfOp    = new OdIndex(0x6060, 0x00);
        private readonly OdIndex OD_ModesDisplay = new OdIndex(0x6061, 0x00);
        private readonly OdIndex OD_PosActual    = new OdIndex(0x6064, 0x00);
        private readonly OdIndex OD_TargetVel    = new OdIndex(0x60FF, 0x00);
        private readonly OdIndex OD_HomeOffset   = new OdIndex(0x607C, 0x00);
        private readonly OdIndex OD_HomingMethod = new OdIndex(0x6098, 0x00);
        // Digital Inputs status object (limit-switch bits used by the calibration find).
        private readonly OdIndex OD_DigitalInputs = new OdIndex(0x60FD, 0x00);
        // Store-parameters object: writing the "save" signature persists RAM values → NV.
        private readonly OdIndex OD_StoreParameters = new OdIndex(0x1010, 0x01);

        // Profile Position mode objects (for MoveAbsolute / MoveRelative).
        private readonly OdIndex OD_TargetPosition  = new OdIndex(0x607A, 0x00);
        private readonly OdIndex OD_ProfileVelocity = new OdIndex(0x6081, 0x00);
        private readonly OdIndex OD_ProfileAccel    = new OdIndex(0x6083, 0x00);
        private readonly OdIndex OD_ProfileDecel    = new OdIndex(0x6084, 0x00);

        public const long ENCODER_TICKS_PER_REV = 40000;

        // --- Object sizes, in bits (writeNumber's last argument is a BIT length) ---
        private const uint BITS_8  = 8;
        private const uint BITS_16 = 16;
        private const uint BITS_32 = 32;

        // Signature written to 0x1010:01 to persist current parameters to NV (ASCII "save").
        private const long STORE_SIGNATURE = 0x65766173;

        // --- CiA 402 controlword commands (object 0x6040) ---
        private const ushort CW_DISABLE          = 0x0000; // disable voltage
        private const ushort CW_SHUTDOWN         = 0x0006; // -> Ready To Switch On
        private const ushort CW_SWITCH_ON        = 0x0007; // -> Switched On
        private const ushort CW_ENABLE_OPERATION = 0x000F; // -> Operation Enabled (halt bit clear)
        private const ushort CW_FAULT_RESET      = 0x0080; // rising edge of bit 7 clears a fault
        private const ushort CW_HALT             = 0x010F; // Operation Enabled + halt (bit 8)
        private const ushort CW_START_HOMING     = 0x001F; // Operation Enabled + start (bit 4)
        // Profile Position: bit4 = new set-point, bit5 = change immediately, bit6 = relative.
        private const ushort CW_PP_NEWSETPOINT_ABS = 0x003F; // op enabled + new set-point + change-now
        private const ushort CW_PP_NEWSETPOINT_REL = 0x007F; // ... + relative (bit 6)

        // --- CiA 402 statusword bits / state masks (object 0x6041) ---
        private const long SW_FAULT           = 0x0008; // bit 3
        private const long SW_TARGET_REACHED  = 0x0400; // bit 10
        private const long SW_SETPOINT_ACK    = 0x1000; // bit 12 (Profile Position: set-point acknowledge)
        private const long SW_HOMING_ATTAINED = 0x1000; // bit 12 (homing mode)
        private const long SW_HOMING_ERROR    = 0x2000; // bit 13 (homing mode)

        private const long SW_STATE_MASK            = 0x006F;
        private const long SW_STATE_READY_TO_SWITCH = 0x0021;
        private const long SW_STATE_SWITCHED_ON     = 0x0023;
        private const long SW_STATE_OP_ENABLED      = 0x0027;
        // Quick-Stop-Active (0x07) is what a limit hit leaves the drive in on this machine:
        // bits 0/1/2 set but the quick-stop bit (5) CLEAR. Motion commands are ignored until
        // it is cleared by a re-enable. Fault-Reaction-Active (0x0F) is the transient state
        // while a fault ramps down.
        private const long SW_STATE_QUICK_STOP_ACTIVE = 0x0007;
        private const long SW_STATE_FAULT_REACTION    = 0x000F;
        // "Switch On Disabled" uses mask 0x4F (the quick-stop bit is don't-care here).
        private const long SW_STATE_MASK_SOD           = 0x004F;
        private const long SW_STATE_SWITCH_ON_DISABLED = 0x0040;

        // --- Modes of operation (object 0x6060) ---
        private const sbyte MODE_PROFILE_POSITION = 1;
        private const sbyte MODE_PROFILE_VELOCITY = 3;
        private const sbyte MODE_HOMING           = 6;

        // Homing method 34: home on current position (per this drive's configuration).
        private const long HOMING_METHOD_CURRENT_POSITION = 34;

        // Timing for state-transition / homing polling.
        private const int STATE_TIMEOUT_MS = 500;
        private const int HOMING_TIMEOUT_MS = 10000;
        private const int POLL_STEP_MS = 10;
        private const int HOMING_POLL_MS = 100;

        /// <summary>Per-axis configuration (name, jog speeds, limits, direction).</summary>
        public AxisConfig Config { get; }

        public AxisDriver(NanoLibAccessor accessor, DeviceHandle deviceHandle)
            : this(accessor, deviceHandle, new AxisConfig()) { }

        public AxisDriver(NanoLibAccessor accessor, DeviceHandle deviceHandle, AxisConfig config)
        {
            _accessor = accessor;
            _deviceHandle = deviceHandle;
            Config = config;
        }

        /// <summary>A single snapshot of one axis, read from the drive.</summary>
        public readonly struct AxisStatus
        {
            /// <summary>Actual position from object 0x6064, in the drive's configured
            /// position units (raw counts / user units until factor-group scaling is decoded).</summary>
            public long Position { get; init; }
            public double AngleDegrees { get; init; }
            public string State { get; init; }
            public bool HasFault { get; init; }
        }

        // --- Checked NanoLib wrappers -------------------------------------------------
        // Every write/read result is inspected. A failed read used to return a silent
        // 0 via getResult(); these helpers turn that into an explicit DriveException.

        private void Write(long value, OdIndex od, uint bitLength, string what)
        {
            using ResultVoid r = _accessor.writeNumber(_deviceHandle, value, od, bitLength);
            if (r.hasError())
                throw new DriveException($"Write failed ({what}): {r.getError()}");
        }

        private long Read(OdIndex od, string what)
        {
            using ResultInt r = _accessor.readNumber(_deviceHandle, od);
            if (r.hasError())
                throw new DriveException($"Read failed ({what}): {r.getError()}");
            return r.getResult();
        }

        /// <summary>
        /// Writes Modes of Operation (0x6060) and blocks until the Modes-of-Operation-Display
        /// (0x6061) confirms the drive has actually switched, or the timeout elapses. The
        /// switch can lag the write by a cycle; callers that trigger motion immediately after
        /// must not race it. Best-effort on timeout (proceeds) so a drive that doesn't surface
        /// 0x6061 still works — it just loses the guarantee.
        /// </summary>
        private void SetModeOfOperation(sbyte mode, string what)
        {
            Write(mode, OD_ModesOfOp, BITS_8, $"mode: {what}");
            int waited = 0;
            while (waited < STATE_TIMEOUT_MS)
            {
                if ((sbyte)Read(OD_ModesDisplay, "modes of operation display") == mode) return;
                Thread.Sleep(POLL_STEP_MS);
                waited += POLL_STEP_MS;
            }
        }

        /// <summary>Polls the statusword until <paramref name="predicate"/> holds or it times out.
        /// If <paramref name="cancel"/> is supplied and returns true, throws
        /// <see cref="OperationCanceledException"/> so the caller can abort (e.g. an operator Stop).</summary>
        private long WaitForStatus(Func<long, bool> predicate, int timeoutMs, string what, Func<bool>? cancel = null)
        {
            int waited = 0;
            long sw = 0;
            while (waited < timeoutMs)
            {
                sw = Read(OD_Statusword, "statusword");
                if (predicate(sw)) return sw;
                if (cancel != null && cancel()) throw new OperationCanceledException($"cancelled waiting for {what}.");
                Thread.Sleep(POLL_STEP_MS);
                waited += POLL_STEP_MS;
            }
            throw new DriveException(
                $"Timed out after {timeoutMs} ms waiting for {what}. " +
                $"Last statusword=0x{sw:X4} (state 0x{sw & SW_STATE_MASK:X2}).");
        }

        // --- Public API ---------------------------------------------------------------

        public void EnableDrive(bool enable)
        {
            if (!enable)
            {
                Write(CW_DISABLE, OD_Controlword, BITS_16, "controlword: disable");
                return;
            }

            // If the drive is Faulted, the state machine ignores everything until a
            // fault reset is issued.
            long sw = Read(OD_Statusword, "statusword");
            if ((sw & SW_FAULT) != 0)
            {
                Write(CW_FAULT_RESET, OD_Controlword, BITS_16, "controlword: fault reset");
                WaitForStatus(s => (s & SW_FAULT) == 0, 1000, "fault to clear");
            }

            // Normalize to Switch-On-Disabled first. This recovers cleanly from a
            // Quick-Stop-Active or other leftover state (e.g. after an aborted/faulted
            // move) that a plain Shutdown would NOT transition out of.
            Write(CW_DISABLE, OD_Controlword, BITS_16, "controlword: disable voltage");
            WaitForStatus(s => (s & SW_STATE_MASK_SOD) == SW_STATE_SWITCH_ON_DISABLED,
                          STATE_TIMEOUT_MS, "Switch On Disabled");

            // Walk the state machine, confirming each transition instead of sleeping blindly.
            Write(CW_SHUTDOWN, OD_Controlword, BITS_16, "controlword: shutdown");
            WaitForStatus(s => (s & SW_STATE_MASK) == SW_STATE_READY_TO_SWITCH, STATE_TIMEOUT_MS, "Ready To Switch On");

            Write(CW_SWITCH_ON, OD_Controlword, BITS_16, "controlword: switch on");
            WaitForStatus(s => (s & SW_STATE_MASK) == SW_STATE_SWITCHED_ON, STATE_TIMEOUT_MS, "Switched On");

            // SAFETY: before entering Operation Enabled, force a known, NON-MOVING
            // setpoint. Otherwise the drive acts on whatever mode (0x6060) and target
            // (e.g. a leftover 0x60FF target velocity) it happens to hold the instant
            // it is enabled - which is what made an axis lurch on enable. Profile
            // Velocity + zero target + Halt = holding torque, no motion.
            Write(MODE_PROFILE_VELOCITY, OD_ModesOfOp, BITS_8, "mode: profile velocity (safe)");
            Write(0, OD_TargetVel, BITS_32, "target velocity: zero (safe)");
            Write(CW_HALT, OD_Controlword, BITS_16, "controlword: enable operation + halt");
            WaitForStatus(s => (s & SW_STATE_MASK) == SW_STATE_OP_ENABLED, STATE_TIMEOUT_MS, "Operation Enabled");
        }

        public void StartManualJog(int velocity)
        {
            Write(MODE_PROFILE_VELOCITY, OD_ModesOfOp, BITS_8, "mode: profile velocity");
            Write(velocity, OD_TargetVel, BITS_32, "target velocity");
            Write(CW_ENABLE_OPERATION, OD_Controlword, BITS_16, "controlword: run (clear halt)");
        }

        public void StopManualJog()
        {
            Write(0, OD_TargetVel, BITS_32, "target velocity: zero");
            Write(CW_HALT, OD_Controlword, BITS_16, "controlword: halt");
        }

        /// <summary>
        /// Velocity-only update to an ALREADY-RUNNING profile-velocity jog: rewrites just the
        /// 0x60FF target (one SDO transaction) instead of re-sending mode + controlword like
        /// <see cref="StartManualJog"/>. Zero decelerates to a servo hold WITHOUT the halt bit,
        /// so there is no halt/run controlword flipping around zero. Arm the axis with
        /// <see cref="StartManualJog"/> first; used by the crosshair-rotation follow loop, where
        /// three axes are re-commanded every tick and SDO traffic sets the loop period.
        /// </summary>
        public void UpdateJogVelocity(int velocity)
            => Write(velocity, OD_TargetVel, BITS_32, "target velocity (update)");

        /// <summary>Current profile accel/decel (0x6083/0x6084) — read so callers that need their
        /// own ramps (the crosshair-rotation follow loop) can save and later restore them.</summary>
        public (long Accel, long Decel) GetProfileRamp()
            => (Read(OD_ProfileAccel, "profile acceleration"),
                Read(OD_ProfileDecel, "profile deceleration"));

        /// <summary>
        /// Sets profile accel/decel (0x6083/0x6084), in counts/s². These bound how fast the drive
        /// chases a new 0x60FF target in profile-velocity mode too (not just profile-position), so
        /// a follow loop that rewrites the target every tick needs them high enough that each step
        /// is reached well within one tick — the drive's stored default is otherwise an unmodeled
        /// lag on every update.
        /// </summary>
        public void SetProfileRamp(long accel, long decel)
        {
            Write(accel, OD_ProfileAccel, BITS_32, "profile acceleration");
            Write(decel, OD_ProfileDecel, BITS_32, "profile deceleration");
        }

        // --- Profile Position (point-to-point) ---------------------------------------
        // Used by the step-and-settle scan. Positions/velocities are in the drive's
        // own units (counts / 0x60FF units) until factor-group conversion is wired in.
        // NOTE: unverified on hardware — confirm scaling before trusting magnitudes.

        /// <summary>Moves to an absolute target position (drive position units).</summary>
        public void MoveAbsolute(long targetPosition, int profileVelocity)
            => Move(targetPosition, profileVelocity, relative: false);

        /// <summary>Moves by a relative delta from the current position (drive position units).</summary>
        public void MoveRelative(long deltaPosition, int profileVelocity)
            => Move(deltaPosition, profileVelocity, relative: true);

        private void Move(long position, int profileVelocity, bool relative)
        {
            StartMove(position, profileVelocity, relative);
            FinishSetpoint();
        }

        private void StartMove(long position, int profileVelocity, bool relative)
        {
            // Switch to Profile Position and WAIT for the drive to actually enter it (0x6061).
            // The mode change is not instantaneous — on the rotary chuck it takes ~one cycle —
            // and triggering the new-set-point edge before the drive has left the previous mode
            // makes it read the set-point as velocity-mode bits and ignore the move (the axis
            // silently doesn't turn). Confirming the mode first fixes that.
            SetModeOfOperation(MODE_PROFILE_POSITION, "profile position");
            Write(profileVelocity, OD_ProfileVelocity, BITS_32, "profile velocity");
            Write(position, OD_TargetPosition, BITS_32, "target position");

            // Ensure bit 4 is low, then set it (with change-immediately + abs/rel) so a fresh
            // move is always triggered even if bit 4 was left high.
            Write(CW_ENABLE_OPERATION, OD_Controlword, BITS_16, "controlword: clear set-point");
            Write(relative ? CW_PP_NEWSETPOINT_REL : CW_PP_NEWSETPOINT_ABS,
                  OD_Controlword, BITS_16, "controlword: new set-point");
        }

        /// <summary>
        /// Completes a latched move: waits for the drive to ACKNOWLEDGE the new set-point
        /// (statusword bit 12), then drops bit 4 so the next move can latch. Accepting a set-point
        /// CLEARS Target-Reached, so this is what stops a following <see cref="WaitForMotionComplete"/>
        /// from latching onto the PREVIOUS move's stale Target-Reached and reporting "done" before
        /// the axis has started. If the firmware never raises bit 12, the wait still elapses long
        /// enough for the set-point to be accepted and Target-Reached cleared.
        /// </summary>
        private void FinishSetpoint()
        {
            try { WaitForStatus(s => (s & SW_SETPOINT_ACK) != 0, STATE_TIMEOUT_MS, "set-point acknowledge"); }
            catch (DriveException) { /* bit 12 not surfaced; the elapsed wait is the settle */ }

            // Drop the new-set-point bit so the drive clears the acknowledge and can latch the
            // next move. The move itself continues (change-immediately was set above).
            Write(CW_ENABLE_OPERATION, OD_Controlword, BITS_16, "controlword: release set-point");
        }

        /// <summary>
        /// Blocks until the drive reports Target Reached (statusword bit 10) or the
        /// timeout elapses. Returns false on timeout. Reliable because <see cref="Move"/>
        /// waits for the set-point to be acknowledged (which clears the previous move's
        /// Target-Reached) before this is called. For step-and-settle scanning: issue a
        /// Move, then wait on this before grabbing a frame.
        /// </summary>
        public bool WaitForMotionComplete(int timeoutMs, Func<bool>? cancel = null)
        {
            try
            {
                WaitForStatus(s => (s & SW_TARGET_REACHED) != 0, timeoutMs, "target reached", cancel);
                return true;
            }
            catch (DriveException)
            {
                return false;
            }
            // OperationCanceledException (operator Stop) is intentionally NOT caught — it propagates
            // so the caller abandons the move (and its follow-on steps) instead of proceeding.
        }

        /// <summary>
        /// Runs a homing cycle that establishes the current physical position as zero.
        /// Returns true once homing is attained; false on a drive-reported homing error
        /// or timeout. Throws <see cref="DriveException"/> on a communication failure.
        /// </summary>
        public bool SynchronizeEncoderToPhysicalZero()
        {
            // Home offset is latched at the START of a homing run, so it must be written
            // before the start command, not after (writing it afterwards has no effect).
            Write(0, OD_HomeOffset, BITS_32, "home offset");
            Write(MODE_HOMING, OD_ModesOfOp, BITS_8, "mode: homing");
            Write(HOMING_METHOD_CURRENT_POSITION, OD_HomingMethod, BITS_8, "homing method");
            Write(CW_START_HOMING, OD_Controlword, BITS_16, "controlword: start homing");

            int waited = 0;
            while (waited < HOMING_TIMEOUT_MS)
            {
                long status = Read(OD_Statusword, "statusword");
                if ((status & SW_HOMING_ERROR) != 0) return false;

                // Per CiA 402, homing is complete when both "homing attained" (bit 12)
                // and "target reached" (bit 10) are set. If your drive only sets bit 12
                // during homing, relax this to test SW_HOMING_ATTAINED alone.
                if ((status & SW_HOMING_ATTAINED) != 0 && (status & SW_TARGET_REACHED) != 0)
                    return true;

                Thread.Sleep(HOMING_POLL_MS);
                waited += HOMING_POLL_MS;
            }
            return false;
        }

        /// <summary>
        /// Raw Digital Inputs object 0x60FD. CiA bits: 0 = negative limit, 1 = positive
        /// limit, 2 = home; bits 16+ = raw physical inputs. The calibration limit-find
        /// watches bits 0/1 to detect a switch.
        /// </summary>
        public long ReadDigitalInputs() => Read(OD_DigitalInputs, "digital inputs 0x60FD");

        /// <summary>
        /// True if the drive is held in Quick-Stop-Active (state 0x07) — what a limit hit
        /// leaves it in on this machine. While in this state the drive ignores motion
        /// commands; only a re-enable (<see cref="EnableDrive"/>(true), which normalises via
        /// Disable Voltage → Switch-On-Disabled) clears it. A plain jog/controlword 0x0F does
        /// NOT reliably recover it here.
        /// </summary>
        public bool IsQuickStopped()
            => (Read(OD_Statusword, "statusword") & SW_STATE_MASK) == SW_STATE_QUICK_STOP_ACTIVE;

        /// <summary>
        /// Writes an arbitrary object-dictionary entry. Expert/manual use (the "Write Object"
        /// console) — there is no validation beyond NanoLib's, so it can change any writable
        /// drive setting. <paramref name="bitLength"/> must match the object size (8/16/32).
        /// </summary>
        public void WriteObject(ushort index, byte subIndex, long value, uint bitLength)
            => Write(value, new OdIndex(index, subIndex), bitLength,
                     $"manual write 0x{index:X4}:{subIndex:X2}");

        /// <summary>
        /// Persists the drive's CURRENT parameter values to non-volatile memory by writing the
        /// "save" signature to object 0x1010:01, so a prior <see cref="WriteObject"/> survives a
        /// power-cycle. Saves the whole parameter set, not just the last write.
        /// </summary>
        public void SaveParametersToNV()
            => Write(STORE_SIGNATURE, OD_StoreParameters, BITS_32, "store parameters to NV (0x1010:01)");

        /// <summary>
        /// Reads 0x6064 (Position Actual Value) as a SIGNED 32-bit count. NanoLib returns
        /// the raw object zero-extended, so a negative position would otherwise come back
        /// as ~4.29 billion (e.g. -117863 → 4294849433) and corrupt any maths on it. The
        /// (int) cast reinterprets the low 32 bits as two's-complement.
        /// </summary>
        private long ReadPosition() => (int)Read(OD_PosActual, "actual position");

        public double GetActualChuckAngle()
        {
            long rawTicks = ReadPosition();
            return TicksToAngle(rawTicks);
        }

        /// <summary>Position-only read (one SDO transaction, half of <see cref="GetStatus"/>)
        /// for fast follow loops that don't need the CiA 402 state each tick.</summary>
        public long GetPosition() => ReadPosition();

        /// <summary>Reads angle + decoded CiA 402 state in one go, for live display.</summary>
        public AxisStatus GetStatus()
        {
            long sw = Read(OD_Statusword, "statusword");
            long rawTicks = ReadPosition();
            return new AxisStatus
            {
                Position = rawTicks,
                AngleDegrees = TicksToAngle(rawTicks),
                State = DecodeState(sw),
                HasFault = (sw & SW_FAULT) != 0,
            };
        }

        private static double TicksToAngle(long rawTicks)
        {
            double angle = (double)(rawTicks % ENCODER_TICKS_PER_REV) / ENCODER_TICKS_PER_REV * 360.0;
            if (angle < 0) angle += 360.0;
            return angle;
        }

        private static string DecodeState(long sw)
        {
            if ((sw & SW_FAULT) != 0) return "Fault";
            return (sw & SW_STATE_MASK) switch
            {
                SW_STATE_OP_ENABLED        => "Operation Enabled",
                SW_STATE_SWITCHED_ON       => "Switched On",
                SW_STATE_READY_TO_SWITCH   => "Ready",
                SW_STATE_QUICK_STOP_ACTIVE => "Quick Stop",
                SW_STATE_FAULT_REACTION    => "Fault Reaction",
                _                          => $"State 0x{sw & SW_STATE_MASK:X2}",
            };
        }
    }
}
