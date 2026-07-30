---
title: Automated Chuck Centre-Finding
---

# Automating Chuck Centre-Finding

The manual flow (see the **[Chuck Center-Finding Analysis](ChuckCenterFindingAnalysis/)**)
has the operator jog the rim into view at several spots, capturing one rim point each time, then
circle-fits them. This page is the design record for **automating the point collection**: the
operator roughly centres the chuck once, and the stage then probes itself outward in several
directions, detecting a rim point in each, and fits the centre — with no per-point jogging.

**This is implemented.** It ships as the *Auto Centre-Find* controls in the vision protocols
window; the code is `Vision/FrmVisionProtocols.AutoCentre.cs`. This page explains *why* it is
shaped the way it is — starting with the **edge detector**, which both the manual and the
automatic flow share and whose behaviour dictates the shape of the scan. The developer guide's
**§17** documents the as-built automation, and the user guide's **§10** the operator procedure.
Where they disagree with this page, they win.

> **Frame.** As in the analysis page, every detected edge pixel becomes the stage position
> that would bring it onto the fixed crosshair,
> $$\mathbf{E} = \mathbf{M} + A\,(\mathbf{p}_\text{cross} - \mathbf{p}_\text{edge}),$$
> with $\mathbf{M}$ the stage position at capture and $A$ the calibrated pixel$\rightarrow$step
> affine. The collected $\mathbf{E}$ all lie on the chuck's rim circle; fitting it (Pratt)
> gives the centre.

## The edge detector — a focus RIDGE, not brightness (`Vision/ChuckEdgeDetector.cs`)

Everything below follows from what the detector can and cannot see, so it is documented here.
The same `ChuckEdgeDetector` serves the manual flow (developer guide **§16 B**) and this
automatic one; neither changes it.

Across the rim there are **three** zones: the in-focus, sharply-textured chuck face; a thin
in-focus **dark band**; and beyond it the **out-of-focus** (blurry) background. The true edge is
the boundary between the in-focus and out-of-focus sides. The two sides are nearly the same
colour, so brightness can't separate them — and a *coarse* focus-energy map smears the thin dark
band into the blur, landing the edge on the wrong side of the band. But in a **fine-scale
sharpness map** (gradient magnitude pooled over a *small* window) that boundary shows up as a
thin, continuous **bright ridge**: sharp on the chuck side, dark on the blurry side. The detector
extracts that ridge directly as a sub-pixel line. The HDevelop tuning script
`Halcon/chuck edge detector.hdev` mirrors this pipeline stage for stage (with
`dev_display`/`stop` after each), so it can be tuned against representative captures.

`TryDetect(image, crossRow, crossCol, …)` runs on the **full-resolution** frame:

1. **Red channel → byte.** The scene is red-lit, so channel 1 carries the contrast; mono frames
   pass through (`Preprocess`).
2. **Fine sharpness map.** `sobel_amp('sum_abs', SobelWidth)` → gradient magnitude (high where
   sharp), then `mean_image(FineWindow, FineWindow)` with a **small** window so in-focus detail
   stays crisp and the in-focus/out-of-focus boundary reads as a thin bright ridge. A coarse
   window would blur that ridge into the halo — `FineWindow` is the key knob, and the reason a
   coarse focus-energy map does not work here.
3. **Extract ridges.** `lines_gauss(LineSigma, LineLow, LineHigh, 'light', …)` traces bright
   curvilinear ridges at **sub-pixel** accuracy. `LineSigma` ≈ the ridge half-width;
   `LineLow`/`LineHigh` are the hysteresis thresholds on line response (low, because the pooled
   map has a modest response scale).
4. **Keep the edge, drop the texture.** `select_contours_xld('contour_length', MinLineLength, …)`
   keeps only long contours: the chuck edge is one long continuous ridge, while the sharp texture
   below it produces many short responses that fall away.
5. **Nearest point wins.** Of every point on the surviving ridge contour(s), return the one
   **nearest the crosshair** as the `EdgePoint(Row, Column)`. That single point is a true
   (sub-pixel) point on the rim — which is all the centre-find needs, and it sidesteps the
   aperture problem (a smooth arc only reveals motion along its normal, so you can't localise
   *along* it — but you can localise the one point under the crosshair). The ridge contour is
   optionally returned for overlay; **the caller owns and disposes it**. The input frame is never
   modified, and every HALCON temp is disposed in a `finally`.

```
red channel → sobel_amp (sharp=high) → mean_image (FINE window → ridge=bright)
  → lines_gauss (extract bright ridge, sub-pixel) → select by length (drop texture)
  → point nearest crosshair
```

> **Tunables** (`SobelWidth=3`, `FineWindow=9`, `LineSigma=1.5`, `LineLow=0.5`, `LineHigh=1.5`,
> `MinLineLength=500`) are properties that mirror the `.hdev` script's variables; tune them there
> first, then copy across.

Two of those stages set the terms for everything below: step 5 is why a rim point is available
the moment the rim enters the frame, and step 4's `MinLineLength` is what bounds the hop size.

## The core idea

The starting proposal was:

1. Manually jog the chuck to roughly the centre.
2. Automatically jog outward until an edge is detected.
3. Repeat in several unique directions.

That is the right shape, with the adjustments below.

### Detect *in frame*, not *on the crosshair*

Because `TryDetect` returns the rim point nearest the crosshair **as soon as the rim is anywhere
in the field of view** (above), the affine can convert that pixel to a step-space rim point
straight away. So "jog until an edge is detected" means **until the rim enters the frame**, and
the moment `TryDetect` succeeds you already have a valid rim point. No null-ing servo loop is
needed — each direction is a plain *coarse-step-until-detected* scan.

### Command absolute targets, not a continuous jog

Because the soft master can't cleanly stop a continuous move on a vision trigger, and because
motion here is **step-and-settle**, each outward move is an **absolute computed target**:

$$\mathbf{target}_k = \mathbf{centre} + \hat{\mathbf{d}} \cdot (\text{jump} + k \cdot \Delta s),$$

for hop index $k = 1, 2, \dots$, heading unit vector $\hat{\mathbf{d}}$, and hop size
$\Delta s$. After each move: settle, read the position, grab a frame, run `TryDetect`. This
fits the architecture, removes stop-latency, and — critically — makes the travel bound
*inherent*: you never **command** a position beyond the guard.

It also matters for *accuracy*, not just safety. A frame exposed while the stage is moving has
no position sample corresponding to the exposure instant, and $\mathbf{E}$ is built from
exactly that pairing. Capturing stopped is what makes $\mathbf{M}$ exact.

## The as-built scan: bisect, then diagonals

The naïve version — $N$ evenly-spaced headings straight from the operator's rough centre — was
rejected. The problem is the *starting point*: it is the operator's eye, so it can be well off
centre, which forces a wide acceptance band on every point and makes the approach jump unsafe.

So the run happens in stages, each one buying information the next stage uses:

| Stage | Directions | Purpose |
|---|---|---|
| **A** | N, S | bisect for $c_y$ |
| **B** | E, W (from the corrected $c_y$) | bisect for $c_x$ → estimate $C_1$ **and a measured radius** |
| **C** | NE, NW, SW, SE (from $C_1$) | four more rim points, now with a tight band and an approach jump |
| **D** | — | Pratt-fit all eight points; persist centre + radius |
| **E** | — | report per-point radial residuals |

```
for each heading d̂:
    move to the current centre estimate     # each direction independent; no accumulated travel
    for k = 1, 2, … :
        dist = jump + k·Δs
        if dist > guard: break                          # radius guard
        target = centre + d̂ · dist
        if target outside stored travel envelope: break  # host soft limits
        MoveAbsolute(target); settle
        read position FRESH; verify arrival
        if TryDetect succeeds and the point passes the sanity checks
           and repeats on a second frame:
            record the rim point; break
    # guard reached with no detection: skip this direction
fit centre with Pratt CircleFit over the collected points   # need ≥ 3
```

Returning to the centre estimate before each direction keeps the scans independent, stops
travel from creeping toward a limit, and takes the rim back out of frame so the previous leg's
edge can't re-fire. Always approaching each rim point **outward** also keeps backlash
consistent across all points.

**Stages A/B are a re-centring stage, not the estimator.** `TryDetect` returns the rim point
nearest the *crosshair*, which lies along the ray $C \rightarrow M$ rather than on the scan
line, so with a laterally offset start the midpoint only approximates a true chord bisection.
It is good enough to aim the diagonals; the answer comes from the fit over all eight points.

For the same reason the radius is the **mean distance of the four cardinal points from
$C_1$** — not half the N–S span, which shortens to $2\sqrt{R^2 - \delta^2}$ when the start was
offset laterally by $\delta$.

## Safety — the constraint that shapes the design

The hardware offers **no protection against a runaway outward scan**: X's **+end limit switch
is dead**, **Z has none**, and the drives' soft limits (`0x607D`) read a fake $\pm9999999$. If
the rough centre is off, or a direction never sees an edge, an unbounded scan can drive **into
a hard stop** (worst on X+). Therefore:

- **Per-direction max-travel guard.** Sized from the *operator-entered nominal chuck radius*
  times `AUTO_GUARD_R` (1.8) — generous, because the starting point may be well off-centre.
  No detection within that distance → abort the direction. This is the primary crash guard,
  and on X it is effectively the only one.
- **Host-side soft position limits.** Because the drive's limits are fake, the **application**
  clamps every $\mathbf{target}_k$ to the stored X/Y travel envelope before commanding it.
  Never rely on the drive to stop you. If those limits aren't set, the run warns and offers to
  proceed on the radius guard alone.
- **Arrival verification.** After each hop, read the position **fresh from the drives** — not
  the cached one, which is stale for at least a status period after every move — and require it
  to match the target. This catches a move that was silently rejected (which would otherwise
  hop in place until the guard and report a clean "miss") and a quick-stop.
- **False-positive rejection.** A detection is accepted only if it lies *ahead* of the probe
  heading and its distance from the centre estimate falls in the expected band — wide
  ($0.2R \dots 1.8R$) in stages A/B, tight ($0.7r_1 \dots 1.3r_1$) in stage C once the radius
  is measured. It must also **repeat on a second frame without moving**, so a one-frame
  detector artefact can't enter the fit.
- **Rim not already in view.** The opening capture rejects that start: the first probe's own
  edge would be indistinguishable from the one it is hunting.
- **Manual lockout.** The run holds `BeginExternalOp` for its whole duration, so the main
  window's d-pad, puck and *polled* analog joystick can't move the stage between a move and the
  capture paired with it. Without this the arrival check would pass and the point would still
  be wrong.
- **Enough points.** The fit needs $\ge 3$; with 8 attempts a couple of skipped directions are
  survivable. A run that aborts mid-way **discards its points**, so a later manual Compute
  Centre can't fit a half-collected rim.
- **Z is never moved.**

## Parameters

| Parameter | Meaning | As built |
|---|---|---|
| directions | How many outward scans | **8** — the 4 cardinals (which also do the bisection) plus the 4 diagonals. Even angular spacing matters more than the count for a well-conditioned fit. |
| $\Delta s$ (hop) | Outward move per check | `AUTO_HOP_FRAC` = **0.4** of the frame's smaller extent *in step space*, computed per run from the live frame through $A$. Must stay well under a full frame or the rim can be skipped between captures — and `ChuckEdgeDetector` needs a ≥`MinLineLength` (500 px) ridge, so a rim merely clipping a corner does not count as seen. **Never cached:** zoom is a centred-ROI crop, so the field of view in steps changes with it. |
| jump | Skip the empty chuck interior | `AUTO_APPROACH_R` = **0.8 × the measured radius**, **stage C only**. Pure time optimisation, and safe only because $C_1$ came from the bisection: from a rough centre off by $\delta$, a jump aligned with $\delta$ lands at $0.8R + \delta$, which can be *past* the rim — and the rim is then skipped unseen. |
| $R$ (nominal radius) | Feeds the guard + the wide band | Operator-entered, seeded from the last fit's `ChuckRadius` so it comes from a measurement rather than being re-typed each session. |
| guard | Abort distance per direction | `AUTO_GUARD_R` = **1.8 × $R$**. |

## Preconditions

- **Pixel↔step affine calibrated** ($A$) — the conversion and the whole centre-find depend on it.
- **Z / focus set** — the detector keys on a *focus* ridge; at the wrong Z the ridge never forms
  and nothing detects. Rough-centring must include getting focus right.
- **Drives enabled and idle**, camera streaming.
- **Rough centre** provided by the operator, with the rim **out of frame**.

## Where it sits in the code

The building blocks were already there; the automation is an **orchestration layer** over them:

- **Grab + detect + convert:** `DetectEdgeAsync` is the awaitable form of the manual
  `RequestEdge` — same detector, same grab thread, result handed back instead of stored (with a
  timeout, because the grab thread can drop a job silently if the camera closes mid-run).
  `CentreFinder.ToStepPoint` does the affine conversion *without* storing, so a candidate can be
  sanity-checked before it is admitted.
- **Point accumulation + fit:** the same `CentreFinder` (`_chuckFinder`) and the same
  `ComputeCentre` → Pratt `CircleFit` → persist `ChuckCenterX/Y` + `ChuckRadius`.
- **Motion:** absolute moves go through `IMotionHost.MoveToAsync` (i.e. `FrmMain`) — the single
  motion entry point, so the bounds-check and the Y user↔raw flip are never re-implemented.

**No change to the detector or the fit was required.**

## Reliability extension (not implemented)

The single-pass result could be strengthened exactly as the analysis page's
**concentric-rings** method (§5 there) prescribes: run the whole scan at a **different radius**
twice more and average the three fitted centres, cutting the centre's standard error by
$\approx 1/\sqrt{3}$. In automation terms that is just an outer loop over a few guard/step
radii. The **rotational-invariance test** (§6 there) would then validate the averaged centre
against the physical rotation axis.

What *is* implemented in that spirit is stage E: per-point radial residuals, because the fit's
own RMS hides a single bad point among eight, and the residuals say which direction it came
from.
