using System.Collections.Generic;

namespace NanotecController
{
    /// <summary>
    /// Per-axis soft-limit jog guard, extracted from FrmMain. Tracks, in COMMAND space (never
    /// assuming a command→position polarity): the last polled position, which axes are parked at
    /// a stored digital limit, the direction currently commanded, and the direction refused
    /// because it tripped a limit. This stops a held/re-pressed outward jog from re-lurching past
    /// the limit each poll — critical for X+ and Z, which have no hardware switch (see the
    /// limit-switch findings).
    ///
    /// Pure state + decisions: the owner (FrmMain) performs the actual axis Stop and the logging,
    /// so this class has no drive or UI dependency and is unit-testable on its own.
    /// </summary>
    public sealed class SoftLimitTracker
    {
        private readonly Dictionary<AxisId, long> _prevPos = new();
        private readonly HashSet<AxisId> _atLimit = new();
        private readonly Dictionary<AxisId, int> _cmdDir = new();
        private readonly Dictionary<AxisId, int> _blockedDir = new();

        /// <summary>Outcome of <see cref="Evaluate"/>: whether to stop the axis now, and a
        /// one-shot log line (null = nothing to log this tick).</summary>
        public readonly record struct Decision(bool Stop, string? Log);

        /// <summary>
        /// True if jogging <paramref name="dir"/> would push the axis further past the soft limit
        /// it is already parked against. The blocked direction was recorded in command space at the
        /// moment the limit tripped, so this never assumes a command→position polarity; jogging the
        /// opposite way (back into range) is always allowed.
        /// </summary>
        public bool IsBlocked(AxisId id, int dir)
            => dir != 0 && _blockedDir.TryGetValue(id, out int b) && b == dir;

        /// <summary>Records the direction currently commanded for an axis (0 = stopped).</summary>
        public void RecordCommand(AxisId id, int dir) => _cmdDir[id] = dir;

        /// <summary>
        /// Updates tracking from a fresh position sample and decides whether the axis is jogging
        /// past a stored digital limit. A stop fires only when the axis is at/beyond a limit AND
        /// still moving further out (direction inferred from the position delta, so it's polarity-
        /// independent); reversing back inside the range clears the block. With drives disabled or
        /// no prior sample, this just rebaselines and returns no action.
        /// </summary>
        public Decision Evaluate(AxisId id, long pos, CalibrationStore calib, bool drivesEnabled)
        {
            bool hasPrev = _prevPos.TryGetValue(id, out long prev);
            _prevPos[id] = pos;
            if (!drivesEnabled || !hasPrev) { _atLimit.Remove(id); _blockedDir[id] = 0; return default; }

            AxisCalibration cal = calib.For(id);
            long delta = pos - prev;
            bool outMax = cal.Max.HasValue && pos >= cal.Max.Value;
            bool outMin = cal.Min.HasValue && pos <= cal.Min.Value;

            if ((outMax && delta > 0) || (outMin && delta < 0))
            {
                // Refuse further jogs in the SAME command direction that pushed it out, so a
                // held/re-pressed control can't re-lurch past the limit each poll. Reversing
                // (back into range) clears the block below.
                if (_cmdDir.TryGetValue(id, out int d) && d != 0) _blockedDir[id] = d;
                _cmdDir[id] = 0;
                string? log = _atLimit.Add(id)   // log once per approach, not every poll
                    ? $"{id} soft {(outMax ? "Max" : "Min")} limit reached - jog stopped at {pos:N0}."
                    : null;
                return new Decision(true, log);
            }
            if (!outMax && !outMin)
            {
                _atLimit.Remove(id);     // safely back inside the range
                _blockedDir[id] = 0;     // re-allow both directions
            }
            return default;
        }

        /// <summary>Clears the limit block + parked flag for one axis (after its stored limit is cleared).</summary>
        public void ClearAxis(AxisId id)
        {
            _blockedDir[id] = 0;
            _atLimit.Remove(id);
        }

        /// <summary>Clears all tracking so a stale position delta can't trigger a false stop.</summary>
        public void Reset()
        {
            _prevPos.Clear();
            _atLimit.Clear();
            _cmdDir.Clear();
            _blockedDir.Clear();
        }
    }
}
