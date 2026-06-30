using System;
using System.Threading.Tasks;

namespace NanotecController
{
    /// <summary>
    /// The surface the tool windows (FrmCalibration, FrmParams, FrmPosition, FrmVision) call
    /// back into. <see cref="FrmMain"/> is the sole implementer: it owns the single NanoLib
    /// channel, serializes all drive access, and coordinates the status/joystick timers, so the
    /// windows stay pure UI and never touch a drive directly.
    ///
    /// This interface exists to (a) document that owner surface in ONE place instead of scattered
    /// across the FrmMain partials, and (b) decouple the windows from the concrete form so they can
    /// be exercised against a fake. Members are grouped by the partial that implements them.
    /// </summary>
    public interface IMotionHost
    {
        // --- Calibration / positioning (FrmMain.Calibration.cs) ---
        CalibrationStore Calibration { get; }
        bool CanCaptureCalibration { get; }
        bool CanMoveCalibration { get; }
        bool TryCurrentUser(AxisId id, out long user);
        (long min, long max)? UserLimits(AxisId id);
        long? HomeTargetFor(AxisId id);
        Task MoveToAsync(string xText, string yText, string zText);
        Task GoHomeAsync(AxisId id);
        Task FindLimitsAsync(AxisId id);
        void SetCalibrationMin(AxisId id);
        void SetCalibrationMax(AxisId id);
        void SetCalibrationHome(AxisId id);
        void ClearCalibrationMin(AxisId id);
        void ClearCalibrationMax(AxisId id);

        // --- Drive parameters (FrmMain.Params.cs) ---
        bool CanAccessParams { get; }
        bool CanWriteParams { get; }
        Task ReadAllParamsAsync(IProgress<string> sink);
        Task WriteObjectAsync(AxisId id, ushort index, byte sub, long value, uint bits, IProgress<string> sink);
        Task SaveParamsToNvAsync(AxisId id, IProgress<string> sink);

        // --- Rotate about crosshair (FrmMain.Rotation.cs) ---
        int? RotationSign { get; }
        void SetRotationSign(int sign);
        Task RotateToAngleAsync(double targetDegrees);
        Task RotateAboutCrosshairAsync(double deltaDegrees);
        Task HoldRotateAsync(int direction);
        void StopHoldRotate();

        // --- Drift-corrected vision jog (FrmMain.Vision.cs) ---
        void VisionJogUser(int vxUser, int vyUser);
        void VisionStop();
    }
}
