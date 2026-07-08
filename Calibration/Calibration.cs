using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NanotecController
{
    /// <summary>
    /// Stored travel limits + home for one axis, in raw drive position units (object
    /// 0x6064). Min/Max are the two "digital limits"; <see cref="Center"/> is their
    /// midpoint, used as Home for the linear stages that have two references (X, Y).
    /// </summary>
    public sealed class AxisCalibration
    {
        public long? Min { get; set; }
        public long? Max { get; set; }
        /// <summary>Explicit home, used where Center doesn't apply (Z has no two references).</summary>
        public long? Home { get; set; }

        /// <summary>Motor steps per millimetre of stage travel (user-entered, from the stage's
        /// mechanical spec). Converts mm-relative moves and scales the crosshair mm ticks;
        /// null until entered. Θ never uses this (degrees go via ChuckTicksPerRev).</summary>
        public double? StepsPerMm { get; set; }

        /// <summary>Midpoint of the two limits, or null until both are set.</summary>
        [JsonIgnore]
        public long? Center => Min.HasValue && Max.HasValue ? (Min.Value + Max.Value) / 2 : null;
    }

    /// <summary>
    /// Pixel→motor-step affine from the camera-scale calibration: the steps each axis moves
    /// per pixel of fiducial displacement. ΔX = Xr·Δrow + Xc·Δcol; ΔY = Yr·Δrow + Yc·Δcol.
    /// Captures both scale and the camera↔stage rotation; offset is not stored (only
    /// displacements are used). <see cref="ResidualSteps"/> is the calibration's RMS fit error.
    /// </summary>
    public sealed class PixelStepAffine
    {
        public double Xr { get; set; }
        public double Xc { get; set; }
        public double Yr { get; set; }
        public double Yc { get; set; }
        public int SampleCount { get; set; }
        public double ResidualSteps { get; set; }
        public string? Timestamp { get; set; }
    }

    /// <summary>
    /// Per-axis calibration persisted to a JSON file, so a defined home survives restarts.
    /// Theta is excluded by convention (the rotary chuck has no home). The home model is
    /// the caller's policy: X/Y use <see cref="AxisCalibration.Center"/>; Z uses its
    /// explicit <see cref="AxisCalibration.Home"/>.
    /// </summary>
    public sealed class CalibrationStore
    {
        public Dictionary<AxisId, AxisCalibration> Axes { get; set; } = new();

        /// <summary>Camera-scale calibration (pixel→step), or null until calibrated.</summary>
        public PixelStepAffine? PixelStep { get; set; }

        /// <summary>Chuck centre in motor steps (USER frame), or null until found. The motor
        /// position that puts the chuck centre under the crosshair / view centre.</summary>
        public long? ChuckCenterX { get; set; }
        public long? ChuckCenterY { get; set; }

        /// <summary>Wafer centre in motor steps (USER frame), or null until found — same meaning as
        /// the chuck centre but circle-fit from WAFER rim points. Kept separate from the chuck centre.</summary>
        public long? WaferCenterX { get; set; }
        public long? WaferCenterY { get; set; }

        /// <summary>Image handedness of a positive Θ move: +1 or -1, or null until the
        /// crosshair-rotation sign test fixes it. Not derivable from the translation-only
        /// <see cref="PixelStep"/> affine — it depends on Θ's mounting and camera orientation,
        /// so it is found empirically and persisted here.</summary>
        public int? RotationSign { get; set; }

        /// <summary>Gets (creating if absent) the calibration record for an axis.</summary>
        public AxisCalibration For(AxisId id)
        {
            if (!Axes.TryGetValue(id, out AxisCalibration? c)) { c = new AxisCalibration(); Axes[id] = c; }
            return c;
        }

        private static readonly JsonSerializerOptions Opts = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        };

        public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "calibration.json");

        /// <summary>
        /// Loads the saved calibration, or a fresh (empty) store if none exists / it is
        /// corrupt. <paramref name="warning"/> is set (and the bad file preserved as
        /// <c>calibration.corrupt.json</c>) when an existing file could not be read — the
        /// caller MUST surface it, because starting with an empty store silently removes the
        /// soft limits, which on X+/Z are the only travel protection.
        /// </summary>
        public static CalibrationStore Load(out string? warning)
        {
            warning = null;
            try
            {
                if (File.Exists(DefaultPath))
                {
                    CalibrationStore? s = JsonSerializer.Deserialize<CalibrationStore>(
                        File.ReadAllText(DefaultPath), Opts);
                    if (s != null) return s;
                    warning = "calibration.json was empty/invalid - starting with NO soft limits.";
                }
            }
            catch (Exception ex)
            {
                warning = $"calibration.json could not be read ({ex.Message}) - starting with NO soft limits.";
            }
            if (warning != null) TryPreserveCorrupt();
            return new CalibrationStore();
        }

        // Moves an unreadable calibration file aside so it isn't silently overwritten by the
        // next Save() and can be inspected/recovered. Best effort — never throws.
        private static void TryPreserveCorrupt()
        {
            try
            {
                if (File.Exists(DefaultPath))
                {
                    string bak = Path.Combine(AppContext.BaseDirectory, "calibration.corrupt.json");
                    File.Copy(DefaultPath, bak, overwrite: true);
                }
            }
            catch { /* best effort */ }
        }

        /// <summary>
        /// Writes the store to disk atomically (temp file + replace) so a crash mid-write
        /// can't truncate the live calibration and silently drop the limits. Throws on IO
        /// failure so the caller can report it.
        /// </summary>
        public void Save()
        {
            string json = JsonSerializer.Serialize(this, Opts);
            string tmp = DefaultPath + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(DefaultPath)) File.Replace(tmp, DefaultPath, null);
            else File.Move(tmp, DefaultPath);
        }
    }
}
