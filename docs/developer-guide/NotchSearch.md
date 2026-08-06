---
title: Notch Search by Continuous Sweep
---

# Finding the Notch

The wafer centre-find turns the wafer past a fixed camera and fits a circle to the rim. Finding the
**notch** reuses that same station, but asks a different question — *where* on the rim is the one
place that is not a circle — and the answer has to come from a single revolution rather than 25
samples of one.

**This is implemented.** It ships as the *Notch find (Θ sweep)* controls in the vision protocols
window: orchestration in `Vision/FrmVisionProtocols.NotchFind.cs`, detection in
`Vision/NotchDetector.cs`, motion in `FrmMain.RimSweep.cs`, geometry in `Geometry/RimStation.cs`.
The tuning mirror is `Halcon/notch detector.hdev`.

> **Not verified on hardware.** Everything below about the vision is measured, on the captures in
> `Desktop/images`. Everything about the *motion* — the follower, the blur, the frame pacing — is
> arithmetic and has never been run. See §7.

**It may already be done.** The wafer centre-find screens every sample with the coarse detector and
drops an anomalous one from the fit; when the anomaly happens to be the notch — fully in frame and
clear of the chord's end anchors, which is worth roughly 1 scan in 13 to 1 in 45 — it measures it on
the spot and saves the same `NotchAngleDeg` / `NotchDepthMm` / `NotchTimestamp` this run writes. So
check the wafer scan's log and the **Rotate to datum** button before spending a revolution here. See
**[Finding the Wafer Centre by Rotating It](WaferCentreByRotation/)** §7.

---

## 1. The cost is Θ's speed cap, not the vision

A 200 mm wafer's rim is 628 mm long and the camera sees ~4.9 mm of it. That sounds like a hopeless
search until the actual numbers are put in:

| | |
|---|---|
| Revolution | 359,859 Θ ticks (`CrosshairRotation.ChuckTicksPerRev`) |
| Θ velocity cap | 3200 steps/s (`Drive/MotionTypes.cs`, `JogVelocityMax`) |
| **⇒ one revolution** | **112 s of rotation, before a single frame is grabbed** |
| Rim speed at that cap | 5.60 mm/s |

So the search takes ~112 s worst case and ~56 s expected **no matter how the frames are taken**.
Everything in the design exists to add as little as possible on top of that floor.

That is also why the sweep is **continuous** rather than step-and-settle. At a 4 mm capture pitch a
stepped scan needs ~157 stops; at ~1.5 s each that is ~240 s *added* to the 112 s. Sweeping
continuously adds nothing, because the detector (§3) finishes in ~130 ms against a 710 ms budget.

> **The one lever.** `JogVelocityMax = 3200` is a **host-side constant**, not a limit read from the
> drive. If Θ's motor and gearbox tolerate more, the whole search scales down linearly. Nothing else
> comes close. Whether it is safe to raise is a hardware question.

---

## 2. A frame only has to *overlap* the notch

The reason the pitch can be coarse: the notch is **2.9 mm** wide at the rim and the frame covers
**4.9 mm** of it, so a frame does not have to *contain* the notch — it only has to catch enough of
one flank to bend the rim contour. The detection window is therefore about

```
4.9 mm (frame) + 2.3 mm (the part of the notch deeper than the threshold) − 2×0.3 mm ≈ 7 mm
```

and a **4 mm pitch** is roughly twice as fine as it needs to be. Requiring the notch to be *whole*
in frame instead would force a ~1.4 mm pitch and ~450 stops — the difference between a workable
search and an unworkable one.

---

## 3. Two detectors, and why they are not interchangeable

`NotchDetector` has two modes that differ only in their **baseline**.

**Coarse** (`TryCoarse`) fits a plain regression line to the whole rim contour and reports the
greatest perpendicular departure. It needs no clean rim anywhere, so it still fires when the notch
is half in view.

**Fine** (`TryMeasure`) anchors a chord on the first and last 300 contour points and measures
everything as a signed offset from that. Far more accurate, but it demands plain rim at *both* ends
and refuses a partial notch outright.

The sweep must use the coarse test, because **a partially-visible notch is exactly what a sweep
meets first** — the fine test would reject that frame and the search would step straight over it.
The fine test then runs once, on a stationary frame, after the sweep has re-centred the notch.

Measured over the twelve captures on file (full 4016×3024):

| | plain rim | notch frame |
|---|---|---|
| coarse residual | 0.012 – 0.046 mm | **0.548 mm** |
| coarse run over 0.30 mm | **0 points** | **1143 points** |
| fine depth | 0.006 – 0.032 mm | **1.002 mm** |

### Deep AND wide — one test is not enough

The coarse test asks two questions, and the second is what tells a notch from dirt. A peak residual
asks how **far** the contour departs from straight; it never asks how **long** it departs for. Debris
on the rim, a spur off the skeleton, or a ring bridged across a break by
`union_adjacent_contours_xld` is a *narrow spike* that can reach any height at all — so no threshold
on the peak alone separates them. It only trades one error for the other: raise it enough to reject
debris and you also blind the sweep to a real notch seen at an unlucky angle.

Requiring the departure to **persist over 200 contiguous contour points** separates them on shape
instead. The notch runs **1143** points at a 0.30 mm cut; every plain-rim frame runs **0**. That is a
cleaner separation than either test gives on its own, and it is the one that matches the physics —
a notch is 2.9 mm of rim, debris is a speck.

The threshold itself sits at **0.30 mm**: 7.5× the worst plain rim (0.040) and still 1.8× under the
notch (0.541). It does **not** need to catch a barely-visible notch, which is what an earlier 0.12 mm
was for. The sweep grabs a frame every ~0.67 mm of rim (5.6 mm/s ÷ ~120 ms) and the notch sits wholly
inside the 4.9 mm field over 2.0 mm of travel, so **~3 frames per pass see all of it**. Sensitivity
to a sliver bought nothing and cost the margin against debris.

Both are exposed: the threshold as **Trigger (mm)** in the protocols window (the right value depends
on how much the sweep blurs the rim, which cannot be known off-hardware), the run length as
`CoarseMinRunPoints`.

> Run length degrades gracefully, so the threshold has room to move: the notch still runs 1002 points
> at 0.35 mm, 852 at 0.40 and 415 at 0.50.

The fine depth landing on **1.002 mm** against the SEMI nominal of **1.00 mm** is the strongest
evidence available that the whole chain — `PixelStep` → µm/px → contour → chord → depth — is right
end to end. Nothing in the detector was fitted to that number.

### Do not downscale

The obvious optimisation is to run the coarse test on a quarter-size frame. **It does not work.**
Measured at 1/4 with every radius and area constant rescaled with the image:

| | notch | plain rim |
|---|---|---|
| 1/4 resolution | 0.275 mm | 0.217 mm |

A 30× separation collapses to 1.27×. The opening and closing radii fall to 2 and 5 px, too small to
hold the gap's outline together, so the ring goes ragged and the residual measures raggedness rather
than shape — several frames stop segmenting at all. It is also unnecessary: the coarse path costs
**~130 ms at full resolution** (measured through the C# detector; 230 ms through HDevEngine) against
a 710 ms budget.

### Which boundary, and why it differs from the edge detector

The unlit gap past the rim has two boundaries. `WaferEdgeDetector` deliberately takes the
**chuck-side** one, because the chuck is in focus and that makes the measured *radius* repeatable.
`NotchDetector` takes the **wafer-side** one, because the notch is a feature of the wafer's outline
whereas the chuck-side boundary is where the shadow ends *on the chuck* — a shape set by the
illumination geometry, not by the notch profile. The 1.002 mm depth is the evidence that the wafer
side is tracing the real edge.

Everything else about the segmentation is shared, including the flank-contrast test, which asks "is
this dark region a rim gap at all" and is indifferent to which side is then taken.

> **Divergence to be aware of.** `Halcon/wafer edge gpt.hdev` still describes itself as a mirror of
> `WaferEdgeDetector.cs`, but the C# has since gained a `SeverRadius` stage and the flank-contrast
> gate, and switched to the chuck side. `notch detector.hdev` inherits the older front end, which is
> why its numbers differ from the C# detector's by ~1.3%.

---

## 4. The station, and what "rotate about the wafer centre" actually costs

The rim circle is larger than X/Y travel, so the camera cannot be driven around it — only the line
`X = X min` crosses it at all (see **[Wafer Centre-Finding by Rotation](WaferCentreByRotation/)**).
The wafer is turned past that one station.

Because the wafer sits eccentric on the chuck, holding station on the rim means cancelling the
wafer centre's orbit about the Θ axis. That is **all** it means — a 2.53 mm radius, one cycle per
revolution — not a 100 mm path. The centre-find has already measured it, so the station's Y is
*computed*, never searched:

```
Y(θ) = Cy(θ) ± √(R² − (Xstation − Cx(θ))²)          in mm, then × steps/mm
```

with `C(θ) = CalibrationStore.WaferCentreAt(θ)`.

### Which crossing — the ± is a real decision, not a formality

The line crosses the rim **twice**, and on this machine **only one of the two is reachable**:

| branch | Y path (USER frame) | fits Y travel −92,595..66,097? |
|---|---|---|
| upper | 58,126 … 70,662 | **no** — over Y max by 3.63 mm, out for 42% of the revolution |
| lower | −69,438 … −56,902 | **yes** — 0% out |

Both are 9.98 mm peak-to-peak; only their offset differs. No rule based on the travel limits alone
picks correctly: "nearest Y max", the obvious one, picks the **upper** branch and the sweep dies at
stage A. So `RimStation.TryChooseBranch` walks *both* candidates over a full revolution and keeps
the one that fits, preferring the nearer to the current Y if both do. Checked at 15° intervals
around the circle: every starting angle resolves to the lower branch and fits.

The centre-find has no such rule and does not need one — it rasters down from Y max until it happens
to see the rim, and **skips** whatever samples it then loses. Which branch it lands on depends on
where Θ started. A continuous sweep cannot skip, so the choice has to be made deliberately.

> **Watch the frame.** Y is inverted between the raw and user frames (`user = −raw`), so
> `calibration.json`'s Y `Min`/`Max` of −66,097/92,595 are **−92,595..66,097** as `UserLimits`
> reports them. `RimStation` works entirely in the user frame. An early verification of this page
> compared the user-frame path against the *raw* limits and wrongly concluded the upper branch fit —
> which is exactly the bug that shipped.

Once chosen, the branch preference is held **fixed for the whole run** — the sweep and every
re-centring nudge — because the crossings are ~130 mm apart while the path moves 10 mm, so a
constant preference cannot flip branch, and a nudge that flipped would jump the stage across the
wafer.

| | |
|---|---|
| Wafer radius | 100.21 mm |
| Eccentricity | 2.527 mm |
| Peak Y rate | **365 steps/s** — 1/9th of `ROTATE_FOLLOW_VMAX` |

This is by far the gentlest thing the follower has been asked to do. `RotateAboutCrosshairAsync`, by
contrast, drives X/Y around a ~100 mm circle to pin a *material* point — which is rotation about a
point on the rim, and would keep the same patch of wafer in view for the whole sweep. It is the
wrong tool; only its tuning is reused.

### Why `FrmMain.RimSweep.cs` is a second loop

What is worth sharing between the three continuous-motion loops is the **tuning** —
`ROTATE_FOLLOW_*`, the ramp constants, `FollowVel`, `CommandFollow` — and being a partial of the
same class, the sweep uses those directly, so no measured value is duplicated. The loop *bodies*
differ (one axis not two; an opaque delegate target rather than the closed-form pin geometry; a stop
condition that is a vision result rather than an angle). Folding three shapes into one parameterised
loop would put the joystick twist and the crosshair rotate — both hardware-tuned, both in daily use
— at risk of a silent regression to save a control structure.

The feedforward is the only genuinely new piece: the station's `dY/dθ` by **central difference of
the analytic path** at ±1°. That is differencing the *function*, not a history of quantised
commands, so it carries none of the noise that made the crosshair rotate abandon a measured-velocity
estimate. Its only error is the ±1 step rounding inside `WaferCentreAt`, ~1% of the 114 steps/deg
peak.

---

## 5. Shape of a run

| Stage | What happens |
|---|---|
| **A** | Park at `X min`, `Y = Y(θ₀)`; choose the reachable rim crossing; pre-check the *whole* Y path against travel; confirm a rim is actually in view |
| **B** | Sweep Θ continuously, grabbing free-running and testing each frame with the coarse detector |
| **C** | On a hit, ramp down, then re-measure with the **fine** detector on stationary frames — first where it stopped, then ±2° — until one reports the notch fully enclosed |
| **B/C loop** | A hit that does **not** confirm is recorded and swept past — back to B with the remaining arc |
| **D** | Convert the apex to a chuck-frame bearing; store `NotchAngleDeg` / `NotchDepthMm` |

### A false hit must not end the run

Even with the deep-**and**-wide rule above, the coarse test can still stop on something that is not
the notch — a chipped edge, or a stretch of rim the segmentation has mangled. That is an expected
event, not an error.

The first version treated it as terminal — one unconfirmed hit and the run reported "anomaly did not
confirm" and stopped — which made the search useless on any wafer with a speck on its rim, and the
captures on file show the rim is covered in them. Worse, the message it printed said *"most likely
it was debris"*: it diagnosed the benign case correctly and then quit anyway.

Now each stop is confirmed, and a stop that fails confirmation is added to a reject list and swept
past. Two things make that safe:

* **The arc budget is shared.** Every sweep leg asks for `375° − swept`, so total rotation is bounded
  by one revolution no matter how many false hits occur. The search cannot run forever.
* **Rejected angles are suppressed.** The coarse loop ignores a hit within `±3°` of an angle already
  rejected — a little wider than the ~2.8° a frame covers, so the same feature is recognised whether
  it was caught at a frame edge or its middle. Without this the resumed sweep would stop on the same
  speck immediately and spend the whole budget re-confirming one piece of debris.

`NOTCH_MAX_FALSE_HITS` (8) bounds the confirmation time, but its real job is to separate two
different faults: a handful of rejects is a dirty wafer and is normal, while eight says the **rim
itself is reading badly** — focus, lighting, or sweep blur — and the log says so rather than
reporting a clean "no notch".

Rotating to a datum is a **separate button**, so a search never turns the wafer as a side effect.

### The enclosure window, and why the search step must be finer than it

Stage C needs a frame where the notch is **fully enclosed** *and* clear of the chord's two end
anchors. That is a narrower condition than it sounds, and getting it wrong is what made the search
report *"ends are not plain rim"* on the very frames that had the notch in them.

The rim contour runs 3000–4200 points depending on how the rim crosses the frame, and the notch is
2.9 mm ≈ 2340 of them. On a 3138-point contour that leaves only ~800 points of clean rim in total:

| anchor length | clean rim left over | window in Θ |
|---|---|---|
| 300 points | ~200 points | **0.14°** |
| 200 | ~450 | 0.28° |
| **120** | ~560 | **0.40°** |
| 100 | ~600 | 0.42° |

With 300-point anchors the window is 0.07–0.14° on the shorter contours — and the re-centring search
was stepping **1.0°**. The grid was seven times coarser than the target, so it walked over it every
time. Both numbers were wrong together, which is why neither looked wrong alone.

`EndSpanPoints` is now **120** and `NOTCH_NUDGE_DEG` **0.25°**, i.e. the step is under even the worst
window. Shortening the anchors cost nothing: on the reference capture the measured depth *improved*
from 0.989 mm to **1.000 mm** against the SEMI nominal of 1.00, because shorter anchors sit on
cleaner rim, further from the notch's influence.

`MaxChordFitMm` went 0.08 → **0.25** for a related reason. It guards the **depth**, not the angle: a
tilted baseline biases depth and width directly, but the apex comes from two straight-line fits to
raw contour points and the baseline only decides which points land in the flank band — shifting that
band along a straight flank returns the same line. At 0.08 it was refusing usable frames on hardware
(0.101 and 0.151 mm). Its real job is to catch ends that are not rim *at all*, and 0.25 still does
that. Every result logs the value, and a result over 0.10 mm carries a note that the depth is
approximate while the angle is not.

### Re-centring must not cost more than the sweep

The order the re-centring frames are tried in is chosen to **minimise Θ travel**, because every one
is a real move and the obvious order is by far the worst one:

| order | span | step | travel | at 2000 steps/s |
|---|---|---|---|---|
| `0, +1, −1, +2, −2, +3, −3` (was) | ±3° | 1.0° | **21.6°** | ~11 s |
| `stay, +0.25 … +1.5, −0.25 … −1.5` | ±1.5° | 0.25° | **4.5°** | ~2.2 s |

The first entry is *no move at all* — measure where the sweep actually stopped. That is also the best
guess: Θ over-runs by ~0.65° while ramping down, and since the sweep drives the notch **into** the
frame, the over-run leaves it more centred than the angle the hit was recorded at, not less. The
forward tries then carry on in the direction Θ was already turning, so there is no reversal and the
notch keeps moving towards the middle of the frame; only then does it come back, in one reversal
rather than six.

Note the newer order takes **more frames** (13 against 7) and still travels a quarter as far, because
travel is set by the ordering rather than by the count. Frames are cheap — ~90 ms of detection — and
the finer step is what actually catches the notch.

The speed matters as much as the order. These are profile-**position** moves — the drive lands on
target regardless of how fast it gets there — so there is no accuracy argument for going slowly, and
the wafer Θ scan already commands 5000 for its own 14.4° steps. An earlier `NOTCH_NUDGE_SPEED` of
400 combined with the zig-zag made re-centring take **~54 s**, which is half a sweep spent shuffling
and reads, correctly, as the machine running away backwards.

### The two frames, and the datum

`NotchAngleDeg` is stored as a **chuck-frame** bearing — measured from the wafer centre, de-rotated
to θ = 0 — for the same reason `WaferOffsetX/Y` is: it is then **invariant as Θ turns** and does not
go stale. (Verified: recovering it from a synthetic notch at θ = 0, 55, 190 and 300° returns the
same value to 3 decimals.)

The **datum** is the other frame. As of 2026-08-06 it is read in the **camera's** frame — the bearing
as it appears on the live view — because that is the frame the operator works in; `CameraFrame`
converts, and the whole conversion is one angle, the camera's mounting tilt:

```
lab bearing = view bearing + tilt,     tilt = atan2(Yc/kY, Xc/kX) folded to (−90, 90]
```

`tilt` is the lab bearing of one pixel COLUMN — where the view's horizontal points — so it is
**measured, not configured**, and a camera swap needs only the camera-scale calibration re-run. It is
computed in mm because X and Y differ by 0.4 % in steps/mm (step space gives the same angle to 0.06°),
and folded because the ~180° the camera is mounted at belongs to the live view's display flip;
counting it twice would invert every bearing. On the affine on file it is **+4.59°**, so a typed
datum of 0 drives the notch to 4.59° in the machine frame, and the die grid — square to the notch to
0.06° — lands square on the screen.

**`CameraFrame` carries orientation only, and cannot carry positions.** The camera's origin travels
with X and Y, so a point expressed in its frame stops meaning anything the moment the stage moves;
that is why every vision measurement here is `E = M + A·(p_cross − p_edge)`, anchored by the motor
position. Directions have no origin, which is exactly why they convert cleanly and positions do not.

Underneath, the target is still a lab bearing. The two frames are related by

```
lab bearing = chuck-frame bearing + sign · Θ          (sign = WaferFitSign)
```

— the same relation `WaferCentreAt` applies — so the chuck angle that puts the notch on a datum `D`
is `Θ_target = sign · (D − φ)`, and the move is `Θ_target − Θ_now`.

> **Both terms are load-bearing, and both were missing in the first version of
> `RotateNotchToDatumAsync`.** It computed `delta = D − φ` directly. Dropping `Θ_now` treats a target
> as a delta, which is only right when Θ happens to read 0; dropping `sign` turns the wrong way
> entirely on this machine, where `WaferFitSign` is −1. Checked against the stored calibration at
> Θ = 0, 55, 190 and 300°: the old form landed the notch at 284°, 229°, 94° and 344° for a requested
> 150°, i.e. wrong every time and wrong differently each time. The corrected form lands on 150.00°
> at all four.

For reference, the camera station bears **~210°** in the machine frame (**~206°** as the datum now
reads it) from the wafer centre — measured on hardware
2026-08-06, with the station at X = −107,345 / Y ≈ −63,299 against a wafer centre near (2146, 1087):
`atan2(−51.25, −86.79) = 210.6°`. An earlier note here said ~150°, which is the mirror of that in Y:
it belongs to the **upper** rim crossing, and `TryChooseBranch` takes the **lower** one on this
machine because the upper leaves travel for 42 % of a revolution (§4). Don't hard-code either — the
number drifts as the eccentric centre orbits, and **Check notch angle** solves for it and logs it.

### Why the notch looks slanted in the camera, at every datum

It has to. The notch's axis of symmetry points **radially**, and the radial direction at the station
is fixed by where the station sits on the wafer — nothing to do with the datum, which only decides
*which part of the rim* is at the station.

Take the outward radial at the station into image space through `A⁻¹` and it lands at **25.7° above
image-horizontal**, i.e. the rim runs 26° off vertical. Measured on `capture_20260806_102745_217.bmp`
(band ellipse over a crop clear of the vacuum port): tangent −63.0°, so radial **27.0°**. The 1.3° is
crop and curvature noise over a 959 px band.

Decomposed, it is `station bearing − camera column-axis bearing = 210.6° − 184.6° = 26.0°`. The
camera's own tilt is only **4.6°** of that: a perfectly aligned camera would still show ~30°, because
the station is 30° round the wafer from the −X axis. **No datum value can make the notch appear
upright in the frame** — only physically rotating the camera, or standing somewhere else on the rim,
which the travel envelope does not allow.

**Check notch angle draws it** rather than asserting it. On a successful measurement it posts the
frame to the *Captured* pane with the apex (yellow) and that outward radial (cyan) on it, and logs
the angle — `TryRadialPixels` takes the radial into image space through `A⁻¹`, in mm and back to
steps so the 0.4 % anisotropy does not tilt it. A notch straddling the cyan line symmetrically is
the visual form of the number the check reports. If it ever does *not*, the affine's orientation is
wrong, and that would be worth knowing — which is why the line is drawn from the calibration rather
than fitted to the notch.

Note the two panes differ: the Captured pane is the raw frame, the **live pane is rotated 180°**
(`VisionViewControl._invertView`, the camera being mounted inverted). Same rim, same 25°, but the
notch points the opposite way, so the two must not be compared with each other by eye.

**Rotating the view to square it up was built and then rolled back** (2026-08-06), and the reason is
worth keeping: it would not straighten the notch. Squaring the picture with the machine makes screen
angles machine angles, so the notch would move from 25.4° to its true bearing of **30.6°** — further
from horizontal, not nearer. What is genuinely square is the wafer: with the datum at 0° the die
grid measures **0.06°** off the machine X axis (`capture_20260806_111932_667.bmp`,
projection-variance over both street families, 9.7× and 4.2× contrast). Rotating the *chuck* to
straighten the picture would be worse still — it would take silicon that is square to four
arcminutes and put it 4.6° out, and the datum, the notch bearing and every scan along a die street
would inherit that. The lean belongs to the camera mount, and that is where it can be fixed.

### Θ is read from the sweep, not from the drive

NanoLib access is serialized on one channel and the sweep owns it for the whole revolution, so the
grab thread must not call `TryReadThetaNow`. The sweep publishes Θ each tick to
`FrmMain.SweepThetaTicks`, and the grab loop samples it **inside the frame callback**, before the
~130 ms of processing — otherwise the angle recorded would belong to the moment the answer came
back, 0.7 mm of rim later.

Timing skew does not reach the result anyway: the reported angle comes only from the **stationary**
stage-C measurement.

### Two apex numbers, which are not interchangeable

The **depth** is measured to the deepest contour point. The **flank intersection** is a different
thing: it overshoots by 0.37 mm, because the traced silhouette's apex is genuinely rounded (fit a
circle and the radius is ~1.1 mm — what is traced is the gap's outer edge, defocused), and
extrapolating two straight flanks past a rounded tip must land beyond it. It is reported as
`VertexOffsetMm` so the overshoot stays visible instead of quietly inflating a depth.

The intersection is what the **Θ angle** uses, because it averages hundreds of flank points rather
than resting on one deepest pixel. That it is *steadier* is reasoning, not measurement — there is
one notch capture on file. Check it on hardware before relying on it.

`IncludedDeg` reads ~98° against the SEMI nominal 90° for the same reason: these are the shadow's
flanks, not the silicon's. It is a sanity check, not a measurement of the wafer.

---

## 6. Failure modes it is built to survive

**Chuck texture reading as a notch.** Run the notch geometry alone on a bare-chuck frame
(`capture_20260804_135135_136.bmp`) and it reports a 1.06 mm deep, 2.2 mm wide, contiguous,
fully-enclosed "notch" — the machined surface's shadow troughs are the right size *and* the right
shape. Nothing in the shape tests can tell them apart. What rejects it is `MinContourPoints`: a
trough's boundary comes out **280 points** against 3000–4200 for a real rim. Two more texture frames
go the same way at 700 and 1131 points, and the one that survives to the chord stage is then caught
on depth at 0.009 mm. The defence is layered and `MinContourPoints` is the load-bearing part.

**A feature in the CHUCK reading as a notch** — measured on hardware 2026-08-06
(`capture_20260806_102745_217.bmp`), where a run reported a notch at 313.64° that was not one.

A vacuum port sits in the chuck a few mm outside the rim, and its dark boundary had **merged with
the rim gap into a single region** (`cut=57 dark=1` — one dark region, not two). The traced
wafer-side boundary therefore follows the rim in, runs round part of the port's circular arc, and
comes back out. Two flank fits to an arc intersect just as they do on a notch, so every shape test
passed: contiguous, enclosed, chord fit 0.052 mm, width 1.85 mm inside the 1.5–4.0 band. The coarse
test fired at 1.30 mm, twice its usual notch value.

Three numbers said it was not a notch, and none of them was being enforced:

| | Real notch (`…114358_183`) | The port (`…102745_217`) |
|---|---|---|
| Depth | 1.005 mm | **1.954 mm** |
| Included angle | 98.5° | **62.9°** |
| Apex vs where the rim is | on the rim | **1.67 mm off it** |

Depth is now bounded above by `MaxNotchDepthMm` (1.5 mm — 49 % above every real measurement, 23 %
below this one), which rejects the frame on its own. The included angle is still reported and not
tested: there is one real measurement of it, so a window would be a guess.

The load-bearing fix is the third one, because it is the only test that asks **where the feature
is** rather than what it looks like — an arc is a notch shape wherever it happens to be. A real
notch's apex sits at the rim radius less its own depth, so `ApexOnRim` refuses a candidate whose
apex misses that by more than `NOTCH_APEX_TOL_STEPS`. Two details matter:

* It is compared against `VertexOffsetMm`, **not** `DepthMm`. The point being placed is the flank
  intersection, and that point is the vertex offset deep — the depth belongs to the deepest contour
  point, ~0.37 mm shallower. Comparing one point's radius against another point's depth spent that
  difference out of the tolerance for nothing.
* It runs **inside the confirmation loop**, not after it. A candidate that fails is rejected like
  any other false hit, the angle joins the suppression list, and the sweep carries on looking for
  the real notch — where the old post-hoc check could only print a warning about an answer the run
  had already committed to.

**A lost rim reading as a clean one.** `TryCoarse` returns **false** rather than a small residual
when the contour is too short to fit. That distinction matters over a 112 s unattended sweep: a run
that could not tell "clean rim, no notch" from "no rim at all" would sail past the notch reporting
nothing wrong for the rest of the revolution. Forty consecutive failures abandon the sweep.

**The wafer moving on the chuck.** Vacuum loss voids every angle measured against the stored offset,
exactly as it voids the centre-find (which has `WAFER_CLOSURE_TOL_STEPS` for the same reason). After
a fruitless sweep the rim radius is re-measured against the stored radius and a mismatch is reported
as *the wafer moved*, which explains "no notch" far better than a wafer with no notch does.

---

## 7. What is NOT verified

Everything about the vision is measured. Everything about the motion is arithmetic:

1. **Motion blur.** Every capture on file is stationary. At 5.6 mm/s with a 10 ms exposure the smear
   is ~45 px against an 800 px notch, which should be nothing — but the coarse test's margin is only
   2.6×, and blur eats into exactly that. **Test this first**, offline: capture frames spanning the
   notch during a slow continuous Θ jog and check the residual profile peaks at the notch and stays
   below 0.05 mm elsewhere.
2. **Y-follow tracking.** Sweep a revolution with capture disabled and log the rim's position in
   frame. Expect ≪ 1 mm of error at 365 steps/s.
3. **Frame pacing.** 130 ms of processing against a 710 ms budget assumes the camera delivers on
   demand at ~1.4 fps while the drives are being hammered at 40 Hz.
4. **End to end.** Set the notch to a known angle by hand, run 3×, require the reported angle to
   repeat within 0.1°.
5. **Negative.** Run on bare chuck — must complete a revolution and report "not found" rather than
   latching onto texture.

### Checking the answer: what the machine can measure about itself

Item 4 above is what **Check notch angle** does, without the "by hand" part. It turns Θ so the notch
comes round to the camera, re-measures it on a stationary frame, and reports
`measured − stored` in degrees *and in mm of rim*. It is not circular: where it drives depends on
the stored angle, but what it measures there does not — an angle wrong by ψ leaves the notch ψ off
the crosshair and the measurement reads that ψ back.

**The Θ it drives to is solved, not assumed.** The station's bearing *from the wafer centre* is not
a constant: the station rides the rim as Θ turns while the wafer centre orbits the rotation axis, so
the bearing drifts by about `atan(e/R)` either side of its mean. `RimStation.TryStationBearing`
returns it for a given Θ, and the check iterates `Θ = σ·(β(Θ) − φ)` three times — a very weak fixed
point, so it is at the noise floor after two. This is also why the user guide's "datum 150° parks
the notch under the camera" is only approximate while the check is not.

The reason this exists at all is an error budget that says the software **cannot** be the source of
a discrepancy of degrees. Against the calibration on file (eccentricity 0.717 mm, wafer R 100.24 mm,
scan RMS 0.076 mm over 24 samples), where one degree of rim is 1.75 mm:

| Source | Ceiling | Why that is the ceiling |
|---|---|---|
| Camera tilt | 0 | The affine holds it — its column axis measures 184.6° and its row axis 94.45° in stage mm, orthogonal to 0.1°, i.e. a real ~4.5° camera rotation. Every conversion goes through it. |
| Apex, crosshair, affine error | **±1.8°** | The apex is converted relative to the frame centre, so the displacement is bounded by half a frame diagonal (~3.1 mm). Realistically <0.2°. |
| `ChuckTicksPerRev` | **±0.04°** per revolution | A scale error smears the de-rotated scan progressively around the revolution; a 0.076 mm RMS fit bounds it. |
| Wafer offset / fit sign | **±0.82°** | Bearing error is `ε/R`; even a fully mirrored 0.717 mm eccentricity is 1.43 mm of ε. |

So a residual inside ~0.5 mm of rim means the stored angle and the Θ chain agree with each other,
and a datum that still looks wrong is being judged against something outside this chain. A residual
of degrees is real, and its behaviour separates the two remaining causes: one that repeats with the
same sign is a bias in the stored angle, one that follows the direction Θ approached from is
backlash or slip between the encoder and the chuck — which no stored number can fix.

---

## 8. Parameters

| Constant | Value | Why |
|---|---|---|
| `CoarseThresholdMm` | 0.30 | 7.5× the worst plain rim (0.040), 1.8× under the notch (0.541). Exposed as **Trigger (mm)**. |
| `CoarseMinRunPoints` | 200 | Notch runs 1143 points, plain rim 0. Rejects a narrow spike whatever its height. |
| `MinCoarseContourPoints` | 500 | Below this a residual is fitted to a scrap — return false, not "no notch". |
| `MinContourPoints` | 1500 | Two end spans plus a notch; also the texture-frame reject (§6). |
| `EndSpanPoints` | 120 | Chord anchor. Too LARGE is the dangerous direction — it shrinks the enclosure window to nothing. |
| `MaxChordFitMm` | 0.25 | Guards the depth, not the angle. At 0.08 it refused usable frames. |
| `MinNotchDepthMm` | 0.25 | 8× above the worst plain rim, 4× below a real notch. |
| `MaxNotchDepthMm` | 1.5 | 49 % above every real measurement (1.002 on file, 1.005 re-measured, 0.987 on hardware), 23 % below the 1.954 mm a chuck port produced (§6). |
| `NOTCH_APEX_TOL_STEPS` | 1000 | 0.79 mm. Budget: the stored radius is the chuck-side gap boundary while the apex is traced on the wafer side (~0.3 mm systematic), the radius fit (0.08 mm), the pixel→step conversion (~0.1 mm). Half the port's 1.67 mm miss. |
| `Min/MaxNotchWidthMm` | 1.5 / 4.0 | Rejects a chip or dust clump — deep but narrow. Not a gauge. |
| `DeepFraction` | 0.25 | What counts as "in the notch" for width and flank selection. |
| `FlankLo/HiFraction` | 0.25 / 0.85 | Flank band: clear of the rounded apex and of the shoulders. |
| `BridgeGapPx` | 30 | Rejoins a ring broken by dust. Keep ≪ notch width or it bridges the mouth. |
| `NOTCH_SWEEP_DEGREES` | 375 | A revolution plus overlap, so a notch at θ₀ is still seen whole. |
| `NOTCH_NUDGE_DEG` / `TRIES` | 0.25 / 6 | Step must be under the 0.32–1.16° enclosure window; span ±1.5° reaches either edge of a 2.2–3.0° frame. |
| `NOTCH_NUDGE_SPEED` | 2000 | Position moves — speed costs nothing in accuracy. At 400 re-centring took ~54 s. |
| `NOTCH_REJECT_WINDOW_DEG` | 3.0 | Suppression window around a rejected anomaly; wider than a frame. |
| `NOTCH_MAX_FALSE_HITS` | 8 | Dirty wafer (normal) vs the rim reading badly (a fault). |
| `NOTCH_MAX_MISSES` | 40 | ~5 s of lost rim before abandoning the sweep. |
| `NOTCH_RADIUS_TOL_STEPS` | 2000 | ~1.6 mm — the wafer-moved test. |
| `NOTCH_CHECK_STEP_DEG` / `SPAN` | 0.5 / 4.0 | The check's search either side of the stored angle. A frame's worth of rim per step, since this is measuring a bias rather than hunting the enclosure window; the span is wider than any error anyone would call "a few degrees", so a bias is quantified instead of reported as "not found". |
| `SWEEP_FF_HALF_DEG` | 1.0 | Central-difference half-interval for the Y feedforward. |
