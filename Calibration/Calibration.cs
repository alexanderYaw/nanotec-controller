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

        /// <summary>Chuck radius in motor steps, or null until a centre-find has measured one. Written
        /// from the circle fit's radius; read back as the auto centre-find's nominal radius, which arms
        /// its travel guard and its approach jump. Persisted so the guard survives a restart.</summary>
        public long? ChuckRadius { get; set; }

        /// <summary>Wafer centre in motor steps (USER frame) at the Θ angle the scan ended on — a
        /// SNAPSHOT, not an invariant. The wafer sits eccentric on the chuck, so its centre orbits the
        /// rotation axis as Θ turns; use <see cref="WaferCentreAt"/> for the centre at any other angle.
        /// Kept so a plain "go to the wafer centre" has something to read without a Θ query.</summary>
        public long? WaferCenterX { get; set; }
        public long? WaferCenterY { get; set; }

        /// <summary>Wafer centre relative to the chuck centre, in motor steps, expressed in the CHUCK's
        /// rotating frame (de-rotated to θ = 0). This is the invariant the Θ scan measures: unlike
        /// <see cref="WaferCenterX"/> it does not go stale when Θ moves. Null until a scan has run.</summary>
        public long? WaferOffsetX { get; set; }
        public long? WaferOffsetY { get; set; }

        /// <summary>Wafer radius in motor steps from the scan's circle fit. Compared against the
        /// operator's nominal diameter as the scan's main sanity check.</summary>
        public long? WaferRadius { get; set; }

        /// <summary>De-rotation handedness the Θ scan settled on (±1). Needed to turn
        /// <see cref="WaferOffsetX"/> back into a motor position at a given Θ.</summary>
        public int? WaferFitSign { get; set; }

        /// <summary>Θ-scan fit quality, for the same reason PixelStepAffine carries its own: a stored
        /// centre with no record of how well it fitted cannot be judged later.</summary>
        public double? WaferFitRms { get; set; }
        public int? WaferFitN { get; set; }
        public string? WaferFitTimestamp { get; set; }

        /// <summary>Bearing of the notch from the wafer centre, in the CHUCK's rotating frame — so
        /// like <see cref="WaferOffsetX"/> it does not go stale when Θ moves, and turning the notch to
        /// a datum is just a Θ move of (datum − this). Null until a notch search has run. Belongs to
        /// the WAFER, not the machine: it is void the moment the wafer is lifted or re-placed.</summary>
        public double? NotchAngleDeg { get; set; }
        /// <summary>Depth the notch measured, kept for the same reason the wafer fit keeps its RMS —
        /// a stored angle with no record of how good the measurement was cannot be judged later.
        /// A SEMI 200 mm notch is 1.00 mm; a value far off that means the search latched onto
        /// something else and the angle should not be trusted.</summary>
        public double? NotchDepthMm { get; set; }
        public string? NotchTimestamp { get; set; }

        /// <summary>Image handedness of a positive Θ move: +1 or -1, or null until the
        /// crosshair-rotation sign test fixes it. Not derivable from the translation-only
        /// <see cref="PixelStep"/> affine — it depends on Θ's mounting and camera orientation,
        /// so it is found empirically and persisted here.</summary>
        public int? RotationSign { get; set; }

        /// <summary>
        /// The motor position that puts the WAFER centre under the crosshair when the chuck stands at
        /// <paramref name="chuckAngleDeg"/> (as <see cref="CrosshairRotation.ChuckTicksToDegrees"/>
        /// reports it). The stored offset lives in the chuck's rotating frame, so it is rotated back
        /// out to the lab frame here. Null unless a Θ scan has run, a chuck centre exists, and X and Y
        /// both have StepsPerMm — the rotation is only a rotation in mm.
        /// </summary>
        public (long X, long Y)? WaferCentreAt(double chuckAngleDeg)
        {
            if (ChuckCenterX is not long cx || ChuckCenterY is not long cy) return null;
            if (WaferOffsetX is not long ox || WaferOffsetY is not long oy) return null;
            if (WaferFitSign is not int sign) return null;
            if (!Axes.TryGetValue(AxisId.X, out AxisCalibration? ax) || ax.StepsPerMm is not double kX || kX <= 0) return null;
            if (!Axes.TryGetValue(AxisId.Y, out AxisCalibration? ay) || ay.StepsPerMm is not double kY || kY <= 0) return null;

            double wx = ox / kX, wy = oy / kY;
            double rad = sign * chuckAngleDeg * Math.PI / 180.0;
            double c = Math.Cos(rad), s = Math.Sin(rad);
            return ((long)Math.Round(cx + (c * wx - s * wy) * kX),
                    (long)Math.Round(cy + (s * wx + c * wy) * kY));
        }

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
        /// soft limits, which on Z (no switches) and X (switches present but ignored by the
        /// drive, 0x3701 = -1) are the only travel protection.
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
