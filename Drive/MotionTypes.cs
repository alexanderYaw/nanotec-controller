using System;

namespace NanotecController
{
    /// <summary>The four mechanical axes of the inspection table.</summary>
    public enum AxisId
    {
        X,
        Y,
        Z,
        Theta,
    }

    /// <summary>
    /// Per-axis configuration for the motion layer. All four drives report EtherCAT NodeID 1, so an
    /// axis is identified by its <see cref="BusPosition"/> in the daisy-chain scan order. Jog
    /// velocities are in the drive's own units (0x60FF), not mm/deg. Soft travel limits live in
    /// <see cref="CalibrationStore"/>, not here.
    /// </summary>
    public sealed record AxisConfig
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

        /// <summary>Set where the axis is mounted against the others' polarity (Z).</summary>
        public bool InvertDirection { get; init; }
    }

    /// <summary>
    /// The single source of truth for the bus-position → axis mapping. Confirmed EtherCAT scan
    /// order on this machine is X, Y, Z, Θ (bus positions 0..3).
    /// </summary>
    public static class TableAxes
    {
        public static IReadOnlyList<AxisConfig> Default { get; } =
        [
            new AxisConfig { Id = AxisId.X,     Name = "X",     BusPosition = 0, JogVelocityDefault = 4000, JogVelocityMax = 6000 },
            new AxisConfig { Id = AxisId.Y,     Name = "Y",     BusPosition = 1, JogVelocityDefault = 4000, JogVelocityMax = 12000 },
            new AxisConfig { Id = AxisId.Z,     Name = "Z",     BusPosition = 2, JogVelocityDefault = 300,  JogVelocityMax = 800, InvertDirection = true },
            new AxisConfig { Id = AxisId.Theta, Name = "Theta", BusPosition = 3, JogVelocityDefault = 400,  JogVelocityMax = 3200},
        ];

        /// <summary>Config for an axis, or null if it is not in the layout. Avoids indexing
        /// <see cref="Default"/> by the enum value, which only works while bus order matches
        /// declaration order.</summary>
        public static AxisConfig? For(AxisId id)
        {
            foreach (AxisConfig c in Default) if (c.Id == id) return c;
            return null;
        }

        /// <summary>Axis label for a bus position (for logs / readouts). "?" if out of range.</summary>
        public static string NameForBusPosition(int busPosition)
            => busPosition >= 0 && busPosition < Default.Count ? Default[busPosition].Name : "?";
    }
}
