---
title: Notch Search by Continuous Sweep
---

# Finding the Notch

The wafer centre-find turns the wafer past a fixed camera and fits a circle to the rim. Finding the
**notch** reuses that station but asks where on the rim the one non-circular feature is, and the
answer has to come out of a single revolution.

**Implemented** as the *Notch find (Θ sweep)* controls in the vision protocols window: orchestration
in `Vision/FrmVisionProtocols.NotchFind.cs`, detection in `Vision/NotchDetector.cs`, motion in
`FrmMain.RimSweep.cs`, geometry in `Geometry/RimStation.cs`. Tuning mirror:
`Halcon/notch detector.hdev`.

**Check whether it is already done.** The wafer centre-find screens every sample with the coarse
detector; when the anomaly it drops turns out to be the notch, it measures it and writes the same
`NotchAngleDeg` / `NotchDepthMm` / `NotchTimestamp` fields. Read the wafer scan log before spending a
revolution here.

---

## 1. Detection

The notch is **2.9 mm** of rim and the frame covers **4.9 mm**, so a frame does not have to contain
the notch — catching one flank is enough to bend the contour. That gives a detection window of about
6.6 mm of rim, which the sweep oversamples several times over (below). Requiring the notch *whole* in
frame would collapse the window to ≈1.4 mm and force a stepped scan.

`NotchDetector` has two modes, differing only in their baseline:

| | Baseline | Use |
|---|---|---|
| **Coarse** (`TryCoarse`) | regression line through the whole contour | the sweep — fires on a half-visible notch |
| **Fine** (`TryMeasure`) | chord anchored on the first and last `EndSpanPoints` | the measurement — needs plain rim at both ends |

The sweep must use the coarse test, because a partially-visible notch is what a sweep meets first.
The fine test runs once afterwards, on a stationary frame, once the notch has been re-centred.

Measured over twelve captures at full resolution:

| | plain rim | notch |
|---|---|---|
| coarse residual | 0.012 – 0.046 mm | **0.548 mm** |
| coarse run over 0.30 mm | **0 points** | **1143 points** |
| fine depth | 0.006 – 0.032 mm | **1.002 mm** |

### Deep and wide, not just deep

The coarse test measures how far the contour departs from straight, not how long it departs for.
Debris, a spur, or a bridged break in the ring is a narrow spike that can reach any height, so no
threshold on peak height alone separates them: raise it enough to reject debris and a real notch at an
unlucky angle is missed too.

Requiring the departure to persist over **200 contiguous contour points** (`CoarseMinRunPoints`)
separates them on shape. The notch runs 1143 points at a 0.30 mm cut; every plain-rim frame runs 0.

The 0.30 mm trigger is 6.5× the worst plain rim and 1.8× under the notch. It does not need to catch a
barely-visible notch: the sweep grabs a frame every ≈1 mm of rim and the notch sits wholly in frame
over 2 mm of travel, so about two frames per pass see all of it. It is exposed as **Trigger (mm)**
because the right value depends on sweep blur, which cannot be known off-hardware.

The fine depth landing on **1.002 mm** against the SEMI nominal of 1.00 mm is the best evidence that
the whole chain — calibration, contour, chord, depth — is right end to end. Nothing was fitted to that
number.

---

## 2. The run

Θ's 5000 steps/s cap and the 359,859-tick revolution fix the timing: **72 s** per revolution, a rim
speed of **8.75 mm/s**, and a **457 ms** budget per frame at the 4 mm design pitch. The detector needs
≈130 ms, so the sweep runs **continuously** rather than step-and-settle — ≈157 stops at ≈1.5 s each
would add four minutes to a 72 s floor, while sweeping adds nothing. In practice a frame lands every
≈1 mm of rim, well inside the 6.6 mm detection window.

| Stage | |
|---|---|
| **A** | Park at `X min`, `Y = Y(θ₀)`; pick the reachable rim crossing; check the whole Y path against travel; confirm a rim is in view |
| **B** | Sweep Θ continuously, grabbing free-running, coarse-testing every frame |
| **C** | On a hit, ramp down and re-measure with the fine detector on stationary frames — where it stopped, then ±0.25° — until one reports the notch fully enclosed |
| **B/C** | A hit that does not confirm is recorded and swept past; back to B with the remaining arc |
| **D** | Convert the apex to a chuck-frame bearing; store `NotchAngleDeg` / `NotchDepthMm` |

Rotating to a datum is a **separate button**, so a search never turns the wafer as a side effect.

### False hits must not end the run

The coarse test will stop on things that are not the notch — a chipped edge, or a stretch of rim the
segmentation mangled. This is expected, not an error, and the rim carries specks on every capture on
file. Each stop is confirmed; one that fails is added to a reject list and swept past. Two things keep
that bounded:

* **Shared arc budget.** Each sweep leg asks for `NOTCH_SWEEP_DEGREES − swept`, so total rotation
  cannot exceed one revolution plus overlap however many false hits occur.
* **Rejected angles are suppressed** within ±3°, slightly wider than the ≈2.8° a frame covers.
  Without this the resumed sweep stops on the same speck immediately and burns the budget on it.

`NOTCH_MAX_FALSE_HITS` (8) separates two faults rather than bounding time: a few rejects is a dirty
wafer, eight means the rim itself is reading badly — focus, lighting, or blur — and the log says so
instead of reporting a clean "no notch".

### The enclosure window sets the search step

Stage C needs the notch fully enclosed *and* clear of both chord anchors. That window is much
narrower than it looks, and getting it wrong made the search report "ends are not plain rim" on the
very frames that had the notch.

The contour runs 3000–4200 points at ≈1.24 µm each; the notch is ≈2340 of them. On a short contour
that leaves ≈800 points of clean rim, and each anchor consumes `EndSpanPoints` from both ends:

| `EndSpanPoints` | clean rim left | window |
|---|---|---|
| 300 | ≈200 pts | **0.14°** |
| 200 | ≈400 | 0.28° |
| **120** | ≈560 | **0.40°** |

With 300-point anchors the window is 0.07–0.14°, and the search was stepping **1.0°** — seven times
coarser than the target, so it stepped over it every time. Both numbers were wrong together, which is
why neither looked wrong alone. `EndSpanPoints` is now **120** and the step **0.25°**, under even the
worst window. Shortening the anchors also improved the measured depth (0.989 → 1.000 mm), because
shorter anchors sit further from the notch's influence.

`MaxChordFitMm` was relaxed 0.08 → **0.25** for a related reason: it guards the *depth*, not the
angle. A tilted baseline biases depth directly, but the apex comes from two straight-line fits to raw
contour points and the baseline only selects which points land in the flank band. At 0.08 it refused
usable hardware frames. A result over 0.10 mm is logged with a note that the depth is approximate
while the angle is not.

### Re-centring order

Every re-centring frame is a real move, so the order is chosen to minimise Θ travel:

| order | travel |
|---|---|
| `stay, +0.25 … +1.5, −0.25 … −1.5` | **4.5°** |

The first entry is no move at all, and it is also the best guess: Θ over-runs by ≈0.65° while ramping
down, and since the sweep drives the notch *into* the frame, the over-run leaves it better centred
than the angle the hit was logged at. The forward tries then continue the direction Θ was already
turning, so there is one reversal rather than six. The newer order takes more frames (13 vs 7) and
still travels a quarter as far, because travel is set by ordering, not count.

These are profile-*position* moves, so speed costs nothing in accuracy. `NOTCH_NUDGE_SPEED` is 3200;
an earlier 400 combined with the zig-zag made re-centring take ≈54 s.

---

## 3. Frames and the datum

`NotchAngleDeg` is stored as a **chuck-frame** bearing — measured from the wafer centre, de-rotated to
θ = 0 — for the same reason `WaferOffsetX/Y` is: it stays valid as Θ turns. The two frames are related
by the same rule `WaferCentreAt` uses:

$$\text{lab bearing} = \varphi + \sigma\Theta, \qquad \sigma = \texttt{WaferFitSign}.$$

So the chuck angle that puts the notch on a lab bearing $D$ is found by solving for Θ, and the move is
relative to where Θ is now:

$$\Theta_\text{target} = \sigma\,(D - \varphi), \qquad \text{move} = \Theta_\text{target} - \Theta_\text{now}.$$

**The operator works in the camera's frame**, so the datum the UI accepts is a bearing as it appears
on the live view. `CameraFrame` converts with the camera's mounting tilt plus a fixed quarter-turn
that puts the datum's zero at north:

$$\text{lab} = \text{datum} + 270° + \text{tilt}.$$

That gives **0 = N, 90 = W, 180 = S, 270 = E**. It keeps the view frame's direction of travel, so it
increases anticlockwise on screen and is not a compass bearing. `DatumToLab` / `LabToDatum` are the
only two places that encode this; every user-facing number goes through them.

`tilt` is the lab bearing of one pixel column, so it is **measured, not configured** — a camera swap
needs only the camera-scale calibration re-run:

$$\text{tilt} = \operatorname{atan2}\!\big(Y_c/k_Y,\; X_c/k_X\big) \quad\text{folded to } (-90°,\,90°].$$

It is computed in mm because X and Y differ by 0.4 % in steps/mm, and folded because the ≈180° the
camera is mounted at belongs to the display flip — counting it twice inverts every bearing. On the
current affine tilt is **+4.5°**, so a typed datum of 0 drives the notch to 274.5° in the machine
frame.

**Orientation only.** The camera frame cannot carry positions: its origin travels with X and Y, so a
point expressed in it stops meaning anything once the stage moves. Directions have no origin, which
is why they convert and positions do not.

For reference the camera station bears **≈210°** in the machine frame (**≈296°** on the datum dial)
from the wafer centre. Do not hard-code it — it drifts as the eccentric centre orbits, and **Check
notch angle** solves for it and logs it.

---

## 4. False positives it is built to reject

**Chuck texture.** Run the notch geometry on a bare-chuck frame and it reports a 1.06 mm deep, 2.2 mm
wide, contiguous, fully-enclosed "notch" — the machined surface's shadow troughs are the right size
*and* shape, and no shape test separates them. `MinContourPoints` rejects them: a trough's boundary
comes out at 280–1131 points against 3000–4200 for a real rim.

**A feature in the chuck** — seen on hardware, where a run reported a notch at 313.64° that was a
vacuum port. Its dark boundary had merged with the rim gap into one region, so the traced boundary
follows the rim in, runs round part of the port's arc, and comes back out. Two flank fits to an arc
intersect just as they do on a notch, so every shape test passed: contiguous, enclosed, chord fit
0.052 mm, width 1.85 mm.

| | real notch | the port |
|---|---|---|
| Depth | 1.005 mm | **1.954 mm** |
| Included angle | 98.5° | 62.9° |
| Apex vs the rim | on it | **1.67 mm off** |

`MaxNotchDepthMm` (1.5 mm) rejects it on depth alone. The decisive test is the third, because it is
the only one that asks **where the feature is** rather than what it looks like — an arc is a notch
shape wherever it sits. `ApexOnRim` refuses a candidate whose apex misses the rim radius less its own
depth by more than `NOTCH_APEX_TOL_STEPS`. Two details:

* Compare against `VertexOffsetMm`, **not** `DepthMm` — the point being placed is the flank
  intersection, which is 0.37 mm deeper than the deepest-point depth. Mixing them spends that
  difference out of the tolerance for nothing.
* It runs **inside** the confirmation loop, so a failed candidate is rejected like any other false hit
  and the sweep carries on. The earlier post-hoc version could only warn about an answer already
  committed to.

**A lost rim reading as a clean one.** `TryCoarse` returns false rather than a small residual when the
contour is too short to fit. Over a 72 s unattended sweep, a run that could not tell "clean rim, no
notch" from "no rim at all" would continue past the notch reporting nothing wrong. 40 consecutive
failures abandon the sweep.

**The wafer moving on the chuck.** Vacuum loss voids every angle measured against the stored offset.
After a fruitless sweep the rim radius is re-measured against the stored one, and a mismatch is
reported as *the wafer moved* — which explains "no notch" better than a wafer with no notch does.

---


## 5. Parameters

| Constant | Value | Why |
|---|---|---|
| `CoarseThresholdMm` | 0.30 | 6.5× the worst plain rim, 1.8× under the notch. Exposed as **Trigger (mm)**. |
| `CoarseMinRunPoints` | 200 | Notch runs 1143 points, plain rim 0. Rejects a narrow spike whatever its height. |
| `MinCoarseContourPoints` | 500 | Below this a residual is fitted to a scrap — return false, not "no notch". |
| `MinContourPoints` | 1500 | Two end spans plus a notch; also the texture reject (§4). |
| `EndSpanPoints` | 120 | Chord anchor. Too large is the dangerous direction — it shrinks the enclosure window to nothing. |
| `MaxChordFitMm` | 0.25 | Guards depth, not angle. At 0.08 it refused usable frames. |
| `MinNotchDepthMm` | 0.25 | 8× above the worst plain rim, 4× below a real notch. |
| `MaxNotchDepthMm` | 1.5 | 49 % above every real measurement, 23 % below the chuck port's 1.954 mm. |
| `NOTCH_APEX_TOL_STEPS` | 1000 | 0.79 mm — the wafer-side/chuck-side offset (≈0.3 mm), the radius fit (0.08 mm) and the pixel→step conversion (≈0.1 mm). Half the port's 1.67 mm miss. |
| `Min/MaxNotchWidthMm` | 1.5 / 4.0 | Rejects a chip or dust clump: deep but narrow. Not a gauge. |
| `DeepFraction` | 0.25 | What counts as "in the notch" for width and flank selection. |
| `FlankLo/HiFraction` | 0.25 / 0.85 | Flank band: clear of the rounded apex and of the shoulders. |
| `BridgeGapPx` | 30 | Rejoins a ring broken by dust. Keep well under the notch width or it bridges the mouth. |
| `NOTCH_SWEEP_DEGREES` | 375 | A revolution plus overlap, so a notch at θ₀ is still seen whole. |
| `NOTCH_NUDGE_DEG` / `TRIES` | 0.25 / 6 | Step under the 0.32–1.16° enclosure window; span ±1.5° reaches either edge of a frame. |
| `NOTCH_NUDGE_SPEED` | 3200 | Position moves — speed costs nothing in accuracy. At 400 re-centring took ≈54 s. |
| `NOTCH_REJECT_WINDOW_DEG` | 3.0 | Suppression window around a rejected anomaly; wider than a frame. |
| `NOTCH_MAX_FALSE_HITS` | 8 | Dirty wafer (normal) vs the rim reading badly (a fault). |
| `NOTCH_MAX_MISSES` | 40 | ≈5 s of lost rim before abandoning the sweep. |
| `NOTCH_RADIUS_TOL_STEPS` | 2000 | ≈1.6 mm — the wafer-moved test. |
| `NOTCH_CHECK_STEP_DEG` / `SPAN` | 0.5 / 4.0 | The check's search either side of the stored angle — a frame's worth of rim per step, spanning wider than any error anyone would call "a few degrees", so a bias is quantified rather than reported as "not found". |
| `SWEEP_FF_HALF_DEG` | 1.0 | Central-difference half-interval for the Y feedforward. |
| `SWEEP_MAX_MS` | 300000 | Wall-clock ceiling on one sweep leg. |
