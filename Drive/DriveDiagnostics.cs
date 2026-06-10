using System;
using System.Collections.Generic;
using Nlc;

namespace MotorControlApp
{
    /// <summary>One object's value read back from a drive (or the error if it failed).</summary>
    public readonly record struct ParameterReadout(
        string Label, string Index, long? Value, string Unit, string? Error, bool Hex = false)
    {
        public override string ToString()
        {
            string val = Error != null ? $"<error: {Error}>"
                       : !Value.HasValue ? "<no value>"
                       : Hex ? $"0x{Value.Value:X}"
                       : $"{Value} {Unit}".TrimEnd();
            return $"{Label,-24} {Index} = {val}";
        }
    }

    /// <summary>
    /// Read-only readout of a drive's key configuration. WRITES NOTHING — only calls
    /// readNumber, so it cannot disturb the values it reports. This is the neutral,
    /// project-less checker for verifying NV-persisted parameters after a power cycle:
    /// unlike opening a .nprj in Studio (which may write the project on connect),
    /// reading here can't be the thing that sets the values, so the check isn't circular.
    ///
    /// Two groups are read:
    ///  • <see cref="Limits"/> — protection/motor limits. Current/torque objects have
    ///    FIXED semantics (mA, or per-mille ratios) independent of the factor group.
    ///  • <see cref="UnitsScaling"/> — the factor-group / SI-unit objects that DEFINE how
    ///    raw counts map to user position/velocity units. These are read (not assumed)
    ///    because the units are configurable per axis — so jog/profile velocities and
    ///    MoveAbsolute targets are only meaningful relative to these.
    /// </summary>
    public static class DriveDiagnostics
    {
        private readonly record struct ParamSpec(
            ushort Index, byte Sub, string Label, string Unit, bool Hex = false);

        // Protection / motor limits. Units here are fixed (not factor-group dependent).
        private static readonly ParamSpec[] Limits =
        {
            new(0x2031, 0x00, "Max motor current",   "mA"),
            new(0x6073, 0x00, "Max current",         "0.1% rated"),
            new(0x6075, 0x00, "Motor rated current", "mA"),
            new(0x203B, 0x01, "i2t nominal current", "mA"),
            new(0x203B, 0x02, "i2t peak duration",   "ms"),
            new(0x6072, 0x00, "Max torque",          "0.1% rated"),
            new(0x6080, 0x00, "Max motor speed",     "rpm"),
            // Profile ramps used by jog (Profile Velocity) and point-to-point moves. A large
            // 0x6084 is why an axis coasts down slowly after the jog button is released.
            new(0x6083, 0x00, "Profile acceleration", "vel units/s"),
            new(0x6084, 0x00, "Profile deceleration", "vel units/s"),
        };

        // Factor group + SI-unit codes: these DEFINE the position/velocity units.
        // SI-unit objects are encoded bitfields → shown in hex for comparison.
        private static readonly ParamSpec[] UnitsScaling =
        {
            new(0x60A8, 0x00, "SI unit position",      "code", Hex: true),
            new(0x60A9, 0x00, "SI unit velocity",      "code", Hex: true),
            new(0x6091, 0x01, "Gear ratio: motor rev", ""),
            new(0x6091, 0x02, "Gear ratio: shaft rev", ""),
            new(0x6092, 0x01, "Feed constant: feed",   ""),
            new(0x6092, 0x02, "Feed constant: shaft",  ""),
            new(0x6096, 0x01, "Velocity factor: num",  ""),
            new(0x6096, 0x02, "Velocity factor: den",  ""),
        };

        /// <summary>Protection / motor-limit objects (fixed units).</summary>
        public static IReadOnlyList<ParameterReadout> ReadLimits(
            NanoLibAccessor accessor, DeviceHandle handle) => Read(Limits, accessor, handle);

        /// <summary>Factor-group / SI-unit objects that define position &amp; velocity units.</summary>
        public static IReadOnlyList<ParameterReadout> ReadUnitsScaling(
            NanoLibAccessor accessor, DeviceHandle handle) => Read(UnitsScaling, accessor, handle);

        private static IReadOnlyList<ParameterReadout> Read(
            ParamSpec[] specs, NanoLibAccessor accessor, DeviceHandle handle)
        {
            var results = new List<ParameterReadout>(specs.Length);
            foreach (ParamSpec p in specs)
            {
                string idx = $"0x{p.Index:X4}:{p.Sub:X2}";
                using ResultInt r = accessor.readNumber(handle, new OdIndex(p.Index, p.Sub));
                results.Add(r.hasError()
                    ? new ParameterReadout(p.Label, idx, null, p.Unit, r.getError(), p.Hex)
                    : new ParameterReadout(p.Label, idx, r.getResult(), p.Unit, null, p.Hex));
            }
            return results;
        }
    }
}
