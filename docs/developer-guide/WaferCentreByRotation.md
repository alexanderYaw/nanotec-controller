---
title: Wafer Centre-Finding by Rotation
---

# Finding the Wafer Centre by Rotating It

The chuck centre-find drives the stage around the chuck rim, captures a rim point at several
spots, and Pratt-fits them (**[Chuck Center-Finding Analysis](ChuckCenterFindingAnalysis/)**,
**[Automated Chuck Centre-Finding](ChuckCenterFindingAutomation/)**). The same method cannot be
pointed at the wafer, for a geometric reason rather than an algorithmic one.

**This is implemented** as the *Auto wafer centre-find (Θ scan)* controls in the vision protocols
window: orchestration in `Vision/FrmVisionProtocols.AutoWafer.cs`, maths in
`Vision/WaferCentreScan.cs`. There is no manual wafer flow — the old "Add Wafer Edge / Compute
Centre" buttons were removed with this change, because they could only collect the short,
badly-conditioned arc described in §1.

---

## 1. Why the chuck method cannot be reused

The camera is fixed and the table moves, so every stored geometric quantity is *the motor position
that puts feature X under the crosshair*. Viewing a rim point requires driving the stage to that
point's position, so the set of positions that views the whole rim is a circle of the feature's own
radius about its centre.

For the chuck's inner circle that radius is ≈7,000 steps (≈5.6 mm). For a 200 mm wafer it is
≈100 mm, i.e. ≈126,000 steps:

| Axis | Travel (steps) | Travel (mm) | Rim circle needs |
|---|---|---|---|
| X | 220,388 | ≈174.7 | ≈200 mm of span |
| Y | 158,518 | ≈126.2 | ≈200 mm of span |

Neither axis has the span, so only a band of the rim is reachable. A circle fit over one short arc
is badly conditioned: the centre component perpendicular to the arc's chord is nearly
unconstrained, so small edge noise displaces the fitted centre far along that direction. Two
opposed arcs improve this but still leave the perpendicular direction weak.

## 2. Rotate the wafer instead of circling it

**The chuck is the Θ axis.** Θ is continuous, unbounded, and carries the wafer, so the rim need not
be reached: park the stage on one reachable spot on the rim and rotate Θ, and every part of the rim
passes under the camera in turn. One station plus one revolution samples the whole circle from
inside the reachable band. This is the method commercial wafer pre-aligners use.

## 3. De-rotation, then the existing fit

All quantities are in USER-frame motor steps unless noted. `C` is the stored chuck centre, `A` the
2×2 pixel→step affine.

At sample *k*, with chuck angle $\theta_k$, motor position $\mathbf{M}_k$ and detected rim pixel
$\mathbf{p}_k$:

1. $\mathbf{E}_k = \mathbf{M}_k + A(\mathbf{p}_\text{cross} - \mathbf{p}_k)$ — the rim point in step
   space. This is `CentreFinder.ToStepPoint`, unchanged from the chuck flow.
2. $\mathbf{v}_k = \mathbf{E}_k - \mathbf{C}$, converted to **millimetres** by the per-axis
   `StepsPerMm` ($k_X$ = 1261.5, $k_Y$ = 1256.5).
3. De-rotate: $\mathbf{P}_k = R(-\sigma\theta_k)\,\mathbf{v}_k$.
4. Circle-fit the $\mathbf{P}_k$ with the existing Pratt `CircleFit`.

**Step 4.** Write the lab-frame rim circle at angle $\theta$: its centre is
$\mathbf{C} + R(\theta)\mathbf{W}$, where $\mathbf{W}$ is the wafer centre expressed in the chuck's
rotating frame, and its radius is $R_w$. Any measured rim point is

$$\mathbf{E}_k = \mathbf{C} + R(\theta_k)\mathbf{W} + R_w\hat{\mathbf{u}}_k,$$

with $\hat{\mathbf{u}}_k$ the unit vector from the wafer centre to the measured point. De-rotating,

$$\mathbf{P}_k = R(-\theta_k)(\mathbf{E}_k - \mathbf{C}) = \mathbf{W} + R_w\,R(-\theta_k)\hat{\mathbf{u}}_k,$$

so $\lVert\mathbf{P}_k - \mathbf{W}\rVert = R_w$ **exactly, for every $k$, whatever
$\hat{\mathbf{u}}_k$ was**. The $\mathbf{P}_k$ are the rim points expressed in the chuck's own
rotating frame, and they span the full 360° even though all of them were measured from the same
small patch of reachable travel. The fitted centre is the wafer's offset from the rotation axis;
the fitted radius is the wafer radius. A partial-arc problem has become a full-circle problem, and
the fit already trusted for the chuck is reused unchanged.

### The chuck angle must come from `ChuckTicksToDegrees`

Θ turns the chuck through a ≈9:1 reduction. `CrosshairRotation.ChuckTicksPerRev` (359,859) is the
only correct divisor; the motor's 40,000 ticks/rev wraps nine times per chuck revolution and would
make every de-rotation angle wrong by a different amount.

The angle is read fresh through `IMotionHost.TryReadThetaNow`, not from the polled cache, which
holds the pre-rotation angle for at least one poll period after each move — the same staleness that
`TryReadUserXyNow` exists for.

## 4. An error in the chuck centre does not bias the offset

The stored `C` comes from fitting the chuck's machined inner circle, which is not guaranteed to
coincide with the true rotation axis. Suppose it is wrong by $\boldsymbol{\delta}$, i.e.
$\mathbf{C} = \mathbf{C}_\text{true} + \boldsymbol{\delta}$. Substituting into the identity above:

$$\mathbf{P}_k = R(-\theta_k)(\mathbf{E}_k - \mathbf{C}_\text{true} - \boldsymbol{\delta})
= \mathbf{W}_\text{true} + R(-\theta_k)\big(R_w\hat{\mathbf{u}}_k - \boldsymbol{\delta}\big).$$

If $\hat{\mathbf{u}}_k$ is the same direction $\hat{\mathbf{n}}$ at every sample, then
$\lVert\mathbf{P}_k - \mathbf{W}_\text{true}\rVert = \lVert R_w\hat{\mathbf{n}} - \boldsymbol{\delta}\rVert$
is constant and the points lie **exactly** on a circle centred on $\mathbf{W}_\text{true}$, with the
radius changed. The chuck-centre error is absorbed entirely by the radius and does not bias the
measured offset at all.

$\hat{\mathbf{u}}_k$ is not exactly constant: the station is a fixed lab point while the wafer
centre orbits, so the bearing from centre to station varies by $\pm\arctan(e/R_w)$. At $e$ = 3.2 mm
and $R_w$ = 100.2 mm that is ±1.8°, and the resulting spread in the fitted radius is bounded by
$\lVert\boldsymbol{\delta}\rVert \cdot \Delta\hat{\mathbf{u}} \approx 0.03\lVert\boldsymbol{\delta}\rVert$
— under 30 µm for a 1 mm chuck-centre error, against a measured fit RMS of 0.15 mm. The result is
therefore exact to well inside the noise, not merely approximate.

Three consequences:

* The eccentricity the scan reports is relative to the **true rotation axis**, regardless of how
  good `C` is. That is the quantity that matters, because it is what rotation does to the wafer.
* The *absolute* lab position still inherits $\boldsymbol{\delta}$, because converting the offset
  back to a motor position adds the stored `C` in again. That error was already present in
  everything else built on `C` and is not made worse here.
* A fitted radius that disagrees with the operator's nominal wafer diameter is an independent check
  on the chuck centre, obtained at no extra cost.

Verified offline: see §9.

## 5. The handedness σ

`CalibrationStore.RotationSign` is the image handedness of a positive Θ move, and
`CrosshairRotation` applies it in **pixel** space. The de-rotation here happens in step/mm space, and
a linear map reverses orientation exactly when its determinant is negative, so

$$\sigma_\text{expected} = \text{RotationSign} \cdot \operatorname{sign}(\det A).$$

On the affine of 2026-08-07, $\det A = X_rY_c - X_cY_r = +2.50$, so $\sigma$ equals `RotationSign`
(−1 on this machine).

That expectation is not trusted on its own: `RotationSign` is null until the sign test has been run,
and a wrong σ produces a mirrored but otherwise plausible centre. `WaferCentreScan` therefore fits
**both** signs and:

* if one is clearly better — the loser's RMS exceeds `SIGN_SEPARATION_MM` (0.05 mm) **and** the
  winner beats it by 2× — the **data decides**, and disagreement with $\sigma_\text{expected}$ is
  logged as a warning that one of the two is wrong;
* otherwise the scan cannot separate them and $\sigma_\text{expected}$ breaks the tie;
* with no expectation available, the fit **fails** rather than guessing.

The tie case is real. **Any 3 points lie exactly on some circle**, so at N = 3 both handednesses fit
with zero residual and an RMS comparison decides on floating-point noise. The offline check in §9
caught this, and it is why the margin and the floor exist.

## 6. Keeping the rim inside a 5 mm field of view

With X/Y parked and Θ turning, the rim sweeps **radially** past the camera by ±e (the eccentricity),
and the nearest rim point also moves tangentially by roughly e. A hand-placed wafer can exceed the
frame in both.

The scan holds X and Y still and moves them **only when a sample misses**, searching along Y about
the station — ±1, ±2 … up to `WAFER_SEARCH_HOPS` hops, down first — and moving the station to
wherever it re-acquired. If the search finds nothing the station is left where it was, so one lost
sample cannot leave the run parked away from the rim, and the angle is **skipped**. Missed samples
must never abort a run.

### The search must go both ways

Down-only was tried first and fails for half of every revolution. The outward radial direction from
the rotation axis at the station is **+Y**, so:

* rim swings **outward** — the camera is now inside the wafer, and recovery is +Y. A downward search
  traverses the wafer's ≈100 mm interior before re-finding the rim at the opposite crossing.
* rim swings **inward** — the camera is outside, and recovery is −Y, one or two hops.

Both occur once each per revolution, which matches the reported symptom: sometimes moving down
restored the edge, sometimes it drove it further out of view.

Moving along Y by $\Delta Y$ changes the radial coordinate by $\Delta Y\,\lvert\hat{\mathbf{r}}\cdot\hat{\mathbf{y}}\rvert$,
so following a radial swing of $e$ costs

$$\Delta Y = e \,/\, \lvert\hat{\mathbf{r}}\cdot\hat{\mathbf{y}}\rvert .$$

At the first crossing $\lvert\hat{\mathbf{r}}\cdot\hat{\mathbf{y}}\rvert \approx 0.5$, so the search
covers roughly $\pm\,\tfrac12\,(\text{hops} \times \text{hop})$ of eccentricity — ±4.5 mm at 6 hops
of 1.5 mm. It is deliberately **bounded**: skipping a sample is cheap, and an unbounded search would
turn one lost sample into a 100 mm traverse.

That the rim leaves the frame at all also puts a floor on the eccentricity in play. The field is
≈3.8 mm across, so a wafer within ≈1 mm of the rotation axis would never lose it; the searching seen
on hardware implies at least ≈2 mm.

### Which edge the detector measures

The obvious segmentation — threshold the bright wafer, take its boundary — **finds the bevel, not
the rim.** In a 4016 × 3024 frame across the rim (`images/capture_20260803_175836_162.bmp`), the grey
levels along the row through the crosshair run:

| Columns | Grey | What |
|---|---|---|
| 0–1310 | 255, dipping dark on dies | wafer surface |
| 1360–1670 | 136–148 | **bevel** |
| 1690–2540 | 255 | wafer, outside the bevel |
| 2555–2890 | 10–27 | the unlit gap beyond the rim |
| 2900–4015 | 60–115 | textured background |

Otsu puts the cut at 163, so the bevel reads *dark* and splits the wafer into two blobs. Two separate
failures follow, and fixing either alone is insufficient:

* the larger blob is the inner one, so `max_area` selects the wrong side of the bevel;
* even on the correct blob, its **nearest** boundary to the crosshair is the bevel (348 px) rather
  than the rim (537 px).

No rule of the form "brightest blob, nearest boundary" can work here. Closing the gap is also ruled
out: the bevel is ≈310 px wide and the dark gap beyond the rim ≈345 px, so any closing radius that
bridges the bevel also bridges the rim.

**The detector therefore segments the off-wafer side.** The bevel is mid-grey; only the region
beyond the wafer is black. The cut is **two-stage Otsu** — the first split isolates the lit wafer,
the second is taken *inside the darker part alone*, which separates sustained black from mid-grey
bevel and background texture. Both stages track exposure, as the single stage did.

This also removes the frame-border artefact that previously needed guarding. When the wafer covers
the entire view there is no large dark region, so the detector returns **false** rather than
reporting the frame border. Boundary points lying on the frame itself are dropped, which is what a
frame taken wholly *off* the wafer would otherwise produce. The scan's own gates remain as
additional checks: a detection within `WAFER_BORDER_MARGIN_PX` (8 px) of a frame edge is refused, as
is one whose radius falls outside `[0.90, 1.10] × nominal`.

The cost is a new dependency: the gap beyond the rim must actually be dark. It is — the gap is
geometric, the wafer standing proud of the chuck under oblique light — and it is present in every rim
frame captured so far, six weeks apart. If a scan ever misses at every angle, check this first.

**Dark is not sufficient on its own.** Otsu always returns a cut, so a frame with no rim still
segments into something, and the chuck's machined surface is what gets segmented. Under oblique
light its shadow troughs form long dark blobs reaching 1.5 Mpx — the size of a real gap — and a
bare-chuck frame was reporting one of them as a rim point. Components are therefore filtered on
`MinArea` (2e5 px²) **and** mean grey no more than `MaxMeanFraction` (0.6) of the stage-2 cut.
Measured on the component that supplies the reported point, a real gap runs 0.24–0.52 of the cut and
a trough 0.70–0.81, so the threshold has margin on both sides. It is a *fraction* rather than a grey
level because the cut is relative by design and spans 37–96 across the captures on file. The grey
test is a filter rather than a verdict, so a trough nearer the crosshair than the real gap is dropped
in favour of the gap instead of failing the frame

### Severing the chuck's gashes from the band

The machined chuck carries dark gashes that read below `Cut`, and `CloseRadius` bridges the ones
within ≈42 px into the black band. The result is a single connected region whose chuck-side boundary
grows narrow filaments reaching hundreds of pixels out across the chuck — and those filaments *are*
the rim boundary.

`SeverRadius` (35 px) is an opening applied **after** the closing, removing anything narrower than
70 px. The band is ≈345 px wide and the filaments a few tens, so it separates them; the region area
plateaus from ≈35 upward (813 k at 25, 799 k at 35, 790 k at 45, 786 k at 60) and the reported point
stops moving there.

It must come after the closing, not as a larger `CleanRadius`, for two measured reasons: opening that
wide first leaves dust specks too large for `CloseRadius` to fill, and it lets the bare-chuck frame segment into something that survives `MinArea` (it detects at
radius 25 and 35, saved only by the flank gate).

## 7. Automatic wafer center protocol

| Stage | What it does |
|---|---|
| A | Park at (X min, Y max) — the corner of the stored travel envelope (§2). Refuse up front if the nominal rim radius lies outside the band that line sweeps. |
| B | Raster down in Y, one hop at a time, until the rim is detected. That spot is the station. |
| C | N+1 samples, rotating Θ by 360/N between them, **Θ only** — X/Y move only to re-acquire a lost rim (§6). Each frame is screened for an anomalous rim and dropped if it is one (see *The notch* below). The last sample repeats θ₀. |
| D | De-rotate and fit (`WaferCentreScan`): settle the handedness, drop outliers past `clamp(2.5σ, 0.15 mm, 0.5 mm)`, refit — iterated, re-judging every sample each pass (§7.1). |
| E | Closure check, then persist — including a notch, if one of the dropped frames turned out to be it. |
| F | Drive to `WaferCentreAt(Θ)` for the angle the run ends on, so it finishes on the wafer centre rather than parked out on the rim. |

**Step-and-settle**, exactly as the chuck run: Θ moves, stops, and only then is a frame grabbed, so
the angle and the position paired with each frame are both exact. A soft master cannot synchronise a
continuous multi-axis sweep with an exposure instant.

### The notch, and dropping anomalous samples

These are 200 mm wafers with a **notch**, not a flat: 2.9 mm of arc (1.66°), ≈1 mm deep. One degree
of rim is $R_w\pi/180 = 1.75$ mm, and the frame covers ≈4.9 mm of rim. At the N = 24 sampling used
below (15°, i.e. 26.2 mm of rim between samples) a sample **overlaps** the notch with probability
$(4.9+2.9)/26.2 \approx 30\%$ and contains it whole with probability $(4.9-2.9)/26.2 \approx 8\%$.
Both scale inversely with N: at the default N = 8 (45°, 78.7 mm) they fall to ≈10 % and ≈2.5 %.

An overlapping sample is a rim point that is not on the rim circle, by up to the notch's full ≈1 mm.
Leaving it to §D's outlier drop works, but is second-best: the outlier is *in* the fit that computes
the cut it is then judged against, so it widens the RMS, pulls the centre, and can survive (§7.1
quantifies when). The measurement is better discarded before it becomes a sample, and the run can
recognise it: `NotchDetector.TryCoarse`, the same "is this rim anomalous?" test the notch sweep
applies to every frame, at the same trigger the notch panel sets. It runs **on the frame the point
came from** — a verdict from a second grab would not belong to the point being judged — and only on
frames where the edge detector found something, since an empty frame fails the coarse test on contour
length anyway and costs ≈230 ms to say so.

**If the anomaly is the notch, the run keeps it.** The stage is stopped on it, which is the one
condition `TryMeasure` requires, so a single extra grab settles whether the anomaly is a notch or a
speck. When it measures as a notch the apex is held as a `NotchSighting` (apex px + the crosshair it
was measured against + the motor position + Θ) and converted at stage E, *after* the fit is written: a
chuck-frame bearing is measured from the wafer centre, and until the offset exists there is no wafer
centre to measure from. It is then saved by the fit's own `Save()`.

A whole scan reading as anomalous is a different fault and is reported as one: one notch cannot
account for more than a sample or two, so it means the rim is reading badly or the trigger is too low
for the lighting.

A wafer with a **flat** would be a different matter — a 200 mm primary flat is ≈33° of arc with a
4.2 mm sagitta — and would cost several samples. The run tolerates that by dropping them, but a
4.2 mm sagitta exceeds what the Y search reaches, so the rim would have to be re-acquired on the far
side of the flat rather than tracked across it.

### Parameters

| Constant | Value | Why |
|---|---|---|
| `WAFER_BAND_LO/HI_FRAC` | 0.90 / 1.10 | Acceptance band on $\lVert\mathbf{E}-\mathbf{C}\rVert$, × nominal R. Tightened from 0.70/1.30 on 2026-08-07: ±10 % is ±10 mm on a 200 mm wafer, still ≈3× the 3.2 mm eccentricity measured on hardware, while ±30 % admitted a detection 30 mm off the rim. |
| `WAFER_BORDER_MARGIN_PX` | 8 | Frame-border rejection (§6). |
| `WAFER_SEARCH_HOPS` | 6 | Local search either side of the station along Y on a miss (§6). Not run on an *anomalous* look. |
| `CoarseThresholdMm` | 0.30 mm | Anomaly trigger for the per-frame screen. Taken live from the notch panel's **Trigger (mm)**, so there is one number rather than two. Plain rim reads 0.01–0.05 mm, the notch 0.55 mm. |
| `CoarseMinRunPoints` | 200 | The departure must persist over this many contour points, which is what separates the notch from a speck. |
| `SideProbeRadius` | 50 px | Collar width for the chuck-side choice. Must stay under the bevel's ≈310 px. |
| `MinCollarAreaPx` | 5,000 | Below this a collar piece is a fragment, not a flank. The two largest survivors are the two flanks; the darker is the chuck. |
| `MaxSideContrast` | 0.80 | Darker flank ÷ brighter. Above it the region has the same surface on both sides — a chuck trough, not the rim. Rim gaps 0.46–0.73, troughs 0.89–0.99. |
| `SeverRadius` | 35 px | Opening after the closing; severs the chuck's gashes from the band. Must be well under half the band's ≈345 px and above the filaments' few tens. |
| `WAFER_THETA_SPEED` | 5000 | Θ's cap is 5000 (raised from 3200 on 2026-08-07); a revolution is 359,859 ticks ⇒ ≈72 s of turning. |
| `WAFER_CLOSURE_TOL_STEPS` | 400 | Closure tolerance (≈0.32 mm). |
| `OUTLIER_SIGMA` / `OUTLIER_FLOOR_MM` / `OUTLIER_MAX_MM` | 2.5 / 0.15 mm / 0.5 mm | Outlier cut = `clamp(σ·RMS, floor, max)`. The floor stops a clean scan discarding good points to its own noise; **the ceiling is the operative gate at small N** (§7.1). |
| `OUTLIER_MAX_PASSES` / `OUTLIER_MIN_KEPT` | 3 / 5 | Drop-and-refit iterations, and the point count below which a pass is abandoned rather than fitted. |
| `SIGN_SEPARATION_MM` / `SIGN_MARGIN` | 0.05 mm / 0.5 | When the data may decide the handedness (§5). |

Total run time is dominated by the single revolution, so N barely affects it — N = 24 is not expensive
relative to N = 12. The panel's **Samples** box defaults to **N = 8** (`_waferSamples`,
`FrmVisionProtocols.cs`); the fit needs 3, and the outlier pass (§7.1) has more to work with the
higher N goes.
