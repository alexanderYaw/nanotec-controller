using System;
using System.Threading;
using Nlc;

namespace NanotecController
{
    /// <summary>Raised when a NanoLib operation fails or a drive misses an expected CiA 402 state.</summary>
    public class DriveException : Exception
    {
        public DriveException(string message) : base(message) { }
    }

    /// <summary>
    /// Drives ONE axis (X / Y / Z / Θ) over NanoLib via the CiA 402 state machine: enable/disable,
    /// profile-velocity jog, profile-position moves, homing and status reads. One instance per
    /// connected drive; <see cref="MultiAxisController"/> owns the set.
    /// </summary>
    public class AxisDriver
    {
        #region Object dictionary indices

        private readonly OdIndex OD_Controlword    = new OdIndex(0x6040, 0x00);
        private readonly OdIndex OD_Statusword     = new OdIndex(0x6041, 0x00);
        private readonly OdIndex OD_ModesOfOp      = new OdIndex(0x6060, 0x00);
        private readonly OdIndex OD_ModesDisplay   = new OdIndex(0x6061, 0x00);
        private readonly OdIndex OD_PosActual      = new OdIndex(0x6064, 0x00);
        private readonly OdIndex OD_TargetVel      = new OdIndex(0x60FF, 0x00);
        private readonly OdIndex OD_HomeOffset     = new OdIndex(0x607C, 0x00);
        private readonly OdIndex OD_HomingMethod   = new OdIndex(0x6098, 0x00);
        private readonly OdIndex OD_DigitalInputs  = new OdIndex(0x60FD, 0x00);
        private readonly OdIndex OD_AnalogInput1   = new OdIndex(0x3220, 0x01);
        private readonly OdIndex OD_StoreParameters = new OdIndex(0x1010, 0x01);

        private readonly OdIndex OD_TargetPosition  = new OdIndex(0x607A, 0x00);
        private readonly OdIndex OD_ProfileVelocity = new OdIndex(0x6081, 0x00);
        private readonly OdIndex OD_ProfileAccel    = new OdIndex(0x6083, 0x00);
        private readonly OdIndex OD_ProfileDecel    = new OdIndex(0x6084, 0x00);

        #endregion

        #region Constants

        /// <summary>Encoder ticks per MOTOR revolution. Θ turns through a ~9:1 reduction, so chuck
        /// angles come from <see cref="CrosshairRotation.ChuckTicksPerRev"/> instead.</summary>
        public const long ENCODER_TICKS_PER_REV = 40000;

        private const uint BITS_8  = 8;
        private const uint BITS_16 = 16;
        private const uint BITS_32 = 32;

        private const long STORE_SIGNATURE = 0x65766173;   // ASCII "save"

        private const ushort CW_DISABLE          = 0x0000;
        private const ushort CW_SHUTDOWN         = 0x0006;
        private const ushort CW_SWITCH_ON        = 0x0007;
        private const ushort CW_ENABLE_OPERATION = 0x000F;
        private const ushort CW_FAULT_RESET      = 0x0080;
        private const ushort CW_HALT             = 0x010F;
        private const ushort CW_START_HOMING     = 0x001F;
        private const ushort CW_PP_NEWSETPOINT_ABS = 0x003F;
        private const ushort CW_PP_NEWSETPOINT_REL = 0x007F;

        private const long SW_FAULT           = 0x0008;
        private const long SW_TARGET_REACHED  = 0x0400;
        private const long SW_SETPOINT_ACK    = 0x1000;   // bit 12 in profile-position mode
        private const long SW_HOMING_ATTAINED = 0x1000;   // same bit, homing mode
        private const long SW_HOMING_ERROR    = 0x2000;

        private const long SW_STATE_MASK              = 0x006F;
        private const long SW_STATE_READY_TO_SWITCH   = 0x0021;
        private const long SW_STATE_SWITCHED_ON       = 0x0023;
        private const long SW_STATE_OP_ENABLED        = 0x0027;
        private const long SW_STATE_QUICK_STOP_ACTIVE = 0x0007;
        private const long SW_STATE_FAULT_REACTION    = 0x000F;
        private const long SW_STATE_MASK_SOD           = 0x004F;
        private const long SW_STATE_SWITCH_ON_DISABLED = 0x0040;

        private const sbyte MODE_PROFILE_POSITION = 1;
        private const sbyte MODE_PROFILE_VELOCITY = 3;
        private const sbyte MODE_HOMING           = 6;

        private const long HOMING_METHOD_CURRENT_POSITION = 34;

        private const int STATE_TIMEOUT_MS  = 500;
        private const int FAULT_RESET_TIMEOUT_MS = 1000;
        private const int HOMING_TIMEOUT_MS = 10000;
        private const int POLL_STEP_MS      = 10;
        private const int HOMING_POLL_MS    = 100;

        #endregion

        #region Construction

        private readonly NanoLibAccessor _accessor;
        private readonly DeviceHandle _deviceHandle;

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
            /// <summary>Actual position (0x6064), in the drive's configured position units.</summary>
            public long Position { get; init; }
            public string State { get; init; }
            public bool HasFault { get; init; }
        }

        #endregion

        #region Checked NanoLib access

        /// <summary>Writes an object, turning a NanoLib error result into a <see cref="DriveException"/>.</summary>
        private void Write(long value, OdIndex od, uint bitLength, string what)
        {
            using ResultVoid r = _accessor.writeNumber(_deviceHandle, value, od, bitLength);
            if (r.hasError())
                throw new DriveException($"Write failed ({what}): {r.getError()}");
        }

        /// <summary>Reads an object, turning a NanoLib error result into a <see cref="DriveException"/>
        /// rather than the silent 0 <c>getResult()</c> would return.</summary>
        private long Read(OdIndex od, string what)
        {
            using ResultInt r = _accessor.readNumber(_deviceHandle, od);
            if (r.hasError())
                throw new DriveException($"Read failed ({what}): {r.getError()}");
            return r.getResult();
        }

        /// <summary>Writes Modes of Operation (0x6060) and blocks until 0x6061 confirms the switch,
        /// so callers triggering motion immediately after don't race it. Best-effort on timeout.</summary>
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
        /// A <paramref name="cancel"/> returning true throws <see cref="OperationCanceledException"/>
        /// so the caller can abort (e.g. an operator Stop).</summary>
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

        #endregion

        #region Enable and jog

        /// <summary>Walks the CiA 402 state machine to Operation Enabled, confirming each transition.
        /// Resets a fault, normalises via Switch-On-Disabled (which also clears Quick-Stop-Active),
        /// and enters Operation Enabled on a zero-velocity halted set-point so no axis lurches.</summary>
        public void EnableDrive(bool enable)
        {
            if (!enable)
            {
                Write(CW_DISABLE, OD_Controlword, BITS_16, "controlword: disable");
                return;
            }

            long sw = Read(OD_Statusword, "statusword");
            if ((sw & SW_FAULT) != 0)
            {
                Write(CW_FAULT_RESET, OD_Controlword, BITS_16, "controlword: fault reset");
                WaitForStatus(s => (s & SW_FAULT) == 0, FAULT_RESET_TIMEOUT_MS, "fault to clear");
            }

            Write(CW_DISABLE, OD_Controlword, BITS_16, "controlword: disable voltage");
            WaitForStatus(s => (s & SW_STATE_MASK_SOD) == SW_STATE_SWITCH_ON_DISABLED,
                          STATE_TIMEOUT_MS, "Switch On Disabled");

            Write(CW_SHUTDOWN, OD_Controlword, BITS_16, "controlword: shutdown");
            WaitForStatus(s => (s & SW_STATE_MASK) == SW_STATE_READY_TO_SWITCH, STATE_TIMEOUT_MS, "Ready To Switch On");

            Write(CW_SWITCH_ON, OD_Controlword, BITS_16, "controlword: switch on");
            WaitForStatus(s => (s & SW_STATE_MASK) == SW_STATE_SWITCHED_ON, STATE_TIMEOUT_MS, "Switched On");

            Write(MODE_PROFILE_VELOCITY, OD_ModesOfOp, BITS_8, "mode: profile velocity (safe)");
            Write(0, OD_TargetVel, BITS_32, "target velocity: zero (safe)");
            Write(CW_HALT, OD_Controlword, BITS_16, "controlword: enable operation + halt");
            WaitForStatus(s => (s & SW_STATE_MASK) == SW_STATE_OP_ENABLED, STATE_TIMEOUT_MS, "Operation Enabled");
        }

        /// <summary>Arms profile-velocity mode at <paramref name="velocity"/> and clears the halt bit.</summary>
        public void StartManualJog(int velocity)
        {
            Write(MODE_PROFILE_VELOCITY, OD_ModesOfOp, BITS_8, "mode: profile velocity");
            Write(velocity, OD_TargetVel, BITS_32, "target velocity");
            Write(CW_ENABLE_OPERATION, OD_Controlword, BITS_16, "controlword: run (clear halt)");
        }

        /// <summary>Zeroes the target velocity and re-asserts halt.</summary>
        public void StopManualJog()
        {
            Write(0, OD_TargetVel, BITS_32, "target velocity: zero");
            Write(CW_HALT, OD_Controlword, BITS_16, "controlword: halt");
        }

        /// <summary>Velocity-only update to an already-running jog — one SDO write, no controlword
        /// flipping around zero. Arm with <see cref="StartManualJog"/> first. Used by the
        /// crosshair-rotation follow loop, where SDO traffic sets the loop period.</summary>
        public void UpdateJogVelocity(int velocity)
            => Write(velocity, OD_TargetVel, BITS_32, "target velocity (update)");

        /// <summary>Current profile accel/decel (0x6083/0x6084), so a caller can restore them later.</summary>
        public (long Accel, long Decel) GetProfileRamp()
            => (Read(OD_ProfileAccel, "profile acceleration"),
                Read(OD_ProfileDecel, "profile deceleration"));

        /// <summary>Sets profile accel/decel in counts/s². These also bound how fast the drive chases
        /// a new 0x60FF target, so a follow loop needs them high enough to settle within one tick.</summary>
        public void SetProfileRamp(long accel, long decel)
        {
            Write(accel, OD_ProfileAccel, BITS_32, "profile acceleration");
            Write(decel, OD_ProfileDecel, BITS_32, "profile deceleration");
        }

        #endregion

        #region Profile position

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

        /// <summary>Enters profile-position mode (waiting on 0x6061, else the set-point edge is read as
        /// velocity bits and the move is silently ignored) and latches the new set-point.</summary>
        private void StartMove(long position, int profileVelocity, bool relative)
        {
            SetModeOfOperation(MODE_PROFILE_POSITION, "profile position");
            Write(profileVelocity, OD_ProfileVelocity, BITS_32, "profile velocity");
            Write(position, OD_TargetPosition, BITS_32, "target position");

            Write(CW_ENABLE_OPERATION, OD_Controlword, BITS_16, "controlword: clear set-point");
            Write(relative ? CW_PP_NEWSETPOINT_REL : CW_PP_NEWSETPOINT_ABS,
                  OD_Controlword, BITS_16, "controlword: new set-point");
        }

        /// <summary>Waits for set-point acknowledge (bit 12) then drops bit 4. The acknowledge clears the
        /// PREVIOUS move's Target-Reached, so <see cref="WaitForMotionComplete"/> can't latch onto it.
        /// If the firmware never raises bit 12 the elapsed wait is itself the settle.</summary>
        private void FinishSetpoint()
        {
            try { WaitForStatus(s => (s & SW_SETPOINT_ACK) != 0, STATE_TIMEOUT_MS, "set-point acknowledge"); }
            catch (DriveException) { }

            Write(CW_ENABLE_OPERATION, OD_Controlword, BITS_16, "controlword: release set-point");
        }

        /// <summary>Blocks until Target Reached (bit 10); false on timeout. An operator Stop propagates as
        /// <see cref="OperationCanceledException"/> so the caller abandons the move.</summary>
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
        }

        #endregion

        #region Homing

        /// <summary>Runs a homing cycle establishing the current physical position as zero. False on a
        /// drive-reported homing error or timeout; throws <see cref="DriveException"/> on a link failure.</summary>
        public bool SynchronizeEncoderToPhysicalZero()
        {
            // Home offset is latched at the START of a run, so it must precede the start command.
            Write(0, OD_HomeOffset, BITS_32, "home offset");
            Write(MODE_HOMING, OD_ModesOfOp, BITS_8, "mode: homing");
            Write(HOMING_METHOD_CURRENT_POSITION, OD_HomingMethod, BITS_8, "homing method");
            Write(CW_START_HOMING, OD_Controlword, BITS_16, "controlword: start homing");

            int waited = 0;
            while (waited < HOMING_TIMEOUT_MS)
            {
                long status = Read(OD_Statusword, "statusword");
                if ((status & SW_HOMING_ERROR) != 0) return false;
                if ((status & SW_HOMING_ATTAINED) != 0 && (status & SW_TARGET_REACHED) != 0)
                    return true;

                Thread.Sleep(HOMING_POLL_MS);
                waited += HOMING_POLL_MS;
            }
            return false;
        }

        #endregion

        #region Raw object access

        /// <summary>Digital Inputs 0x60FD. Bits 0/1/2 = negative limit / positive limit / home;
        /// bits 16+ are the raw physical inputs.</summary>
        public long ReadDigitalInputs() => Read(OD_DigitalInputs, "digital inputs 0x60FD");

        /// <summary>Analogue input 1 (0x3220:01) — the drive-wired joystick pot, 16-bit signed.</summary>
        public int ReadAnalogInput1() => (short)Read(OD_AnalogInput1, "analog input 0x3220:01");

        /// <summary>Writes an arbitrary object. Expert/manual use — no validation beyond NanoLib's.
        /// <paramref name="bitLength"/> must match the object size (8/16/32).</summary>
        public void WriteObject(ushort index, byte subIndex, long value, uint bitLength)
            => Write(value, new OdIndex(index, subIndex), bitLength,
                     $"manual write 0x{index:X4}:{subIndex:X2}");

        /// <summary>Reads an arbitrary object. Returns the value NanoLib zero-extends into a long;
        /// the caller casts to the object's signed type.</summary>
        public long ReadObject(ushort index, byte subIndex)
            => Read(new OdIndex(index, subIndex), $"read 0x{index:X4}:{subIndex:X2}");

        /// <summary>Persists the drive's whole current parameter set to NV memory (0x1010:01).</summary>
        public void SaveParametersToNV()
            => Write(STORE_SIGNATURE, OD_StoreParameters, BITS_32, "store parameters to NV (0x1010:01)");

        #endregion

        #region Status

        /// <summary>Reads 0x6064 as a SIGNED 32-bit count — NanoLib zero-extends, so without the cast a
        /// negative position comes back as ~4.29 billion.</summary>
        private long ReadPosition() => (int)Read(OD_PosActual, "actual position");

        /// <summary>Position-only read (one SDO transaction) for fast follow loops.</summary>
        public long GetPosition() => ReadPosition();

        /// <summary>State-only read (one SDO transaction). <see cref="DrivePoller"/> refreshes this at a
        /// fraction of the position rate. <c>QuickStopped</c> is the same statusword decoded as
        /// <see cref="IsQuickStopped"/> does, so auto-recovery needs no extra read.</summary>
        public (string State, bool HasFault, bool QuickStopped) GetState()
        {
            long sw = Read(OD_Statusword, "statusword");
            return (DecodeState(sw), (sw & SW_FAULT) != 0,
                    (sw & SW_STATE_MASK) == SW_STATE_QUICK_STOP_ACTIVE);
        }

        /// <summary>Reads position + decoded CiA 402 state in one go, for live display.</summary>
        public AxisStatus GetStatus()
        {
            long sw = Read(OD_Statusword, "statusword");
            return new AxisStatus
            {
                Position = ReadPosition(),
                State = DecodeState(sw),
                HasFault = (sw & SW_FAULT) != 0,
            };
        }

        /// <summary>True if a limit hit has left the drive in Quick-Stop-Active, where it ignores motion
        /// commands until <see cref="EnableDrive"/>(true) normalises it via Switch-On-Disabled.</summary>
        public bool IsQuickStopped()
            => (Read(OD_Statusword, "statusword") & SW_STATE_MASK) == SW_STATE_QUICK_STOP_ACTIVE;

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

        #endregion
    }
}
