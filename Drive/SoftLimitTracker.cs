using System.Collections.Generic;

namespace NanotecController
{
    /// <summary>
    /// Per-axis soft-limit jog guard. Tracks everything in COMMAND space, never assuming a
    /// command→position polarity, so a held or re-pressed outward jog can't re-lurch past the limit
    /// each poll — critical for Z (no hardware switch) and X (switches the drive ignores).
    /// Pure state + decisions: the owner performs the Stop and the logging, so this has no drive or
    /// UI dependency and is unit-testable alone.
    /// </summary>
    public sealed class SoftLimitTracker
    {
        #region State

        private readonly Dictionary<AxisId, long> _prevPos = new();
        private readonly HashSet<AxisId> _atLimit = new();
        private readonly Dictionary<AxisId, int> _cmdDir = new();
        private readonly Dictionary<AxisId, int> _blockedDir = new();

        /// <summary>Outcome of <see cref="Evaluate"/>: whether to stop the axis now, and a one-shot
        /// log line (null = nothing to log this tick).</summary>
        public readonly record struct Decision(bool Stop, string? Log);

        #endregion

        #region Command tracking

        /// <summary>True if jogging <paramref name="dir"/> would push the axis further past the soft
        /// limit it is already parked against. Jogging back into range is always allowed.</summary>
        public bool IsBlocked(AxisId id, int dir)
            => dir != 0 && _blockedDir.TryGetValue(id, out int b) && b == dir;

        /// <summary>Records the direction currently commanded (0 = stopped). Commanding the OPPOSITE of
        /// a refused direction clears the refusal — the only thing that can, for an axis with no stored
        /// limits that <see cref="BlockCommandedDirection"/> latched.</summary>
        public void RecordCommand(AxisId id, int dir)
        {
            _cmdDir[id] = dir;
            if (dir != 0 && _blockedDir.TryGetValue(id, out int b) && b == -dir) _blockedDir[id] = 0;
        }

        /// <summary>Latches the commanded direction as refused, for when the DRIVE stopped the axis
        /// (a hardware limit-switch quick stop) rather than this guard. The switch can sit anywhere
        /// relative to the stored limits, so the direction comes from what was commanded, not from
        /// position. No-op when nothing was commanded; returns true if a direction was latched.</summary>
        public bool BlockCommandedDirection(AxisId id)
        {
            if (!_cmdDir.TryGetValue(id, out int d) || d == 0) return false;
            _blockedDir[id] = d;
            _cmdDir[id] = 0;
            return true;
        }

        #endregion

        #region Evaluation

        /// <summary>Updates tracking from a fresh position sample and decides whether the axis is
        /// jogging past a stored limit. Stops only when at/beyond a limit AND still moving further out
        /// (direction inferred from the position delta, so it stays polarity-independent). With drives
        /// disabled or no prior sample this just rebaselines.</summary>
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
                if (_cmdDir.TryGetValue(id, out int d) && d != 0) _blockedDir[id] = d;
                _cmdDir[id] = 0;
                string? log = _atLimit.Add(id)   // log once per approach, not every poll
                    ? $"{id} soft {(outMax ? "Max" : "Min")} limit reached - jog stopped at {pos:N0}."
                    : null;
                return new Decision(true, log);
            }
            if (!outMax && !outMin)
            {
                _atLimit.Remove(id);
                _blockedDir[id] = 0;
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

        #endregion
    }
}
