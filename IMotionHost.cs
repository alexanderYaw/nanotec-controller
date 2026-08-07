using System;
using System.Threading.Tasks;

namespace NanotecController
{
    /// <summary>
    /// The surface the tool windows call back into. <see cref="FrmMain"/> is the sole implementer: it
    /// owns the single NanoLib channel and serializes all drive access, so the windows stay pure UI.
    /// Existing as an interface documents that owner surface in ONE place rather than scattered
    /// across the FrmMain partials, and lets the windows be exercised against a fake. Members are
    /// grouped by the partial that implements them.
    /// </summary>
    public interface IMotionHost
    {
        #region Calibration and positioning (FrmMain.Calibration.cs)

        CalibrationStore Calibration { get; }
        bool CanCaptureCalibration { get; }
        bool CanMoveCalibration { get; }
        bool TryCurrentUser(AxisId id, out long user);
        bool TryReadUserXyNow(out long x, out long y);
        /// <summary>Fresh (uncached) Θ read, in raw drive ticks. Same rationale as
        /// <see cref="TryReadUserXyNow"/>: the cache is stale for a poll period after every move.</summary>
        bool TryReadThetaNow(out long ticks);
        (long min, long max)? UserLimits(AxisId id);
        long? HomeTargetFor(AxisId id);
        Task MoveToAsync(string xText, string yText, string zText);
        Task GoHomeAsync(AxisId id);
        /// <summary>One unified auto-calibration of the travel limits of X and Y — both axes at once.</summary>
        Task FindXyLimitsAsync();
        /// <summary>Aborts any preplanned move in progress. Cooperative — it only sets a flag; the
        /// running op notices at its next poll and halts the drives on its own thread.</summary>
        void RequestStop();
        /// <summary>Locks out the host's MANUAL motion controls for the scope's lifetime. For callers
        /// running a LONG SEQUENCE of moves, where the host's per-op busy flag drops between steps and
        /// would otherwise re-enable manual input mid-run.</summary>
        IDisposable BeginExternalOp(string what);
        void SetCalibrationMin(AxisId id);
        void SetCalibrationMax(AxisId id);
        void SetCalibrationHome(AxisId id);
        void ClearCalibrationMin(AxisId id);
        void ClearCalibrationMax(AxisId id);

        #endregion

        #region Drive parameters (FrmMain.Params.cs)

        bool CanAccessParams { get; }
        bool CanWriteParams { get; }
        Task ReadAllParamsAsync(IProgress<string> sink);
        Task WriteObjectAsync(AxisId id, ushort index, byte sub, long value, uint bits, IProgress<string> sink);
        Task SaveParamsToNvAsync(AxisId id, IProgress<string> sink);

        #endregion

        #region Rotate about crosshair (FrmMain.Rotation.cs)

        int? RotationSign { get; }
        int RotateThetaSpeed { get; set; }
        void SetRotationSign(int sign);
        Task RotateToAngleAsync(double targetDegrees);
        Task RotateAboutCrosshairAsync(double deltaDegrees);
        /// <summary>Turns Θ alone, with no X/Y compensation — the wafer Θ scan needs the stage to
        /// stay put so the rim sweeps past the camera. False if the move did not complete.</summary>
        Task<bool> RotateThetaOnlyAsync(double deltaDegrees, int speed);
        Task HoldRotateAsync(int direction, Func<bool>? stopWhen = null);
        void StopHoldRotate();

        #endregion

        #region Continuous rim sweep (FrmMain.RimSweep.cs)

        /// <summary>Turns Θ continuously while Y follows <paramref name="stationYAt"/> (a USER-frame Y
        /// for a CHUCK angle in degrees), until <paramref name="stopWhen"/> fires or
        /// <paramref name="maxDegrees"/> are swept. <paramref name="stopWhen"/> is polled on the drive
        /// thread — keep it to reading a flag.</summary>
        Task<FrmMain.RimSweepResult> SweepRimAsync(
            Func<double, double?> stationYAt, Func<bool> stopWhen, int thetaDir, double maxDegrees,
            int thetaSpeed);
        /// <summary>Θ as of the sweep loop's last tick. Read THIS during a sweep, never
        /// <see cref="TryReadThetaNow"/>: NanoLib access is serialized on one channel and the sweep
        /// owns it for the whole revolution.</summary>
        long SweepThetaTicks { get; }
        /// <summary>Θ's velocity cap. This is what floors the search time — a revolution is
        /// <see cref="CrosshairRotation.ChuckTicksPerRev"/> ticks, so at 5000 steps/s no sweep can
        /// take less than ~72 s.</summary>
        int ThetaSpeedMax { get; }

        #endregion

        #region Drift-corrected vision jog (FrmMain.Vision.cs)

        void VisionJogUser(int vxUser, int vyUser);
        void VisionStop();

        #endregion
    }
}
