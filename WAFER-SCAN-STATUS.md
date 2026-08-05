# Wafer centre-find by Θ scan — status

**As of 2026-08-04. Builds 0 warnings / 0 errors. Offline maths checks all pass.** Branch
`ui-rework`, nothing committed.

**First hardware runs happened, and produced two changes (2026-08-04, later):**

1. **Acquisition rewritten.** The cardinal-direction probe out from the chuck centre could not start
   at all — the best cardinal reaches 88 mm and a 200 mm rim is at 100 mm. The run now parks at the
   travel **corner** (X min, Y max), which stands ≈100.8 mm off the axis, and rasters **down** in Y
   until the rim appears. A pre-flight check refuses up front if the nominal rim radius is outside
   the 86.3–113.8 mm band the line X = X min sweeps. `TryPickStationDirection` and the wafer use of
   `ProbeAsync` are gone; the run also now ends by driving to the measured wafer centre.
2. **The detector now deliberately reports the gap↔CHUCK boundary** (2026-08-05). The chuck is the
   in-focus surface, so that boundary is the sharper and more repeatable of the two; the bevel side
   is a specular gradient that moves with the illumination. Either side is only a constant radial
   offset from the true rim, so the recovered *centre* is unaffected and only the fitted *radius*
   shifts — what matters is that the choice is consistent from sample to sample, which
   nearest-to-the-crosshair was not.

   The side is chosen on **brightness**: the bevel throws a near-saturated specular glint that hugs
   the gap, the chuck is diffuse and mid-grey. Measured over the captures on file the chuck collar
   runs 0.4–0.8× the wafer collar's mean at every collar width tried, with ~1 % saturated pixels
   against 14–80 %. `SideProbeRadius` (50 px).

   **The comparison is between the two LARGEST collar pieces, not a threshold** (fixed 2026-08-05,
   after a live Θ scan reported noise and false edges that the tuning script did not reproduce).
   `connection` shatters a ragged collar into 6–13 fragments, and a bevel-side sliver reads darker
   than the bevel proper, so the old `SideDarkFraction ≤ 0.85 × brightest` rule admitted fragments
   from *both* sides — four of six pieces on `capture_20260804_114358_183.bmp` — and the rim ring
   straddled the gap. The gap has exactly two sides, so comparing only the two largest needs no
   cut-off at all and `SideDarkFraction` is gone. Fewer than two sides in frame now **refuses** the
   frame rather than falling back to the darkest: with the wafer out of view nothing says which
   boundary faces the chuck, and on `..._160143_116` that fallback was keeping a mean-205 collar —
   the bright side — and calling it the chuck.

   Verified offline over every `.bmp` in `Desktop/images`: each current-optics rim frame now returns
   a single-piece contour, with the reported point unchanged wherever it was already correct.
   `..._143559_245` and `..._160143_116` (both 31 July, old optics, and the second is the *chuck*
   detector's tuning capture) now return false — see above for why that is the wanted behaviour.

   **Third filter: FLANK CONTRAST, `MaxSideContrast` = 0.80** (2026-08-05). Reported symptom: the
   detector is accurate while the crosshair sits on the wafer, but "frequently picks random edges on
   the chuck surface" once the crosshair is over the chuck. Mechanism: the reported point is the
   boundary point *nearest the crosshair*, so with the rim far away any surviving trough near the
   centre wins — and neither `MinArea` (troughs reach 1.5 Mpx) nor `AcceptDetection`'s radial band
   (a trough by the crosshair sits at ~the station's own radius) objects. `MaxMeanFraction` catches
   most troughs but is a fraction of `Cut`, and `Cut` drifts *up* in exactly the mostly-chuck framing
   where this appears. A rim gap has the wafer on one flank; a trough has chuck on both — measured,
   rim gaps 0.46–0.73 against troughs 0.89–0.99. Applied **per region**, before the merge; the rim
   ring is assembled from the regions that pass.

   The contrast filter rejects all 6 troughs and passes all 8 rim gaps on file. It was *not* what
   caused the reported symptom, though — see the next item. The wafer scan log now carries the
   detector's per-detection view (`cut=… parts=… big=… dark=… | r1 a=… m=… flanks=…/… c=… KEEP`).

3. **`SeverRadius` = 35 — the actual cause of "it targets the gashes in the chuck"** (2026-08-05,
   diagnosed on `capture_20260805_111803_054.bmp`, the frame the user supplied). The chuck's machined
   gashes read below `Cut`, and `CloseRadius` bridges the near ones **into the black band**. That
   makes one connected region whose chuck-side boundary grows dendritic tendrils reaching hundreds of
   px across the chuck — and those tendrils are the rim boundary as far as everything downstream is
   concerned, so the nearest-to-crosshair point lands on one. **Every existing filter passed it**: the
   region is a genuine gap plus its tendrils, so area (997,572), mean (29.3) and flank contrast (0.53)
   all read like a real rim. Neither the two-largest-flanks fix nor `MaxSideContrast` could have
   caught this; it is a segmentation fault, not a selection one.

   Fix: an opening of 35 px applied **after** the closing, severing anything narrower than 70 px. Band
   ~345 px, tendrils a few tens. Region area 997,572 → 798,899; the reported point moves from 375 px
   from the crosshair (out in the chuck) to 1,240 px (on the real band); contour length 26,754 →
   6,420 px. Area plateaus from ~35 up (813 k @25, 799 k @35, 790 k @45, 786 k @60) and the point
   stops moving there.

   **It must come after the closing**, not as a larger `CleanRadius` — measured: opening that wide
   first leaves dust specks too big for `CloseRadius` to fill, and it lets bare-chuck
   `..._114724_720` segment into something surviving `MinArea` (detects at radius 25 and 35, saved
   only by the flank gate). Applied after the closing, all three rejections and every rim detection
   on file are preserved, and the flank count drops from 3–6 to 2 on most frames as a side effect of
   the smoother outline.

   **Texture does not work here**, despite the chuck being the surface that looks textured: the
   glint is a saturated ridge speckled with dark pits, so the *wafer* collar measures rougher (dev
   52–74 vs 41–49, local gradient 2–3× at a 30 px collar). Its polarity also flips between the
   current optics and captures taken before the chuck was focused. An earlier revision of this file
   claimed the opposite on both counts; it was measured on the old optics.

Also: the mid-sweep re-acquire search is **bidirectional** in Y. Down-only was tried on hardware and
sometimes drove the edge further out of view — necessarily, since the outward radial direction at
the station is +Y and the eccentricity swings the rim both ways over a revolution.

Still NOT verified: a full run on hardware end to end, and the detector fix against the captures on
file.

Full design record: `docs/developer-guide/WaferCentreByRotation.md`, plus §18 of the developer
guide. This file is the working status — what is done, what is not, and what to do next.

---

## The problem, and the method

Pointing the chuck centre-find at the wafer does not work, for a geometric reason rather than a
software one. The camera is fixed and the table moves, so seeing a rim point means driving the
stage to it — and viewing the whole rim needs motor positions on a circle of the feature's own
radius:

| | Radius in steps | X travel 220,516 | Y travel 158,624 |
|---|---|---|---|
| Chuck inner circle | ≈7,000 | fits easily | fits easily |
| 200 mm wafer rim | ≈126,000 | **needs ≈200 mm of span** | **needs ≈200 mm of span** |

Neither axis has the span. Only a band of the rim is reachable, and a circle fit over one short arc
leaves the centre component perpendicular to the chord essentially unconstrained.

**The chuck *is* the Θ axis, and Θ is continuous.** So the rim is not reached — it is delivered.
Park the stage on one reachable spot on the rim, turn Θ through a full revolution, and de-rotate
each sample by the angle it was taken at. The points then span a full 360° in the chuck's own
frame, and go into the same Pratt `CircleFit` the chuck centre-find already uses. A partial-arc
problem becomes a full-circle one.

---

## Files

**Added**

| File | What |
|---|---|
| `Vision/WaferCentreScan.cs` | The maths: mm conversion, de-rotation, both-sign fit, outlier drop. No HALCON, no WinForms. |
| `Vision/FrmVisionProtocols.AutoWafer.cs` | Orchestration, modelled on `AutoCentre.cs` — same `BeginExternalOp` lockout, `WithinTravel` guard, fresh position reads, two-frame confirmation. |
| `docs/developer-guide/WaferCentreByRotation.md` | Derivation, parameter table, verification. |

**Changed**

* `Calibration/Calibration.cs` — new stored fields + `WaferCentreAt(chuckAngleDeg)`.
* `Vision/FrmVisionProtocols.cs` — the wafer UI group replaced (Wafer Ø, Samples, Run, Cancel, Go
  to Centre, log, result).
* `Vision/FrmVisionProtocols.AutoCentre.cs` — `AutoTarget { Chuck, Wafer }` field so
  `DetectEdgeAsync` / `ProbeAsync` / `AutoLog` serve both features; log and status routed by target.
* `Vision/FrmVisionProtocols.CentreFind.cs` — manual wafer methods removed; `DrawEdgeOverlay` now
  takes plain row/col so both detectors share it; `GoToWaferCentreAsync` recomputes per Θ.
* `FrmMain.Rotation.cs` — `RotateThetaOnlyAsync(deg, speed)`.
* `FrmMain.Calibration.cs` — `TryReadThetaNow(out long ticks)`.
* `FrmMain.RelativeMove.cs` — "Move to wafer centre" recomputes for the current Θ; gated on the
  offset, not the snapshot.
* `IMotionHost.cs` — the two new members above.
* `Drive/MotionTypes.cs` — `TableAxes.For(id)`, to stop indexing `Default` by the enum value.
* `Halcon/wafer center.hdev` — rewritten 2026-08-04 as a step-through tuning script in the same
  style as `chuck edge detector.hdev`, mirroring the new pipeline. The old file dated from 1 July
  and had never mirrored the C# at all (`auto_threshold` + `inner_radius` + a Canny contour sort),
  and read from a `./wafer edge images` folder that does not exist. Step 4 lists area, mean and
  mean/`Cut` per candidate region with its KEEP/drop verdict — the diagnostic to read when a frame
  misbehaves.
* `docs/developer-guide/index.md` (new §18; old §18–24 renumbered to §19–25),
  `docs/user-guide/index.md` §10–11, `docs/index.md`.

**Deleted — the manual wafer flow, at the user's request**

`Add Wafer Edge` / `Clear` / `Compute Centre` buttons, `_waferFinder`, `OnWaferGrabbed`,
`ClearWaferPoints`, the old `RefreshWaferUi`, `ComputeWaferCentre`. Only the Θ scan remains.

`WaferEdgeDetector` itself **stays** — the scan uses it as its detector. Its segmentation was
rewritten on 2026-08-04 to cut on the off-wafer side (see the gotcha below); the public surface is
unchanged.

---

## Results worth not re-deriving

**A wrong chuck centre does not bias the offset.** If `C` is off by `δ`, then

```
P_k = R(−θ_k)(E_k − C_true − δ) = W_true + R(−θ_k)·(R_w·n̂ − δ)
```

— still **exactly** a circle centred on `W_true`, with the radius changed to `|R_w·n̂ − δ|`. The
error is absorbed entirely by the radius. Two consequences:

* the eccentricity is measured relative to the **true rotation axis** regardless of how good `C` is;
* a fitted radius disagreeing with the nominal diameter is a **free diagnostic on the chuck
  centre**, not just on the wafer.

The absolute lab position still inherits `δ`, because converting the offset back to a motor
position adds `C` in again. That error was already in everything else built on `C`.

**Work in millimetres, not steps.** X and Y differ by 0.4 % in steps/mm (1261.5 vs 1256.5), and a
rotation is only a rotation once that anisotropy is divided out.

**The de-rotation sign is `RotationSign · sign(det A)`.** `RotationSign` is a *pixel*-space
handedness — that is where `CrosshairRotation` applies it — so mapping it to step space costs the
determinant's sign. With the current affine `det A = +2.46`, so σ is just `RotationSign` (−1 here).

**The wafer centre is not a fixed point.** It orbits the rotation axis as Θ turns, moving by `2e`
between opposite angles, so a single `WaferCenterX/Y` is only valid at one unrecorded angle. Hence
`WaferOffsetX/Y` (chuck rotating frame, de-rotated to θ = 0) + `WaferRadius` + `WaferFitSign` +
`WaferFitRms/N/Timestamp`, and `WaferCentreAt(θ)`. `WaferCenterX/Y` is still written as a snapshot
so nothing that read it broke.

---

## The bug the offline check caught

Choosing the handedness by "lower RMS wins" is **silently wrong at small N**. Any 3 points lie
exactly on *some* circle, so at N = 3 both signs fit perfectly and the tie broke on floating-point
noise — mirroring the answer with no visible symptom.

Now: the data decides only when the loser's RMS exceeds `SIGN_SEPARATION_MM` (0.05 mm) **and** the
winner beats it by 2×; otherwise `expectedSign` breaks the tie; with neither available the fit
**fails** rather than guessing.

---

## Verified vs not

**Verified (offline, synthetic scans):**

* exact recovery of offset, radius and handedness on an ideal 24-sample scan;
* data overriding a deliberately wrong `expectedSign`;
* the δ result above — a deliberate `δ` leaves the offset bit-identical and moves the radius to
  exactly `|R_w·n̂ − δ|`;
* a corrupted sample dropped, offset unaffected;
* N = 3 resolving via the expectation, and failing when there is none;
* `WaferCentreAt` round-tripping and swinging by exactly `2e` between 0° and 180°.

**Verified (offline, real captures):** `WaferEdgeDetector` run over every rim capture on disk.
It lands on the wafer's bright→dark silhouette edge in `capture_20260803_175836_162.bmp`, where the
old bright-side pipeline returned the bevel, 1000 px away on the wrong side. On
`capture_20260803_175729_116.bmp`, a frame wholly on the die field with no rim in it, it now returns
**false**; the old pipeline returned a die-pattern boundary 14 px from the crosshair.

`Halcon/wafer center.hdev` was checked the same way — driven headless through **HDevEngine** and
compared against the C# on the same frames. It agrees exactly: `Split=163`, `Cut=77`, edge point
`(1287.5, 2406.5)` on `capture_20260803_175836_162.bmp`; `Split=121`, `Cut=78`,
`(1085.5, 1913.5)` on `capture_20260804_114724_720.bmp`; and no region kept on
`capture_20260803_175729_116.bmp`. Two HDevelop traps were found doing this
and are commented in the script: `/` **truncates when both tuple operands are integer**, so
`gray_histo`'s counts must be forced real before the class means are taken, and `==` on tuples
compares the whole tuple rather than element-wise, so it cannot build a mask (`tuple_max2` does).

**Darkness filter added 2026-08-04 (`MaxMeanFraction`), and `MinArea` raised 5e4 → 2e5.** Otsu
always returns a cut, so a frame containing no rim at all still segments into something. The chuck
is machined, and under oblique light its shadow troughs come out as long dark-ish blobs of
0.7–1.5 Mpx — *as large as a real gap*, so area alone passes them. On
`capture_20260804_114724_720.bmp` (bare chuck, no wafer anywhere in view) the detector reported one
of those troughs as a rim point.

What separates them is grey level, measured on the component that actually supplies the reported
point:

| frame | winner area | mean | mean/`Cut` | what it is |
|---|---|---|---|---|
| `..._143559_245` | 6,200,003 | 10.3 | 0.28 | rim gap |
| `..._160143_116` | 8,840,045 | 13.7 | 0.24 | rim gap |
| `..._114358_183` | 1,384,942 | 27.1 | 0.33 | rim gap |
| `..._175836_162` | 1,523,671 | 28.1 | 0.36 | rim gap |
| `..._105121_943` | 777,390 | 34.0 | 0.52 | rim gap |
| `..._135114_498` | 94,497 | 47.6 | 0.70 | **chuck texture** |
| `..._135135_136` | 1,493,102 | 49.1 | 0.73 | **chuck texture** |
| `..._114724_720` | 141,320 | 63.4 | 0.81 | **chuck texture** |

`MaxMeanFraction = 0.6` sits in clear air between 0.52 and 0.70. It has to be a *fraction of* `Cut`
rather than an absolute grey level, because `Cut` is relative by design and runs 37–96 across these
frames. Area is still needed alongside it — 94 k and 1.49 Mpx both appear in the texture rows.

Applied as a **filter** (`select_gray`), not a verdict, and the ordering matters: on
`135114_498` the real gap is present and kept but a texture trough sits nearer the crosshair, so
dropping the trough lets the gap win rather than failing the frame. Results: `114724_720` now
returns **false**; `135114_498` and `135135_136` move off texture onto the real gap; the five
verified rim frames and the three correct rejections are all unchanged.

Two notes. Standard deviation does *not* separate these (4.4–38.5 good against 17.0–20.3 bad), so
the mean is doing the work. And measuring the wrong region misleads badly — comparing regions in
`select_shape` order rather than the one that supplies the point makes the separation look like it
collapses under normalisation, which is what nearly got this filter dismissed.

**Not verified:** anything on hardware. No camera, no motion, no real wafer. The detector checks
above are stills, not a live arc.

> The offline harness lives in the **session scratchpad**
> (`…\Temp\claude\…\scratchpad\ScanCheck\`), which will not survive. It is a small console project
> referencing `bin\Debug\net10.0-windows\NanotecController.dll` and driving `WaferCentreScan` with
> generated samples. Worth moving into the repo if these checks should be re-runnable — say the
> word and I will.

---

## Next: hardware verification

0. **The detector fix, offline first** — run `WaferEdgeDetector` over the `.bmp` captures in
   `Desktop/images` and confirm the reported point sits on the **dark, in-focus chuck** side of the
   black band, that the two collar means separate cleanly, and that the two correct rejections
   (`..._175729_116`, die field; `..._114724_720`, bare chuck) still return false. Then the same
   frames through `Halcon/wafer center.hdev` under HDevEngine — its step 5 prints each collar's
   mean, deviation and mean/brightest with a KEEP/drop verdict, which is the column to read when a
   frame misbehaves.

   Also worth measuring while the harness is up, because it is the one thing this change depends on
   that has not been checked: the black band's **perpendicular width around the wafer**. A constant
   width makes the chuck-side boundary an exact concentric circle; a varying one distorts it, and
   the distortion lands in the fit. Nothing in the captures on file contradicts a constant width,
   but nothing on file establishes it either.
1. **Preconditions** — camera-scale calibration done, chuck centre found, steps/mm set on X and Y,
   travel limits found. The run refuses without them and says which is missing.
2. **Dry read of the log** — with the stored limits, expect the station line at `X = -108,939`, the
   park at `Y = 65,395`, and the rim within 1-2 descent steps (the first crossing is ~1,990 steps
   down). Every commanded target must have passed `WithinTravel`.
3. **Full run** with a deliberately off-centre wafer. Expect a fitted radius within a few hundred
   steps of `Ø/2 × StepsPerMm`, an RMS comparable to the chuck fit's, and the closure sample
   matching the first.
4. **The definitive test** — this is the rotational-invariance check that
   `docs/developer-guide/ChuckCenterFindingAnalysis.md` §6 describes as not implemented:

   > Drive to `WaferCentreAt(current Θ)`. Rotate Θ by 180°. Drive to `WaferCentreAt(new Θ)`. The
   > same point on the wafer must still be under the crosshair. If the eccentricity is wrong, the
   > wafer centre visibly swings by `2e`.

   Do this before trusting a number. A single-pass fit's own RMS **cannot** catch a systematic bias
   — a skewed affine, a wrong handedness, a mis-scaled `StepsPerMm` — because those move every
   point coherently and leave the RMS looking healthy. The rotation test can.
5. **Repeatability** — re-run from a different starting Θ and a different station direction; the
   recovered offset must agree.

### Things most likely to need tuning first

* `SideProbeRadius` (50 px) — the collar width for the side choice. Must stay well under the bevel's
  ~310 px, or the wafer-side collar reaches past the glint into the darker wafer surface and the
  brightness contrast collapses. `MinCollarAreaPx` (5,000) alongside it, which decides what counts
  as a side at all rather than a fragment.
* **`MinArea` (2e5) is an absolute pixel count, so any Zoom above 1× breaks the wafer detector.**
  The zoom is a centred camera-ROI crop; less gap is in view, the region falls under `MinArea`, and
  the frame is rejected at step 4. Measured on `..._093524_418`: fine at 1×, rejected at 2× and
  every step above. Run wafer scans at 1×, or make `MinArea` a fraction of the frame.
* `WAFER_SEARCH_HOPS` (6) if the wafer turns out more eccentric than ~4.5 mm.
* `WaferEdgeDetector`'s morphology radii / `MinArea` on the live arc — still the untuned levers.
  `MinArea` (5e4 px²) is what stops a dark die structure being mistaken for the gap beyond the rim.
* `WAFER_BAND_LO/HI_FRAC` (0.70 / 1.30) if the eccentricity is larger than expected.
* `WAFER_CLOSURE_TOL_STEPS` (400 ≈ 0.32 mm) if the closure check proves too tight in practice.
* `WAFER_THETA_SPEED` (3000; Θ tops out at 3200) — a revolution is 359,859 ticks, ≈2 minutes, and
  dominates the run time. Sample count barely affects it, which is why 24 is not expensive.

---

## Gotchas recorded during the build

* **Segmenting the bright wafer finds the BEVEL, not the rim.** Checked against
  `images/capture_20260803_175836_162.bmp`: the bevel is a ~310 px mid-grey band (136–148) that
  falls below Otsu's 163, so it splits the wafer in two. `max_area` then picks the wrong side, and
  even on the right side the *nearest* boundary is the bevel (348 px) not the rim (537 px). Closing
  the gap is no fix either — the bevel (310 px) and the dark gap beyond the rim (345 px) are near
  enough the same width that no radius bridges one without the other. `WaferEdgeDetector` therefore
  segments the **off-wafer** side with two-stage Otsu (second cut taken inside the dark part alone):
  the bevel is mid-grey, only the world past the rim is black.
* That also killed the old frame-border artefact — with the wafer filling the view there is no large
  dark region, so the detector returns **false** instead of confidently reporting the frame border.
  The scan's 8 px border gate and `[0.70, 1.30] × nominal` band are now second-line, not
  load-bearing.
* **`RotateToAngleAsync` / `RotateAboutCrosshairAsync` are the wrong primitive here** — they drag
  X/Y to pin the point under the crosshair, which would keep re-viewing the *same* piece of rim.
  Hence the plain `RotateThetaOnlyAsync`.
* **The polled position cache is stale for a poll period after every move** — the scan reads both
  X/Y and Θ fresh, for the same reason `TryReadUserXyNow` exists.
* **Θ must be folded through `ChuckTicksPerRev` (359,859)**, never the motor's 40,000 — the ≈9:1
  reduction would wrap nine times per chuck revolution.
* **Keeping the rim in frame** was originally done by moving the station onto each rim point `E_k`
  (by definition the position that puts it on the crosshair). That is gone: the sweep is Θ-only and
  the station moves only to re-acquire a lost rim, searching Y **both ways** — see the note above on
  why down-only cannot work.
* **The gap has TWO boundaries and only one is measured** — the chuck-side one. "Nearest the
  crosshair" picks whichever side the crosshair sits on, so it wanders between the two. Nothing
  downstream catches that: the boundaries are ~0.35 mm apart, well inside the `[0.70, 1.30] ×
  nominal` band, so it passes every gate and biases the fit in a way the RMS cannot see. Hence the
  brightness test. The standing diagnostic is the fitted radius, which should now sit *above*
  `Ø/2 × StepsPerMm` by the gap's width and stay there — near or below it means the detector is
  slipping onto the bevel side.

---

## Repo state

Working tree is dirty and nothing is committed. Note that `Drive/AxisDriver.cs`,
`Drive/DrivePoller.cs`, `Drive/MultiAxisController.cs`, `Drive/SoftLimitTracker.cs` and
`FrmMain.Jog.cs` were **already modified before this work started** — they are unrelated to the Θ
scan and were not touched.
