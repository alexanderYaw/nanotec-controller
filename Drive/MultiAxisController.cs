using System;
using System.Collections.Generic;

namespace NanotecController
{
    /// <summary>
    /// The shared motion API for the inspection table. One per-axis controller is
    /// built over each connected drive (mapped by <see cref="AxisConfig.BusPosition"/>),
    /// and every consumer — the joystick, the GUI buttons, and later the automation
    /// sequencer — drives the table through THIS class, never the drives directly.
    ///
    /// Threading: NanoLib is single-channel per device, so every method takes the channel
    /// lock. That is what lets <see cref="DrivePoller"/> read on its own thread while the UI
    /// thread commands. Re-entrant, so the multi-step ops nest on one thread.
    /// </summary>
    public sealed class MultiAxisController
    {
        private readonly Dictionary<AxisId, AxisDriver> _axes = new();
        private readonly object _channel = new();

        /// <summary>
        /// Builds a controller per axis from the connection's handles.
        /// Throws if a config points at a bus position that wasn't connected, so a
        /// mis-count is caught here rather than as a null move later.
        /// </summary>
        public MultiAxisController(MultiAxisConnection connection, IReadOnlyList<AxisConfig> configs)
        {
            if (connection.Accessor == null || !connection.IsConnected)
                throw new InvalidOperationException("Connection is not established.");

            foreach (AxisConfig cfg in configs)
            {
                if (cfg.BusPosition < 0 || cfg.BusPosition >= connection.Handles.Count)
                    throw new ArgumentOutOfRangeException(nameof(configs),
                        $"Axis {cfg.Id} ('{cfg.Name}') maps to bus position {cfg.BusPosition}, " +
                        $"but only {connection.Handles.Count} drive(s) are connected.");

                _axes[cfg.Id] = new AxisDriver(
                    connection.Accessor, connection.Handles[cfg.BusPosition], cfg);
            }
        }

        public IReadOnlyCollection<AxisId> Axes => _axes.Keys;

        public bool Has(AxisId id) => _axes.ContainsKey(id);

        /// <summary>
        /// Looks up one axis. Throws <see cref="DriveException"/> — not KeyNotFoundException — for an
        /// axis that isn't in the config, so a mapping slip surfaces through the same
        /// <c>catch (DriveException)</c> every caller already has instead of escaping as an unhandled
        /// crash. Private: reaching an AxisDriver from outside would bypass the channel lock.
        /// </summary>
        private AxisDriver Axis(AxisId id)
            => _axes.TryGetValue(id, out AxisDriver? a)
                ? a
                : throw new DriveException($"Axis {id} is not connected (not in the axis map).");

        // --- Enable / disable -----------------------------------------------------

        /// <summary>Enables every axis (walks each through the CiA 402 state machine).</summary>
        public void EnableAll()
        {
            lock (_channel)
                foreach (AxisDriver axis in _axes.Values)
                    axis.EnableDrive(true);
        }

        /// <summary>Enables ONE axis — the way out of Quick-Stop when the caller already knows the
        /// axis is parked there (the limit-find). <see cref="RecoverIfQuickStopped"/> checks first.</summary>
        public void EnableAxis(AxisId id)
        {
            lock (_channel) Axis(id).EnableDrive(true);
        }

        /// <summary>Stops then disables every axis. Best-effort: never throws.</summary>
        public void DisableAll()
        {
            lock (_channel)
                foreach (AxisDriver axis in _axes.Values)
                {
                    try { axis.StopManualJog(); } catch (DriveException) { }
                    try { axis.EnableDrive(false); } catch (DriveException) { }
                }
        }

        /// <summary>
        /// If the axis is held in Quick-Stop-Active (e.g. after a limit hit), clears it by
        /// re-enabling so a following move can take effect, and returns true. No-op (returns
        /// false) when the axis is healthy, so a holding axis (e.g. Z under gravity) is NOT
        /// briefly de-energised unnecessarily. Call this before commanding a move/jog that
        /// must run even after a limit stop.
        /// </summary>
        public bool RecoverIfQuickStopped(AxisId id)
        {
            lock (_channel)
            {
                AxisDriver axis = Axis(id);
                if (!axis.IsQuickStopped()) return false;
                axis.EnableDrive(true);
                return true;
            }
        }

        // --- Jog (joystick / manual) ----------------------------------------------

        /// <summary>
        /// Jogs one axis at an explicit speed (drive velocity units), applying the
        /// axis's InvertDirection. Direction is -1/0/+1; 0 stops. Used by the GUI jog
        /// buttons, where the speed comes from a UI control rather than the config.
        /// </summary>
        public void JogAt(AxisId id, int direction, int speed)
        {
            lock (_channel)
            {
                AxisDriver axis = Axis(id);
                if (direction == 0) { axis.StopManualJog(); return; }
                int sign = Math.Sign(direction) * (axis.Config.InvertDirection ? -1 : 1);
                axis.StartManualJog(speed * sign);
            }
        }

        /// <summary>
        /// Velocity-only update to an already-running jog (one SDO write; no mode/controlword
        /// traffic, and 0 holds at zero velocity WITHOUT the halt bit — see
        /// <see cref="AxisDriver.UpdateJogVelocity"/>). Applies InvertDirection exactly like
        /// <see cref="JogAt"/>; direction 0 commands zero velocity. Arm the axis with
        /// <see cref="JogAt"/> first.
        /// </summary>
        public void UpdateJogVelocity(AxisId id, int direction, int speed)
        {
            lock (_channel)
            {
                AxisDriver axis = Axis(id);
                int sign = Math.Sign(direction) * (axis.Config.InvertDirection ? -1 : 1);
                axis.UpdateJogVelocity(speed * sign);
            }
        }

        /// <summary>Current profile accel/decel (0x6083/0x6084) of one axis, for save/restore.</summary>
        public (long Accel, long Decel) GetProfileRamp(AxisId id)
        {
            lock (_channel) return Axis(id).GetProfileRamp();
        }

        /// <summary>Sets profile accel/decel (0x6083/0x6084) of one axis, in counts/s² (see
        /// <see cref="AxisDriver.SetProfileRamp"/>).</summary>
        public void SetProfileRamp(AxisId id, long accel, long decel)
        {
            lock (_channel) Axis(id).SetProfileRamp(accel, decel);
        }

        public void Stop(AxisId id)
        {
            lock (_channel) Axis(id).StopManualJog();
        }

        /// <summary>Halts all axes. Best-effort: never throws (safety path).</summary>
        public void StopAll()
        {
            lock (_channel)
                foreach (AxisDriver axis in _axes.Values)
                {
                    try { axis.StopManualJog(); } catch (DriveException) { }
                }
        }

        // --- Positioning (Profile Position) ---------------------------------------

        public void MoveAbsolute(AxisId id, long targetPosition, int profileVelocity)
        {
            lock (_channel) Axis(id).MoveAbsolute(targetPosition, profileVelocity);
        }

        public void MoveRelative(AxisId id, long deltaPosition, int profileVelocity)
        {
            lock (_channel) Axis(id).MoveRelative(deltaPosition, profileVelocity);
        }

        public bool WaitForMotionComplete(AxisId id, int timeoutMs, Func<bool>? cancel = null)
        {
            lock (_channel) return Axis(id).WaitForMotionComplete(timeoutMs, cancel);
        }

        // --- Status ----------------------------------------------------------------

        public AxisDriver.AxisStatus GetStatus(AxisId id)
        {
            lock (_channel) return Axis(id).GetStatus();
        }

        /// <summary>Position-only read (one SDO transaction) for fast follow loops.</summary>
        public long GetPosition(AxisId id)
        {
            lock (_channel) return Axis(id).GetPosition();
        }

        /// <summary>CiA 402 state-only read (one SDO transaction) — the poller's slow lane.</summary>
        public (string State, bool HasFault) GetState(AxisId id)
        {
            lock (_channel) return Axis(id).GetState();
        }

        /// <summary>Raw 0x60FD digital inputs for one axis (limit-switch bits drive the calibration find).</summary>
        public long GetDigitalInputs(AxisId id)
        {
            lock (_channel) return Axis(id).ReadDigitalInputs();
        }

        /// <summary>Analogue input 1 (0x3220:01) of one axis's drive — the wired analog joystick pot.</summary>
        public int GetAnalogInput1(AxisId id)
        {
            lock (_channel) return Axis(id).ReadAnalogInput1();
        }

        // --- Expert: arbitrary object write + NV save (the "Write Object" console) -----

        /// <summary>Writes an arbitrary OD entry on one axis. Expert/manual use only.</summary>
        public void WriteObject(AxisId id, ushort index, byte subIndex, long value, uint bitLength)
        {
            lock (_channel) Axis(id).WriteObject(index, subIndex, value, bitLength);
        }

        /// <summary>Reads an arbitrary OD entry on one axis (read counterpart to <see cref="WriteObject"/>).</summary>
        public long GetObject(AxisId id, ushort index, byte subIndex)
        {
            lock (_channel) return Axis(id).ReadObject(index, subIndex);
        }

        /// <summary>Persists one axis's current parameters to non-volatile memory (0x1010:01 = "save").</summary>
        public void SaveParametersToNV(AxisId id)
        {
            lock (_channel) Axis(id).SaveParametersToNV();
        }
    }
}
