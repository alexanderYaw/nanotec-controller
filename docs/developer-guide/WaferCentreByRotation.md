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

The fix needs no extra state: **the next station is the previous rim point.** `E_k` is by
definition the motor position that puts that rim point on the crosshair, so the scan moves there
before the next Θ step. Consecutive samples then differ by about `e·sin(Δθ)` radially and the same
again tangentially — at N = 24 (15° steps) and e = 3 mm that is ~1.1 mm total, comfortably inside
the ±1.85 mm half-frame. It also keeps every detection **near the crosshair**, which is where
affine error contributes least to `E_k`.

On a miss the scan searches ±1, ±2, ±3 hops along the station direction, then **skips that angle
and carries on**. Missed samples must never abort a run.

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
from mid-grey bevel and background texture. The rim is the boundary of that dark region nearest the
crosshair. Both stages track exposure, as the single stage did.

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

## 7. Shape of a run

| Stage | What it does |
|---|---|
| A | Pick the station direction: the cardinal from `C` with the most travel headroom. Refuse the run if none clears the nominal wafer radius. |
| B | Acquire the rim — the existing `ProbeAsync`, outward from `C`, with a `0.9 × R` approach jump to skip the empty middle. |
| C | N+1 samples, rotating Θ by 360/N between them. The station follows the rim (§6). The last sample repeats θ₀. |
| D | De-rotate and fit (`WaferCentreScan`): settle the handedness, drop outliers past `max(3σ, 0.3 mm)`, refit once. |
| E | Closure check, then persist. |

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

### The notch

These are 200 mm wafers with a **notch**, not a flat: under half a degree of arc, ~1 mm deep. At
15° sampling it rarely lands on a sample at all, and when it does it is an ordinary outlier that
the 3σ drop removes. The dropped angle *is* the notch angle — the run logs it, though nothing
consumes it yet. A wafer with a **flat** would be a different matter (a 200 mm primary flat is ~33°
of arc with a 4.2 mm sagitta) and would cost several samples; the run tolerates that by skipping
them, but the station-follows-the-rim scheme was not sized for that jump.

### Parameters

| Constant | Value | Why |
|---|---|---|
| `WAFER_GUARD_FRAC` | 1.25 | How far the rim probe may travel out, × nominal R. Covers any plausible eccentricity. |
| `WAFER_BAND_LO/HI_FRAC` | 0.70 / 1.30 | Acceptance band on `\|E − C\|`, × nominal R. |
| `WAFER_APPROACH_FRAC` | 0.90 | Approach jump, × nominal R — skips the empty middle. |
| `WAFER_BORDER_MARGIN_PX` | 8 | Frame-border rejection (§6). |
| `WAFER_SEARCH_HOPS` | 3 | Local radial search either side of the station on a miss. |
| `WAFER_THETA_SPEED` | 3000 | Θ tops out at 3200; a revolution is 359,859 ticks ⇒ ~2 min of turning. |
| `WAFER_CLOSURE_TOL_STEPS` | 400 | Closure tolerance (≈0.32 mm). |
| `OUTLIER_SIGMA` / `OUTLIER_FLOOR_MM` | 3.0 / 0.3 mm | Outlier drop; the floor stops a clean scan shedding good points to its own noise. |
| `SIGN_SEPARATION_MM` / `SIGN_MARGIN` | 0.05 mm / 0.5 | When the data may decide the handedness (§5). |

Total run time is dominated by the single revolution, so N barely affects it — N = 24 is not
expensive relative to N = 12.

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
a mis-scaled `StepsPerMm` — because those move every point coherently. The rotation test can.
