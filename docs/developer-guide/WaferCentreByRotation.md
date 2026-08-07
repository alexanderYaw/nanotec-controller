---
title: Wafer Centre-Finding by Rotation
---

# Finding the Wafer Centre by Rotating It

The chuck centre-find drives the stage around the chuck rim, capturing a rim point at several
spots, and Pratt-fits them (**[Chuck Center-Finding Analysis](ChuckCenterFindingAnalysis/)**,
**[Automated Chuck Centre-Finding](ChuckCenterFindingAutomation/)**). The obvious move is to point
the same machinery at the wafer. It does not work, and the reason is geometric rather than
algorithmic.

**This is implemented.** It ships as the *Auto wafer centre-find (Θ scan)* controls in the vision
protocols window; the orchestration is `Vision/FrmVisionProtocols.AutoWafer.cs` and the maths is
`Vision/WaferCentreScan.cs`. There is no manual wafer flow — the old "Add Wafer Edge / Compute
Centre" buttons were removed when this landed, because they could only ever collect the short,
badly-conditioned arc this page exists to avoid.

---

## 1. Why the chuck method cannot be reused

The camera is fixed and the table moves, so every stored geometric quantity is *the motor position
that puts feature X under the crosshair*. To see a rim point you must drive the stage to that
point's position, and the set of positions that view the whole rim is a circle of the feature's own
radius about its centre.

For the chuck's inner circle that radius is about 7,000 steps (≈5.6 mm) — trivially inside the
travel. For a 200 mm wafer it is ≈100 mm, i.e. about 126,000 steps:

| Axis | Travel (steps) | Travel (mm) | Rim circle needs |
|---|---|---|---|
| X | 220,516 | ≈174.8 | ≈200 mm of span |
| Y | 158,624 | ≈126.2 | ≈200 mm of span |

Neither axis has the span. Only a band of the rim is reachable, and a circle fit over one short arc
is badly conditioned: the centre component perpendicular to the arc's chord is essentially
unconstrained, so small edge noise moves the fitted centre a long way along that direction. Two
opposed arcs are better than one but still leave the perpendicular direction weak.

Widening the field of view is not a fix either — at ≈1.24 µm/px the full frame covers about
5.0 × 3.7 mm, which is why the rim is measured a point at a time in the first place.

## 2. The way out: turn the wafer, don't circle it

**The chuck is the Θ axis.** Θ is continuous, unbounded, and carries the wafer. So the rim does not
have to be reached — it can be *delivered*. Park the stage on one reachable spot on the rim and
rotate Θ; every part of the rim passes under the camera in turn. One station plus one revolution
samples the whole circle, entirely inside the reachable band.

This is what commercial wafer pre-aligners do, and the geometry works out cleanly.

### Where the station is: a corner, not a cardinal

"One reachable spot on the rim" is less obvious than it sounds. The first implementation looked for
it along a **cardinal from the chuck centre**, and on this machine there is none — the rim is at
≈100 mm and the best cardinal reaches 88 mm:

| Direction from `C` = (−38, −5) | Headroom | Needed |
|---|---|---|
| +X | 111,603 steps (88.5 mm) | 125,900 steps (99.8 mm) |
| −X | 108,901 (86.3 mm) | ” |
| −Y | 93,107 (74.1 mm) | ” |
| +Y | 65,400 (52.0 mm) | ” |

The run refused to start at all. But a **corner** reaches further than any cardinal: at
(X min, Y max) the stage stands ≈100.8 mm off the rotation axis. More usefully, the whole *line*
X = X min sweeps radii from 86.3 mm (level with `C`) to 113.8 mm (at Y min), so it crosses any rim
whose radius lies in that band — a 200 mm wafer among them, about 1.6 mm below Y max.

So the run parks at (X min, Y max) and rasters **down** in Y until the rim appears. Nothing about
this depends on the rim being at a computed distance: the descent simply looks until it finds an
edge, and the wafer diameter is used only as a pre-flight sanity check (is the rim radius inside
that 86.3–113.8 mm band at all?) and for the acceptance band on each detection. A wafer smaller than
≈172 mm never crosses the line and is refused up front rather than after a fruitless descent.

The line crosses the rim **twice**. The run takes the first crossing it meets. The second, ~100 mm
further down, stands ~23 mm clear of the Y travel end against the first's ~1.6 mm, so it tolerates a
larger eccentricity — worth knowing if a badly-placed wafer ever proves unscannable from the first.

## 3. De-rotation, then the existing fit

All in USER-frame motor steps unless noted. `C` is the stored chuck centre, `A` the pixel→step
affine.

At sample *k*, with chuck angle `θ_k`, motor position `M_k` and detected rim pixel `p_k`:

1. **`E_k = M_k + A·(p_cross − p_k)`** — the rim point, in step space. This is
   `CentreFinder.ToStepPoint`, unchanged from the chuck flow.
2. Take `v_k = E_k − C` and convert to **millimetres** with the per-axis `StepsPerMm`
   (X 1261.5, Y 1256.5). This matters: the two axes differ by 0.4 %, and a rotation is only a
   rotation once that anisotropy is divided out. Skipping it puts a 0.4 % shear on every sample.
3. **De-rotate:** `P_k = R(−σ·θ_k)·v_k`.
4. Circle-fit the `P_k` with the existing Pratt `CircleFit`.

The `P_k` are the rim points expressed in the **chuck's own rotating frame**, where they span the
full 360° even though every one of them was measured from the same small patch of reachable travel.
The fitted centre is the wafer's offset from the rotation axis; the fitted radius is the wafer
radius.

Step 4 is the point of the whole construction: a partial-arc problem has become a full-circle
problem, and the fit that was already trusted for the chuck is reused verbatim.

### Why the chuck angle must come from `ChuckTicksToDegrees`

Θ turns the chuck through a ≈9:1 reduction. `CrosshairRotation.ChuckTicksPerRev` (359,859) is the
only correct divisor; the motor's 40,000 ticks/rev wraps nine times per chuck revolution and would
make every de-rotation angle wrong by a different amount.

The angle is read fresh through `IMotionHost.TryReadThetaNow`, not from the polled cache — the
cache still holds the pre-rotation angle for at least one poll period after each move, for the same
reason `TryReadUserXyNow` exists.

## 4. An error in the chuck centre does not bias the answer

The stored `C` comes from fitting the chuck's machined inner circle, which is not guaranteed to be
the true rotation axis. Suppose it is wrong by `δ`. The rim point seen at each angle lies in the
fixed lab direction `n̂` from the centre, so in the chuck frame its direction is `R(−θ)n̂`, and:

```
P_k = R(−θ_k)(E_k − C_true − δ)
    = W_true + R(−θ_k)·(R_w·n̂ − δ)
```

As `θ_k` sweeps a revolution, `R(−θ_k)` sweeps all rotations, so the `P_k` still lie **exactly** on
a circle — centred on `W_true`, with radius `|R_w·n̂ − δ|`.

So a chuck-centre error is absorbed **entirely by the radius** and does not bias the measured
offset at all. Two consequences worth stating plainly:

* The eccentricity the scan reports is relative to the **true rotation axis**, regardless of how
  good `C` is. That is the quantity that matters, because it is what rotation actually does to the
  wafer.
* The *absolute* lab position still inherits `δ`, because turning the offset back into a motor
  position adds the stored `C` in again. That error was already present in everything else built on
  `C` and is not made worse here.
* A fitted radius that disagrees with the operator's nominal wafer diameter is therefore a **free
  diagnostic on the chuck centre**, not just on the wafer.

This is verified offline: see §8.

## 5. The handedness σ

`CalibrationStore.RotationSign` is the image handedness of a positive Θ move, and
`CrosshairRotation` applies it in **pixel** space. The de-rotation here happens in step/mm space,
so mapping one to the other costs the sign of the affine's determinant:

```
σ_expected = RotationSign · sign(det A)
```

With the current affine `det A = +2.46`, so σ is just `RotationSign` (−1 on this machine).

That expectation is not trusted on its own — `RotationSign` is null until the sign test has ever
been run, and a wrong σ produces a *mirrored but perfectly plausible* centre. So `WaferCentreScan`
fits **both** signs and:

* if one is clearly better (the loser's RMS is above `SIGN_SEPARATION_MM` = 0.05 mm **and** the
  winner beats it by 2×), the **data decides**, and a disagreement with `σ_expected` is logged as a
  warning that one of the two is wrong;
* otherwise the scan cannot tell them apart and `σ_expected` breaks the tie;
* if there is no expectation either, the fit **fails** rather than guessing.

The tie case is real, not hypothetical: **any 3 points lie exactly on some circle**, so at N = 3
both handednesses fit perfectly and an RMS comparison decides on floating-point noise. That was
caught by the offline check in §8 and is the reason the margin and floor exist.

## 6. Keeping the rim inside a 5 mm field of view

With X/Y parked and Θ turning, the rim sweeps **radially** past the camera by ±e (the
eccentricity), and the nearest-rim-point also wanders tangentially by roughly e. A hand-placed
wafer can easily exceed the frame in both.

The scan holds X and Y still and moves them **only when a sample misses**, searching along Y about
the station — ±1, ±2 … up to `WAFER_SEARCH_HOPS` hops, down first — and moving the station to
wherever it re-acquired. If the search comes up empty the station is left where it was (so one lost
sample cannot strand the run away from the rim) and the angle is **skipped**. Missed samples must
never abort a run.

### The search must go both ways

Down-only was tried first, and fails for half of every revolution. At the station the outward radial
direction from the rotation axis is **+Y**, so:

* rim swings **outward** — the camera is now *inside* the wafer, and recovery is +Y. A downward
  search walks the wafer's whole ~100 mm interior before re-finding the rim at the opposite crossing.
* rim swings **inward** — the camera is *outside*, and recovery is −Y, one or two hops.

Both happen, once each per revolution, which is exactly the reported symptom: sometimes moving down
brought the edge back, sometimes it drove it further out of view.

Following a radial swing of `e` costs `e / |r̂·ŷ|` of Y travel, and `|r̂·ŷ| ≈ 0.5` at the first
crossing, so the search covers roughly `±WAFER_SEARCH_HOPS × hop / 2` of eccentricity — ±4.5 mm at
6 hops. It is deliberately **bounded**: skipping a sample is cheap, and an unbounded search would
turn one lost sample into a 100 mm traverse.

That the rim leaves the frame at all also puts a floor on the eccentricity in play. The field is
≈3.8 mm across, so a wafer within ~1 mm of the rotation axis would never lose it; the searching seen
on hardware means at least ~2 mm.

*(Superseded: the original design moved the station onto each rim point `E_k`, which keeps the rim
centred continuously. It was dropped for plain Θ-only sampling — the wafer scan is a measurement of
where the rim is, and the fewer X/Y moves paired with it, the fewer places for a move to go wrong.)*

### Which edge the detector is actually looking at

The obvious segmentation — threshold the bright wafer, take its boundary — **finds the bevel, not
the rim.** In a 4016 × 3024 frame across the rim (`images/capture_20260803_175836_162.bmp`), the
grey levels along the row through the crosshair run:

| Columns | Grey | What |
|---|---|---|
| 0–1310 | 255, dipping dark on dies | wafer surface |
| 1360–1670 | 136–148 | **bevel** |
| 1690–2540 | 255 | wafer, outside the bevel |
| 2555–2890 | 10–27 | the unlit gap beyond the rim |
| 2900–4015 | 60–115 | textured background |

Otsu puts the cut at 163, so the bevel reads *dark* and splits the wafer into two blobs. Two
separate things then go wrong, and fixing either alone is not enough:

* the larger blob is the inner one, so `max_area` selects the wrong side of the bevel entirely;
* even on the correct blob, its **nearest** boundary to the crosshair is still the bevel (348 px)
  rather than the rim (537 px).

So no rule of the form "brightest blob, nearest boundary" can work here. Nor can closing the gap:
the bevel is ~310 px wide and the dark gap beyond the rim is ~345 px, so any closing radius that
bridges the bevel also bridges the rim.

**The detector therefore segments the off-wafer side instead.** The bevel is mid-grey; only the
world beyond the wafer is black. The cut is **two-stage Otsu** — the first split isolates the lit
wafer, the second is taken *inside the darker part alone*, which is what separates sustained black
from mid-grey bevel and background texture. Both stages track exposure, as the single stage did.

This also disposes of the frame-border artefact that used to need guarding against. When the wafer
covers the entire view there is no large dark region at all, so the detector returns **false**
rather than confidently reporting the frame border. It additionally drops boundary points lying on
the frame itself, which is what a frame taken wholly *off* the wafer would otherwise produce. The
scan's own gates remain as a second line: a detection within `WAFER_BORDER_MARGIN_PX` (8 px) of a
frame edge is refused, and so is one whose radius falls outside `[0.70, 1.30] × nominal`.

The cost is a new dependency: the run needs the gap beyond the rim to actually be dark. It is —
that gap is geometric, the wafer stands proud of the chuck under oblique light — and it is present
in every rim frame captured so far, six weeks apart. If a scan ever misses at every angle, this is
the first thing to check.

**Dark is not enough on its own, though.** Otsu always returns a cut, so a frame with no rim in it
still segments into something, and the chuck's own machined surface is the thing that gets
segmented. Under oblique light its shadow troughs form long dark-ish blobs reaching 1.5 Mpx — the
size of a real gap — and a bare-chuck frame was reporting one of them as a rim point. Components
are therefore filtered on **two** criteria: `MinArea` (2e5 px²) and mean grey no more than
`MaxMeanFraction` (0.6) of the stage-2 cut. Measured on the component that supplies the reported
point, a real gap runs 0.24–0.52 of the cut and a trough 0.70–0.81, so the threshold sits in clear
air; it is a *fraction* rather than a grey level because the cut is relative by design and spans
37–96 across the captures on file. The grey test is a filter rather than a verdict, so a trough
sitting nearer the crosshair than the real gap is dropped in favour of the gap instead of failing
the frame outright.

### The gap has two boundaries, and only one is measured

Segmenting the gap correctly still leaves a choice. Reading the table above outward, the gap runs
cols 2555–2890: its **inner** boundary (≈2545) is the gap against the wafer's bevel, its **outer**
one (≈2900) is the gap against the chuck.

**The outer, chuck-side boundary is the one measured.** The chuck surface is in focus, so that
boundary is the sharper and more repeatable of the two, where the bevel side is a specular gradient
whose apparent position moves with the illumination. This is a deliberate change from the first
implementation, which took the bevel side.

Either choice is only a constant radial offset from the true rim — a circle fitted to one is
concentric with a circle fitted to the other — so the recovered **centre** is unaffected and only
the fitted **radius** shifts, by the gap's width. What is not acceptable is choosing
*inconsistently* between samples, which is exactly what the original nearest-to-the-crosshair rule
did: it picks the side the crosshair happens to sit on. Nothing downstream catches that, either —
the two boundaries are a third of a millimetre apart, far inside the `[0.70, 1.30] × nominal` band,
so the error passes every gate and biases the fit sample by sample. A single-pass RMS cannot see a
bias that is coherent across samples.

**Brightness chooses the side.** The bevel is specular and throws a near-saturated glint that hugs
the gap; the chuck is diffuse and mid-grey. The detector takes a collar of `SideProbeRadius` (50 px)
either side of the gap, measures each one's **mean grey**, and keeps the darker. The boundary is
then the gap's own ring restricted to that side.

**Two candidates, compared — not a threshold.** `connection` on the collar does not hand back two
pieces. A ragged gap outline shatters it into six to thirteen, and a sliver *on the bevel side*
reads darker than the bevel proper, so any cut-off against the brightest piece admits fragments
from both sides at once and the rim ring ends up straddling the gap. That was the shipped behaviour
until 2026-08-05, and it is what a live Θ scan surfaced as noise and false edges while the tuning
script — pointed at one of the frames where the collar happens to come through cleanly — looked
correct. On `capture_20260804_114358_183.bmp` the 0.85-of-brightest rule kept **four of six**:

| piece | area px² | mean | old verdict | now |
|---|---|---|---|---|
| 1 | 233,336 | 169.9 | drop | drop — bevel side |
| 2 | 343,944 | 124.7 | KEEP | **KEEP — chuck side** |
| 3 | 10,689 | 157.8 | drop | drop — fragment |
| 4 | 11,592 | 136.6 | KEEP | drop — fragment |
| 5 | 8,238 | 127.3 | KEEP | drop — fragment |
| 6 | 18,375 | 125.9 | KEEP | drop — fragment |

The gap has exactly two sides, so the fix is to compare only the **two largest** pieces and take the
darker. Two candidates need no cut-off, which removes the `SideDarkFraction` knob altogether, and
slivers — whose grey means nothing — enter neither the comparison nor the result. Across every
current-optics capture on file this collapses the returned contour to a single piece and leaves the
reported point unchanged wherever it was already right.

Measured over the captures on file, the chuck collar runs **0.4–0.8×** the wafer collar's mean, at
every collar width tried (30, 50, 60 and 120 px), and carries ~1 % saturated pixels against 14–80 %
on the wafer side.

**Texture cannot choose the side**, counter-intuitive as that is when the chuck is the surface that
visibly looks textured. The glint is a saturated ridge speckled with dark pits, so on the same
captures the *wafer* collar is the rougher of the two — grey deviation 52–74 against the chuck's
41–49, and mean local gradient 2–3× higher at a 30 px collar. Narrowing the collar does not reverse
this. Worse, the polarity is not even stable: captures taken before the chuck was brought into
focus show it the other way round, which on its own disqualifies texture as the discriminator.

Three details matter:

* the two collars stay separate because the gap crosses the frame, so the collar cannot wrap round
  its ends — they *are* the image border;
* `SideProbeRadius` must stay well under the bevel's ~310 px, or the wafer-side collar reaches past
  the glint into the darker wafer surface and the brightness contrast collapses;
* **fewer than two flanks drops the region.** A gap that comes through as a blob gives one ring, and
  a gap running off a corner can leave the wafer side out of view entirely; either way there is no
  evidence at all about which boundary faces the chuck. A dropped sample costs the scan one search
  hop, whereas a boundary taken off the wrong side biases the fit by the gap's width and passes
  every downstream gate. This replaces an earlier "fall back to the darkest" rule, which on
  `capture_20260731_160143_116.bmp` kept a collar of mean 205 — the *bright* side — and called it
  the chuck.

### Severing the chuck's gashes from the band

The machined chuck carries dark gashes that read below `Cut`, and `CloseRadius` bridges the ones
within ~42 px into the black band. The result is a single connected region whose chuck-side boundary
grows dendritic tendrils reaching hundreds of pixels out across the chuck — and those tendrils *are*
the rim boundary as far as everything downstream is concerned. On
`capture_20260805_111803_054.bmp` the region measured 997,572 px and the reported point landed
375 px from the crosshair, out in the chuck, while the real band lay 1,240 px away. Every existing
filter passed it: the region is one gap plus its tendrils, so its area, its mean (29.3) and its flank
contrast (0.53) are all those of a genuine rim.

`SeverRadius` (35 px) is an opening applied **after** the closing, removing anything narrower than
70 px. The band is ~345 px wide and the tendrils a few tens, so it cuts cleanly between them; the
region area plateaus from ~35 upward (813 k at 25, 799 k at 35, 790 k at 45, 786 k at 60) and the
reported point stops moving there.

It must come after the closing, not as a larger `CleanRadius`, for two measured reasons: opening that
wide first leaves dust specks too large for `CloseRadius` to fill, and it lets the bare-chuck frame
`capture_20260804_114724_720.bmp` segment into something that survives `MinArea` (it detects at
radius 25 and 35, saved only by the flank gate). Applied after the closing, all three rejections and
all rim detections on file are preserved.

A useful side effect: the opening also smooths the ragged outline that was fragmenting the collar, so
the flank count drops from 3–6 to 2 on most frames.

### Flank contrast: the filter for a crosshair over the chuck

Reported symptom (2026-08-05): *"accurate when the crosshair is in the white area — when it is on
the darker textured chuck it frequently picks random edges on the chuck surface."*

The reported point is the boundary point **nearest the crosshair**. With the crosshair over the
wafer the rim is the nearest dark boundary and everything works; with it over the chuck the rim can
be two thousand pixels away, so any surviving trough near the centre wins outright. Neither existing
gate objects — a trough reaches 1.5 Mpx so `MinArea` passes it, and `AcceptDetection`'s
`[0.70, 1.30] × nominal` radial band passes it too, because a trough beside the crosshair sits at
very nearly the station's own radius. `MaxMeanFraction` catches most troughs but not all: it is a
fraction of `Cut`, and `Cut` drifts *up* when little black gap is in view, which is exactly the
mostly-chuck framing where the problem appears.

What separates them structurally is that a rim gap has the **wafer on one flank** and a trough has
chuck on both. Measured over every capture on file:

| | darker flank ÷ brighter flank |
|---|---|
| rim gap (×8) | 0.46, 0.49, 0.49, 0.52, 0.53, 0.56, 0.59, 0.73 |
| chuck shadow trough (×6) | 0.89, 0.90, 0.93, 0.97, 0.98, 0.99 |

`MaxSideContrast = 0.80` sits in the clear air between. Applied **per region**, before the regions
are merged — merging first and testing the merger would let a trough contribute boundary that then
wins the nearest-point search.

Old-optics captures (chuck defocused) measure 0.84 on a *real* gap and are therefore refused by this
gate; they are not a target, but it does mean the margin depends on the chuck staying in focus. The
per-detection line now written to the wafer scan log (`cut=… parts=… big=… dark=… | r1 a=… m=…
flanks=…/… c=… KEEP`) is what to read if that changes.

The chosen side is grown back by only 2 px to meet the gap's own boundary ring. A large value would
reach across a narrow gap and re-admit the far side — the very thing this step exists to remove.

One consequence to expect in the logs: the **fitted radius now exceeds `Ø/2 × StepsPerMm`** by the
gap's width, where before it fell short by the bevel's. That figure is still the standing check on
the side choice — what matters is that it is consistently offset in the same direction, not that it
matches the nominal.

## 7. Shape of a run

| Stage | What it does |
|---|---|
| A | Park at (X min, Y max) — the corner of the stored travel envelope (§2). Refuse up front if the nominal rim radius lies outside the band that line sweeps. |
| B | Raster down in Y, one hop at a time, until the rim is detected. That spot is the station. |
| C | N+1 samples, rotating Θ by 360/N between them, **Θ only** — X/Y move only to re-acquire a lost rim (§6). Each frame is screened for an anomalous rim and dropped if it is one (see *The notch* below). The last sample repeats θ₀. |
| D | De-rotate and fit (`WaferCentreScan`): settle the handedness, drop outliers past `clamp(2.5σ, 0.15 mm, 0.5 mm)`, refit — iterated, re-judging every sample each pass (§7.1). |
| E | Closure check, then persist — including a notch, if one of the dropped frames turned out to be it. |
| F | Drive to `WaferCentreAt(Θ)` for the angle the run ends on, so it finishes on the wafer centre rather than parked out on the rim. |

Stage F runs **after** the save, so a move that fails costs the position, not the measurement — the
same ordering as the chuck run's own return-to-centre. It is skipped if the closure check failed
(nothing was saved), if the operator cancelled, or if the target is outside the travel envelope.

**Step-and-settle**, exactly as the chuck run: Θ moves, stops, and only then is a frame grabbed, so
the angle and the position paired with each frame are both exact. A soft master cannot synchronise
a continuous multi-axis sweep with an exposure instant.

Θ is stepped **monotonically in one direction** through a single revolution, so backlash in the
≈9:1 reduction loads identically at every sample.

### The closure check

The last sample returns to θ₀, and its radius must reproduce the first sample's within
`WAFER_CLOSURE_TOL_STEPS` (400). If it does not, the wafer moved on the chuck — vacuum off, or Θ
lost steps — and **every earlier sample is suspect**, so the result is reported but *not saved*.
This is nearly free: a full revolution returns to the start anyway, so it costs one extra grab.

### The notch, and dropping anomalous samples

These are 200 mm wafers with a **notch**, not a flat: 2.9 mm of arc (1.66°), ~1 mm deep. The frame
covers ~4.9 mm of rim, so at 15° sampling (26.2 mm of rim between samples) a sample **overlaps** the
notch about **30%** of runs and contains it whole about **8%**.

An overlapping sample is a rim point that is not on the rim circle, by up to the notch's full ~1 mm.
Leaving it to §D's outlier drop works, but it is second-best: the outlier is *in* the fit that
computes the cut it is then judged against, so it widens the RMS, pulls the centre, and can survive
(§7.1 quantifies exactly when). The measurement is better thrown away before it
becomes a sample, and the run has the means to recognise it: `NotchDetector.TryCoarse`, the same
"is this rim anomalous?" test the notch sweep applies to every frame it passes, at the same trigger
the notch panel sets. It runs **on the frame the point came from** — a verdict from a second grab
would not belong to the point being judged — and only on frames where the edge detector found
something, since an empty frame fails the coarse test on contour length anyway and costs ~230 ms to
say so.

**Anomalous is not the same as missed**, and `RimLook` keeps them apart. A miss sends the station
hunting ±6 hops of Y for a rim that has drifted out of the field (§6); an anomaly means the rim is
*right there*, so there is nothing to hunt for. Searching would walk ±9 mm of Y over a feature 3 mm
wide and find the same anomaly at the far end of it. So an anomalous look ends the sample
immediately — dropped, not skipped — and the station still follows the Y it was seen at, because a
frame with a rim in it is still a good place to stand.

**If the anomaly is the notch, the run keeps it.** The stage is stopped on it, which is the one
condition `TryMeasure` wants, so a single extra grab settles whether the anomaly is a notch or a
speck. When it measures as a notch the apex is held as a `NotchSighting` (apex px + the crosshair it
was measured against + the motor position + Θ) and converted at stage E, *after* the fit is written:
a chuck-frame bearing is measured from the wafer centre, and until the offset exists there is no
wafer centre to measure from. It is then saved by the fit's own `Save()`.

This is a **catch, not a search**. The fine detector needs the notch fully enclosed *and* clear of
both end anchors, which holds over 0.32–1.16° of Θ against 15° of sample spacing — so a scan catches
one somewhere between 1 run in 13 and 1 in 45. When it does, the notch search need not run at all
for that wafer; when it does not, the
sample was still correctly dropped and the log says whether the anomaly was a notch, debris, or a
chipped edge. Finding the notch deliberately remains a separate run: see
**[Notch Search by Continuous Sweep](NotchSearch/)**.

A whole scan reading as anomalous is a different fault and is reported as one: one notch cannot
account for more than a sample or two, so it means the rim is reading badly or the trigger is too
low for the lighting.

A wafer with a **flat** would be a different matter (a 200 mm primary flat is ~33° of arc with a
4.2 mm sagitta) and would cost several samples; the run tolerates that by dropping them, but a
4.2 mm sagitta is past what the Y search reaches, so the rim would have to be re-acquired on the far
side of the flat rather than tracked across it.

### Parameters

| Constant | Value | Why |
|---|---|---|
| `WAFER_BAND_LO/HI_FRAC` | 0.90 / 1.10 | Acceptance band on `\|E − C\|`, × nominal R. Tightened from 0.70/1.30 on 2026-08-07: ±10 % is ±10 mm on a 200 mm wafer, still ~14× the 0.72 mm eccentricity measured on hardware, while ±30 % admitted a detection 30 mm off the rim. |
| `WAFER_BORDER_MARGIN_PX` | 8 | Frame-border rejection (§6). |
| `WAFER_SEARCH_HOPS` | 6 | Local search either side of the station along Y on a miss (§6). Not run on an *anomalous* look. |
| `CoarseThresholdMm` | 0.30 mm | Anomaly trigger for the per-frame screen. Taken live from the notch panel's **Trigger (mm)**, so there is one number rather than two. Plain rim reads 0.01–0.05 mm, the notch 0.55 mm. |
| `CoarseMinRunPoints` | 200 | The departure must persist over this many contour points, which is what separates the notch from a speck. |
| `SideProbeRadius` | 50 px | Collar width for the chuck-side choice. Must stay under the bevel's ~310 px. |
| `MinCollarAreaPx` | 5,000 | Below this a collar piece is a fragment, not a flank. The two largest survivors are the two flanks; the darker is the chuck. |
| `MaxSideContrast` | 0.80 | Darker flank ÷ brighter. Above it the region has the same surface on both sides — a chuck trough, not the rim. Rim gaps 0.46–0.73, troughs 0.89–0.99. |
| `SeverRadius` | 35 px | Opening after the closing; cuts the chuck's gashes off the band. Must be well under half the band's ~345 px and above the tendrils' few tens. |
| `WAFER_THETA_SPEED` | 5000 | Θ's cap is 5000 (raised from 3200 on 2026-08-07); a revolution is 359,859 ticks ⇒ ~72 s of turning. |
| `WAFER_CLOSURE_TOL_STEPS` | 400 | Closure tolerance (≈0.32 mm). |
| `OUTLIER_SIGMA` / `OUTLIER_FLOOR_MM` / `OUTLIER_MAX_MM` | 2.5 / 0.15 mm / 0.5 mm | Outlier cut = `clamp(σ·RMS, floor, max)`. The floor stops a clean scan shedding good points to its own noise; **the ceiling is what does the work at small N** (see below). |
| `OUTLIER_MAX_PASSES` / `OUTLIER_MIN_KEPT` | 3 / 5 | Drop-and-refit iterations, and the point count below which a pass is abandoned rather than fitted. |
| `SIGN_SEPARATION_MM` / `SIGN_MARGIN` | 0.05 mm / 0.5 | When the data may decide the handedness (§5). |

Total run time is dominated by the single revolution, so N barely affects it — N = 24 is not
expensive relative to N = 12. The panel's **Samples** box defaults to **N = 8** (`_waferSamples`,
`FrmVisionProtocols.cs`); the fit needs 3, and the outlier pass (§7.1) has more to work with the
higher N goes.

### 7.1 The outlier pass, and why a sigma multiple alone does not work

Reworked 2026-08-07 after the old pass was observed accepting outliers. Three things were wrong, and
only the third actually mattered:

1. **The cut was loose** — `3σ` with a `0.3 mm` floor, against hardware scans that fit to an RMS of
   **0.076 mm**. Anything within 4× the noise was kept unconditionally. Now `2.5σ` with a `0.15 mm`
   floor (~2× the noise).
2. **It ran once.** A gross outlier inflates the RMS that sets the cut, so the second-worst point can
   hide inside a threshold the worst one widened. Now iterated up to `OUTLIER_MAX_PASSES`, each refit
   tightening the next cut.
3. **A multiple of the RMS cannot catch an outlier at small N at all** — the failure the other two
   miss. With 9 points, one bad sample *drags the circle onto itself*: its own residual shrinks while
   every other residual grows, so it ends up comfortably inside its own cut no matter what σ is. This
   is why `OUTLIER_MAX_MM = 0.5 mm` exists — an absolute ceiling, justified physically rather than
   statistically (6× the measured RMS; nothing genuine reaches it). **On short scans the ceiling is
   the gate; the sigma term only takes over once N is large.**

Measured offline against synthetic scans with known ground truth (`err` = distance from the true
offset; the harness is the standalone-console-compiling-the-source pattern):

| Case | Old: dropped / err | New: dropped / err |
|---|---|---|
| N=8, clean | 0 / 0.025 mm | 0 / 0.025 mm |
| N=8, one 2.0 mm outlier | **0 / 0.487 mm** | 1 / 0.029 mm |
| N=8, 2.0 + 1.2 mm | **0 / 0.367 mm** | 2 / 0.005 mm |
| N=24, clean | 0 / 0.005 mm | 0 / 0.005 mm |
| N=24, three outliers | **1 / 0.203 mm** | 3 / 0.017 mm |
| N=24, one 0.45 mm | 1 / 0.010 mm | 1 / 0.010 mm |

Each pass re-judges **every** sample, not just the survivors: the first fit is pulled towards a gross
outlier, so good points on the far side can fall outside that pass's cut and must be readmitted once
the refit is clean. Without readmission the same 1-outlier case at N=8 dropped **three** points.

**Two limits worth knowing.** A sub-half-mm outlier at N=8 still survives (0.45 mm injected → kept,
0.102 mm of error) because 9 points absorb it; at N=24 the same point is caught. And if a pass would
leave fewer than `OUTLIER_MIN_KEPT` points, it is abandoned whole and **nothing is dropped** — with 4
bad samples out of 9 the run keeps the distorted fit and reports it, RMS 0.855 mm against a typical
0.026 mm. That is visible in the result panel rather than silent, but it is not rejected: there is no
RMS gate on saving.

## 8. What is stored, and why it is not a point

**The wafer centre is not a fixed motor position.** The wafer sits eccentric on the chuck, so its
centre *orbits* the rotation axis as Θ turns — between Θ and Θ+180° it moves by `2e`. A single
stored `WaferCenterX/Y` is therefore only valid at one, unrecorded, angle.

So the scan stores the invariant instead:

| Field | Meaning |
|---|---|
| `WaferOffsetX/Y` | Wafer centre relative to `C`, in the **chuck's rotating frame** (de-rotated to θ = 0). |
| `WaferRadius` | Fitted radius, steps. |
| `WaferFitSign` | The handedness settled on — needed to rotate the offset back out. |
| `WaferFitRms`, `WaferFitN`, `WaferFitTimestamp` | Fit quality, for the same reason `PixelStepAffine` carries its own. |
| `WaferCenterX/Y` | A **snapshot** at the angle the run ended on, kept so a plain read still gets a usable answer. |

`CalibrationStore.WaferCentreAt(chuckAngleDeg)` rotates the offset back out to a motor position for
any Θ, and is what both **Go to Centre** buttons use.

A scan that caught the notch also writes `NotchAngleDeg`, `NotchDepthMm` and `NotchTimestamp` — the
same three fields the notch search writes, with the same meaning, in the same `Save()`. A scan that
did not leaves any stored angle alone and says so in the log: that angle belongs to whatever wafer
was on the chuck when it was measured, and only the operator knows whether that is still this one.

## 9. Verification

The maths is checked **offline**, before any hardware, by driving `WaferCentreScan` with synthetic
scans (known `C`, known offset, known radius, generated `E_k`). That check covers:

* exact recovery of the offset, radius and handedness on an ideal 24-sample scan;
* the data overriding a deliberately wrong `expectedSign`;
* **the §4 result** — a deliberate `δ` added to `C` leaves the offset untouched and moves the
  radius to exactly `|R_w·n̂ − δ|`;
* a corrupted sample being dropped with the offset unaffected;
* N = 3 resolving via the expectation, and **failing** when there is none;
* `WaferCentreAt` round-tripping, and swinging by exactly `2e` between 0° and 180°.

That last `WaferCentreAt` property is also the **definitive on-hardware test**, and it is the
rotational-invariance check that
**[Chuck Center-Finding Analysis](ChuckCenterFindingAnalysis/)** §6 describes as not implemented —
it validates the *physical* result rather than the fit's self-consistency:

> Drive to `WaferCentreAt(current Θ)`. Rotate Θ by 180°. Drive to `WaferCentreAt(new Θ)`. The same
> point on the wafer must still be under the crosshair. If the eccentricity is wrong, the wafer
> centre visibly swings by `2e`.

A single-pass fit's own RMS cannot catch a systematic bias — a skewed affine, a wrong handedness,
a mis-scaled `StepsPerMm`, or the detector settling on the wrong side of the gap — because those
move every point coherently. The rotation test can.

The **fitted radius against `Ø/2 × StepsPerMm`** is the cheap standing check on the same class of
error, and specifically on the side choice. The chuck-side boundary is the one measured, so the
radius should sit *above* the nominal by the gap's width (~0.35 mm) and stay there. A radius that
comes out near or below the nominal means the detector has slipped onto the bevel side on some
samples — which leaves the RMS looking healthy while biasing the fit.
