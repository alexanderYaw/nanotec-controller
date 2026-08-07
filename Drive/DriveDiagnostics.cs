using System;
using System.Collections.Generic;
using Nlc;

namespace NanotecController
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
    /// Read-only readout of a drive's key configuration. WRITES NOTHING — only calls readNumber, so
    /// it cannot disturb the values it reports, which is what makes it a non-circular check of
    /// NV-persisted parameters after a power cycle (unlike opening a .nprj in Studio).
    /// </summary>
    public static class DriveDiagnostics
    {
        #region Object groups

        private readonly record struct ParamSpec(
            ushort Index, byte Sub, string Label, string Unit, bool Hex = false, byte SignedBits = 0);

        /// <summary>Protection / motor limits. Units are fixed, not factor-group dependent.</summary>
        private static readonly ParamSpec[] Limits =
        {
            new(0x2031, 0x00, "Max motor current",   "mA"),
            new(0x6073, 0x00, "Max current",         "0.1% rated"),
            new(0x6075, 0x00, "Motor rated current", "mA"),
            new(0x203B, 0x01, "i2t nominal current", "mA"),
            new(0x203B, 0x02, "i2t peak duration",   "ms"),
            new(0x6072, 0x00, "Max torque",          "0.1% rated"),
            new(0x6080, 0x00, "Max motor speed",     "rpm"),
            new(0x6083, 0x00, "Profile acceleration", "vel units/s"),
            new(0x6084, 0x00, "Profile deceleration", "vel units/s"),
        };

        /// <summary>Factor group + SI-unit codes — these DEFINE the position/velocity units, so they
        /// are read rather than assumed. The SI-unit objects are bitfields, shown in hex.</summary>
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

        /// <summary>Live motion / Profile-Position state, read right after a move to diagnose one that
        /// "completes" in the object dictionary without the stage physically moving.</summary>
        private static readonly ParamSpec[] MotionState =
        {
            new(0x6061, 0x00, "Mode display",         "(1=PP 3=PV 6=home)", SignedBits: 8),
            new(0x6060, 0x00, "Mode commanded",       "",                   SignedBits: 8),
            new(0x6041, 0x00, "Statusword",           "bits",   Hex: true),
            new(0x607A, 0x00, "Target position",      "pos units", SignedBits: 32),
            new(0x6064, 0x00, "Position actual",      "pos units", SignedBits: 32),
            new(0x6081, 0x00, "Profile velocity",     "vel units"),
            new(0x6083, 0x00, "Profile acceleration", "vel units/s"),
            new(0x6084, 0x00, "Profile deceleration", "vel units/s"),
        };

        #endregion

        #region Reads

        /// <summary>Protection / motor-limit objects (fixed units).</summary>
        public static IReadOnlyList<ParameterReadout> ReadLimits(
            NanoLibAccessor accessor, DeviceHandle handle) => Read(Limits, accessor, handle);

        /// <summary>Factor-group / SI-unit objects that define position &amp; velocity units.</summary>
        public static IReadOnlyList<ParameterReadout> ReadUnitsScaling(
            NanoLibAccessor accessor, DeviceHandle handle) => Read(UnitsScaling, accessor, handle);

        /// <summary>Live motion / Profile-Position state objects (for diagnosing absolute moves).</summary>
        public static IReadOnlyList<ParameterReadout> ReadMotionState(
            NanoLibAccessor accessor, DeviceHandle handle) => Read(MotionState, accessor, handle);

        private static IReadOnlyList<ParameterReadout> Read(
            ParamSpec[] specs, NanoLibAccessor accessor, DeviceHandle handle)
        {
            var results = new List<ParameterReadout>(specs.Length);
            foreach (ParamSpec p in specs)
            {
                string idx = $"0x{p.Index:X4}:{p.Sub:X2}";
                using ResultInt r = accessor.readNumber(handle, new OdIndex(p.Index, p.Sub));
                if (r.hasError())
                {
                    results.Add(new ParameterReadout(p.Label, idx, null, p.Unit, r.getError(), p.Hex));
                    continue;
                }
                long v = r.getResult();
                // NanoLib zero-extends every read; sign-extend so negatives don't read as billions.
                if (p.SignedBits != 0) { int sh = 64 - p.SignedBits; v = (v << sh) >> sh; }
                results.Add(new ParameterReadout(p.Label, idx, v, p.Unit, null, p.Hex));
            }
            return results;
        }

        #endregion
    }
}
