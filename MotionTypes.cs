using System;

namespace MotorControlApp
{
    /// <summary>The four mechanical axes of the inspection table.</summary>
    public enum AxisId
    {
        X,
        Y,
        Z,
        Theta, // the rotary chuck
    }

    /// <summary>
    /// Per-axis configuration for the multi-axis motion layer.
    ///
    /// <para><b>Axis identity.</b> All four drives report EtherCAT NodeID 1, so an
    /// axis is identified by its <see cref="BusPosition"/> — its index in the
    /// daisy-chain scan order (exactly how PD Studio enumerates them). The mapping
    /// of bus position → mechanical axis is established once at bring-up via the
    /// wiggle test (jog each, watch which axis moves) and corrected here if X/Y are
    /// swapped, etc.</para>
    ///
    /// <para><b>Units.</b> Jog velocities are in the drive's own velocity units
    /// (object 0x60FF) — NOT yet converted to mm/deg. Per the Nanotec example the
    /// unit is typically rpm, so keep these LOW until verified on hardware. Real
    /// unit conversion (counts↔mm, counts↔deg) belongs with MoveAbsolute and will
    /// read the factor-group objects (0x6091/0x6092) live from the drive.</para>
    /// </summary>
    public sealed class AxisConfig
    {
        public AxisId Id { get; init; }

        /// <summary>Human-readable label shown in the UI / logs (e.g. "X", "Chuck").</summary>
        public string Name { get; init; } = "";

        /// <summary>Index in the EtherCAT scan order this axis is wired at (0-based).</summary>
        public int BusPosition { get; init; }

        /// <summary>Initial per-axis jog speed the slider starts at (drive velocity units).</summary>
        public int JogVelocityDefault { get; init; } = 100;

        /// <summary>Upper limit of the per-axis jog-speed slider (drive velocity units).</summary>
        public int JogVelocityMax { get; init; } = 2000;

        /// <summary>
        /// If true, a positive jog command drives the motor in the negative
        /// direction. Lets the joystick "up/right = +" feel match the mechanics
        /// without rewiring or changing polarity on the drive.
        /// </summary>
        public bool InvertDirection { get; init; }

        /// <summary>
        /// Optional host-side soft travel limits in the drive's position units
        /// (object 0x6064). Null disables the check. These are a convenience guard
        /// for jogging; the drive's own limit objects (0x607D) remain authoritative.
        /// </summary>
        public long? MinPosition { get; init; }
        public long? MaxPosition { get; init; }
    }

    /// <summary>
    /// The inspection table's physical axis layout. Confirmed EtherCAT scan order on
    /// this machine is <b>X, Y, Z, Θ</b> (bus positions 0..3). This is the single
    /// source of truth for the bus-position → axis mapping; the joystick, GUI, and
    /// diagnostics all reference it. Jog speeds are placeholders in drive units —
    /// verify scaling on hardware before raising them (units are configurable per axis).
    /// </summary>
    public static class TableAxes
    {
        public static IReadOnlyList<AxisConfig> Default { get; } = new[]
        {
            new AxisConfig { Id = AxisId.X,     Name = "X",     BusPosition = 0, JogVelocityDefault = 4000, JogVelocityMax = 6000 },
            new AxisConfig { Id = AxisId.Y,     Name = "Y",     BusPosition = 1, JogVelocityDefault = 4000, JogVelocityMax = 12000 },
            new AxisConfig { Id = AxisId.Z,     Name = "Z",     BusPosition = 2, JogVelocityDefault = 300,  JogVelocityMax = 800},
            new AxisConfig { Id = AxisId.Theta, Name = "Theta", BusPosition = 3, JogVelocityDefault = 400,  JogVelocityMax = 800},
        };

        /// <summary>Axis label for a bus position (for logs / readouts). "?" if out of range.</summary>
        public static string NameForBusPosition(int busPosition)
            => busPosition >= 0 && busPosition < Default.Count ? Default[busPosition].Name : "?";
    }
}
