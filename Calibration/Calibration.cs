using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NanotecController
{
    /// <summary>Stored travel limits + home for one axis, in raw drive position units (0x6064).
    /// Min/Max are the two digital limits; <see cref="Center"/> is their midpoint, used as Home for
    /// the linear stages that have two references (X, Y).</summary>
    public sealed class AxisCalibration
    {
        public long? Min { get; set; }
        public long? Max { get; set; }
        /// <summary>Explicit home, used where Center doesn't apply (Z has no two references).</summary>
        public long? Home { get; set; }

        /// <summary>Motor steps per millimetre of stage travel. Either entered by hand in the
        /// calibration window, or — for X and Y — derived by the camera-scale calibration from the
        /// fiducial's known diameter, which overwrites whatever was there. See
        /// <see cref="PixelStepAffine.UmPerPixel"/> for the run that last derived it. Null until set;
        /// Θ never uses it, since degrees go via ChuckTicksPerRev.</summary>
        public double? StepsPerMm { get; set; }

        /// <summary>Midpoint of the two limits, or null until both are set.</summary>
        [JsonIgnore]
        public long? Center => Min.HasValue && Max.HasValue ? (Min.Value + Max.Value) / 2 : null;
    }

    /// <summary>Pixel→motor-step affine from the camera-scale calibration:
    /// ΔX = Xr·Δrow + Xc·Δcol, ΔY = Yr·Δrow + Yc·Δcol. Carries both scale and the camera↔stage
    /// rotation; no offset is stored, since only displacements are used.</summary>
    public sealed class PixelStepAffine
    {
        public double Xr { get; set; }
        public double Xc { get; set; }
        public double Yr { get; set; }
        public double Yc { get; set; }
        public int SampleCount { get; set; }
        /// <summary>RMS fit error of the calibration, in steps.</summary>
        public double ResidualSteps { get; set; }
        public string? Timestamp { get; set; }

        /// <summary>Fiducial diameter (mm) this run was told to assume — the single physical length
        /// the whole scale rests on.</summary>
        public double? FiducialDiameterMm { get; set; }

        /// <summary>Image scale derived from that diameter. Null on an affine solved before scale
        /// derivation existed, or when no sample carried a usable radius, in which case the axes'
        /// StepsPerMm are still whatever was last entered by hand.</summary>
        public double? UmPerPixel { get; set; }

        /// <summary>Sample-to-sample spread of the measured fiducial radius, as a percent of the mean.
        /// It passes straight through to StepsPerMm, so this is the scale's error bar.</summary>
        public double? ScaleSpreadPercent { get; set; }

        /// <summary>Departure from perpendicular of the affine's two pixel axes once scaled to mm.
        /// Nothing in the solve forces this to zero, so a large value means the affine is not a
        /// rotation plus scale and the derived StepsPerMm should not be trusted.</summary>
        public double? ScaleSkewDeg { get; set; }
    }

    /// <summary>
    /// Per-axis calibration persisted to JSON, so a defined home survives restarts. Θ is excluded by
    /// convention (the rotary chuck has no home). The home model is the caller's policy: X/Y use
    /// <see cref="AxisCalibration.Center"/>, Z its explicit <see cref="AxisCalibration.Home"/>.
    /// </summary>
    public sealed class CalibrationStore
    {
        #region Axis limits and camera

        public Dictionary<AxisId, AxisCalibration> Axes { get; set; } = new();

        /// <summary>Camera-scale calibration (pixel→step), or null until calibrated.</summary>
        public PixelStepAffine? PixelStep { get; set; }

        /// <summary>Image handedness of a positive Θ move (±1), or null until the crosshair-rotation
        /// sign test fixes it. Not derivable from the translation-only <see cref="PixelStep"/> affine,
        /// so it is found empirically and persisted.</summary>
        public int? RotationSign { get; set; }

        #endregion

        #region Chuck

        /// <summary>Motor position (USER frame) putting the chuck centre under the crosshair, or null
        /// until found.</summary>
        public long? ChuckCenterX { get; set; }
        public long? ChuckCenterY { get; set; }

        /// <summary>Chuck radius in motor steps from the circle fit, or null until measured. Read back
        /// as the auto centre-find's nominal radius, arming its travel guard and approach jump.</summary>
        public long? ChuckRadius { get; set; }

        #endregion

        #region Wafer

        /// <summary>Wafer centre (USER frame) at the Θ angle the scan ended on — a SNAPSHOT, not an
        /// invariant, since the eccentric wafer's centre orbits as Θ turns. Use
        /// <see cref="WaferCentreAt"/> for any other angle.</summary>
        public long? WaferCenterX { get; set; }
        public long? WaferCenterY { get; set; }

        /// <summary>Wafer centre relative to the chuck centre, in the CHUCK's rotating frame
        /// (de-rotated to θ = 0). The invariant the Θ scan measures — unlike <see cref="WaferCenterX"/>
        /// it does not go stale when Θ moves.</summary>
        public long? WaferOffsetX { get; set; }
        public long? WaferOffsetY { get; set; }

        /// <summary>Wafer radius in motor steps from the scan's circle fit, checked against the
        /// operator's nominal diameter as the scan's main sanity check.</summary>
        public long? WaferRadius { get; set; }

        /// <summary>De-rotation handedness the Θ scan settled on (±1), needed to turn
        /// <see cref="WaferOffsetX"/> back into a motor position at a given Θ.</summary>
        public int? WaferFitSign { get; set; }

        /// <summary>Θ-scan fit quality — a stored centre with no record of how well it fitted cannot
        /// be judged later.</summary>
        public double? WaferFitRms { get; set; }
        public int? WaferFitN { get; set; }
        public string? WaferFitTimestamp { get; set; }

        #endregion

        #region Notch

        /// <summary>Bearing of the notch from the wafer centre in the CHUCK's rotating frame, so
        /// turning the notch to a datum is just a Θ move of (datum − this). Belongs to the WAFER, not
        /// the machine: void the moment the wafer is lifted or re-placed.</summary>
        public double? NotchAngleDeg { get; set; }

        /// <summary>Depth the notch measured. A SEMI 200 mm notch is 1.00 mm; far off that means the
        /// search latched onto something else and the angle should not be trusted.</summary>
        public double? NotchDepthMm { get; set; }
        public string? NotchTimestamp { get; set; }

        #endregion

        #region Derived values

        /// <summary>Motor position putting the WAFER centre under the crosshair at
        /// <paramref name="chuckAngleDeg"/>, rotating the stored chuck-frame offset back out to the lab
        /// frame. Null unless a Θ scan has run, a chuck centre exists, and X and Y both have
        /// StepsPerMm — the rotation is only a rotation in mm.</summary>
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

        #endregion

        #region Persistence

        private static readonly JsonSerializerOptions Opts = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        };

        public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "calibration.json");

        /// <summary>Loads the saved calibration, or a fresh store if none exists or it is corrupt.
        /// <paramref name="warning"/> is set (and the bad file preserved as calibration.corrupt.json)
        /// when an existing file could not be read — the caller MUST surface it, because an empty
        /// store silently removes the only travel protection Z and X have.</summary>
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

        /// <summary>Moves an unreadable calibration file aside so the next <see cref="Save"/> can't
        /// silently overwrite it. Best effort — never throws.</summary>
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
            catch { }
        }

        /// <summary>Writes the store atomically (temp file + replace) so a crash mid-write can't
        /// truncate the live calibration and silently drop the limits. Throws on IO failure.</summary>
        public void Save()
        {
            string json = JsonSerializer.Serialize(this, Opts);
            string tmp = DefaultPath + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(DefaultPath)) File.Replace(tmp, DefaultPath, null);
            else File.Move(tmp, DefaultPath);
        }

        #endregion
    }
}
