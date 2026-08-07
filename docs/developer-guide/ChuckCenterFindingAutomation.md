---
title: Automated Chuck Centre-Finding
---

# Automating Chuck Centre-Finding

The manual flow (see **[Chuck Center-Finding Analysis](ChuckCenterFindingAnalysis/)**) has the
operator jog the rim into view at several spots, capturing one rim point each time, then circle-fits
them. This page is the design record for **automating the point collection**: the stage homes itself,
drives to a fixed seed point, probes outward in eight directions detecting a rim point in each, fits
the centre, and returns to it, with no jogging. The only operator input is the max search radius.

**This is implemented** as the *Auto Centre-Find* controls in the vision protocols window; the code
is `Vision/FrmVisionProtocols.AutoCentre.cs`. It can also be started from the main window's
**Calibration… → Home & centre chuck (auto)**, which runs `FindXyLimitsAsync` and then this routine
via `RunAutoCentreFromHostAsync` — an alias for the Run button's own handler, so both entry points
share one run path, one set of preconditions, one confirmation and one log. The developer guide's
**§17** documents the as-built automation and the user guide's **§10** the operator procedure; where
they disagree with this page, they win.

> **Frame.** Every detected edge pixel becomes the stage position that would bring it onto the fixed
> crosshair,
> $$\mathbf{E} = \mathbf{M} + A\,(\mathbf{p}_\text{cross} - \mathbf{p}_\text{edge}),$$
> with $\mathbf{M}$ the stage position at capture and $A$ the calibrated 2×2 pixel→step affine. Every
> rim point sits at radius $R$ from the chuck centre, so the collected $\mathbf{E}$ lie on a circle
> whose centre is the stage position that puts the chuck centre on the crosshair. Fitting it (Pratt)
> gives that centre.

## The edge detector (`Vision/ChuckEdgeDetector.cs`)

Everything below follows the functioning of the detector. The same `ChuckEdgeDetector`
serves the manual flow (developer guide **§16 B**) and this automatic one.

**Reasoning for using the inner circle:** The centre-find only needs points on *some* circle
concentric with the chuck. It uses the **inner** circle — the boundary between the brightly-lit,
in-focus machined chuck face and the large near-black region inside it — because it is the simplest to maneuver to and run the automated routine while avoiding confusion with other edges. The scan below probes in
opposite **pairs** (N/S, E/W, NE/SW, NW/SE).

**Using grey level, not focus.** Across the *outer* rim the two sides are nearly the same brightness, so
only focus could separate them. At the inner circle that is inverted, and measurably so:

| cue | dark side | bright side |
|---|---|---|
| **grey** (the discriminator) | 72.9 % of frame, mean 13.8, 99 % below 24.9 | 27.1 %, mean 232.9, 19.4 % of frame saturated at 255 |
| **focus energy** (unusable) | mean 2.08, 99 % at 39.5 | 1st percentile **2.8** — overlapping |

`TryDetect(image, crossRow, crossCol, …)` runs on the **full-resolution** frame:

1. **Red channel → byte.** Mono frames pass through (`Preprocess`).
2. **Grey cut.** `threshold(BrightThreshold, 255)` → the bright face. The threshold is **fixed**, not
   Otsu: these two grey levels are set by the illumination and the material and do not move with
   framing, whereas Otsu is a *relative* split that shifts with how much of the frame each side
   occupies — which is exactly what changes as the stage scans.
3. **Reject a one-population frame.** If the face covers less than `MinFaceFraction` or more than
   `MaxFaceFraction` of the frame, the boundary is not in view and there is nothing to measure. This
   gate can be this simple *because* step 2 is fixed — Otsu always returns a split and would produce a
   boundary from noise. Measured: all-dark 0.00 %, all-bright 99.82 %, boundary in view 27.36 %.
4. **Clean up to one solid region.** `closing_circle(CloseRadius)` repairs the pits that bite into the
   lip, `opening_circle` drops stranded specks, largest component, `fill_up`. The closing supplies
   most of the accuracy.
5. **Outline → arc.** `gen_contour_region_xld('border')`, `clip_contours_xld` at a rectangle inset by
   `BorderMargin` to sever the runs along the image frame, then `select_contours_xld` by length to
   drop the stubs that leaves.
6. **Nearest point wins.** Of every point on the surviving arc(s), return the one **nearest the
   crosshair** as the `EdgePoint(Row, Column)`.

```
red channel → threshold (fixed grey cut) → face-fraction gate
  → closing/opening → largest component → fill_up
  → region border → clip to frame inset → select by length → point nearest crosshair
```

> **Tunables** (`SmoothWindow=1` i.e. off, `BrightThreshold=80`, `CloseRadius=105`, `OpenRadius=15`,
> `MinArcLength=800`, `BorderMargin=3`) mirror `Halcon/innerCircleDetection.hdev`, which carries the
> full parameter sweeps.

Two of those stages constrain everything below: step 6 is why a rim point is available the moment the
boundary enters the frame, and step 5's `MinArcLength` is what bounds the hop size.

> The previous focus-based **outer-rim** detector, with its own three-zone model and tuning notes, is
> preserved in `Halcon/chuck edge detector.hdev`.
> 
## The core idea

Get to a repeatable rough centre, probe outward until an edge is detected, repeat in several
directions. That shape is right, with two adjustments:

**Detect *in frame*, not *on the crosshair*.** Because `TryDetect` fires as soon as the rim enters the
field of view, the moment it succeeds a valid rim point already exists — no null-ing servo loop, just
a coarse-step-until-detected scan per direction.

**Command absolute targets, not a continuous jog.** The soft master cannot cleanly stop a continuous
move on a vision trigger, and motion here is step-and-settle, so each hop is an absolute target

$$\mathbf{target}_k = \mathbf{centre} + \hat{\mathbf{d}}\,(\text{jump} + k\,\Delta s)$$

— move, settle, read position, grab, detect. This removes stop latency, makes the travel bound
*inherent* (a position past the guard is never commanded), and keeps $\mathbf{M}$ exact: a frame
exposed in motion has no position sample for the exposure instant, and $\mathbf{E}$ is built from
exactly that pairing.

## The as-built scan: bisect, then diagonals

Each stage supplies a quantity the next one uses:

| Stage | Directions | Purpose |
|---|---|---|
| **0** | — | X and Y **Home**, then `AUTO_SEED_DY` (+15 000) along Y → the seed point |
| **A** | N, S | bisect for $c_y$ |
| **B** | E, W (from the corrected $c_y$) | bisect for $c_x$ → estimate $C_1$ **and a measured radius** |
| **C** | NE, NW, SW, SE (from $C_1$) | four more rim points, with an approach jump |
| **D** | — | Pratt-fit all eight points; persist centre + radius |
| **E** | — | report per-point radial residuals |
| **F** | — | drive to the fitted centre, so the run ends with the chuck centred |

**Stage 0 is what makes the run automatic.** The rough centre starting point is the Home 
plus a fixed offset that is the mechanical shift from there to roughly-over-the-feature on this
machine. Home is repeatable, so the start is too.

**Stages A/B re-centre, they do not estimate.** `TryDetect` returns the point nearest the *crosshair*,
which lies along the ray $C \rightarrow M$ rather than on the scan line, so from a laterally offset
start the midpoint only approximates a chord bisection — sufficient to aim the diagonals, while the
answer comes from the fit over all eight.

The radius is the **mean distance of the four cardinal points from $C_1$**, not
half the N–S span. A scan line offset laterally by $\delta$ from the true centre cuts the circle in a
chord of length $2\sqrt{R^2 - \delta^2}$, so half the span measures $\sqrt{R^2-\delta^2} < R$ and
under-estimates the radius by a factor that grows with $\delta$. The four cardinal points are each at
distance $R$ from the true centre regardless of $\delta$, so their mean distance from $C_1$ is
unbiased to first order in the residual centre error.

Even angular spacing matters more than the number of samples: the Pratt fit's conditioning depends on the points
spanning the circle, and eight evenly-spaced headings span it with margin.

## Safety

- **Per-direction max-travel guard** — the operator-entered **max search radius**, a flat step count
  with no multiplier. The primary crash guard, and on X effectively the only one. It is *also* the top
  of the acceptance band at every stage, so one number governs both how far a probe may travel and how
  far out a detection is still believed. The cost of that simplification: stage C no longer re-checks a
  diagonal against the radius the bisection just measured (it was $1.3\,r_1$), so a false edge anywhere
  inside the search radius is now accepted.
- **Host-side soft position limits** — the *application* clamps every $\mathbf{target}_k$ to the stored
  X/Y travel envelope before commanding it; the drive is never relied on to stop the axis. The envelope
  is guaranteed present, because stage 0 refuses to start without a Home for X and Y and that Home *is*
  the centre of the measured limits.
- **Arrival verification** — a **fresh** position read after each hop (the cached one is stale for at
  least a status period), matched against the target within $\Delta s/4$. Catches a silently rejected
  move, which would otherwise hop in place until the guard and report a clean "miss", and a quick-stop.
- **False-positive rejection** — the detection must lie *ahead* of the heading, fall in the expected
  distance band ($0.2R_\text{max} \dots R_\text{max}$ in A/B, $0.7r_1 \dots R_\text{max}$ in C — only
  the *lower* bounds are fractional), and **repeat on a second frame without moving**, so a one-frame
  artefact cannot enter the fit.
- **Rim not already in view** — the opening capture rejects that start; the first probe's own edge
  would be indistinguishable from the one it is hunting.
- **Manual lockout** — `BeginExternalOp` is held for the whole run, so the d-pad, puck and *polled*
  analog joystick cannot move the stage between a move and the capture paired with it.
- **Enough points** — the fit needs $\ge 3$, so a couple of skipped directions out of 8 are survivable;
  an aborted run **discards its points** so a later manual Compute Centre cannot fit a half-collected
  rim.
- **Z is never moved.**

## Parameters

| Parameter | Meaning | As built |
|---|---|---|
| directions | How many outward scans | **8** — 4 cardinals (which also bisect) + 4 diagonals. |
| $\Delta s$ (hop) | Outward move per check | `AUTO_HOP_FRAC` = **0.4** of the frame's smaller extent *in step space*, computed per run through $A$. Well under a full frame, or the boundary is skipped between captures — and an arc merely clipping a corner fails `MinArcLength`, so it does not count as seen. **Never cached:** zoom is a centred-ROI crop, so the field of view in steps changes with it. |
| jump | Skip the empty chuck interior | `AUTO_APPROACH_R` = **0.8 × the measured radius**, **stage C only**. A time optimisation, safe only because $C_1$ came from the bisection: from a rough centre off by $\delta$ the jump can land *past* the boundary, which is then skipped unseen. |
| $R_\text{max}$ (max search radius) | The guard **and** the top of every acceptance band | Operator-entered, **10 000 steps** as shipped. An operational bound on travel, not a measurement of the feature — so it is deliberately **not** seeded from the last fit. `ComputeCentre` still persists the measured radius as `ChuckRadius`. |
| seed offset | Home → rough centre | `AUTO_SEED_DY` = **+15 000 steps** on Y, **user** frame — which is inverted from the raw drive frame ($y_\text{user} = -y_\text{raw}$), so this sign is the one that moves the stage the right way on the machine. Machine-specific. |

## Preconditions

- **Pixel↔step affine calibrated** ($A$) — the whole centre-find depends on it.
- **Z / focus set** — the detector no longer *keys* on focus (it cuts on grey level), but the boundary
  still has to be a clean step: badly defocused, the chuck face stops reaching the bright plateau and
  the fixed threshold walks. Nothing in the run sets focus.
- **Drives enabled and idle**, camera streaming.
- **Home set for X and Y** — a hard error otherwise; stage 0 starts by driving there.
- **Max search radius entered** — it arms the travel guard, so it is the one number the operator still
  has to get right. No rough centring is needed: stage 0 produces the start itself, and the opening
  capture rejects the one bad starting condition (boundary already in view).
- **A clear path from wherever the stage is to Home**, since that is the first move of the run.

## Code implementation

The building blocks already existed; the automation is an **orchestration layer** over them, and no
change to the detector or the fit was required:

- **Grab + detect + convert:** `DetectEdgeAsync` — the awaitable form of the manual `RequestEdge`, same
  detector and grab thread, with an `AUTO_GRAB_TIMEOUT_MS` backstop because the grab thread can drop a
  job silently if the camera closes mid-run. `CentreFinder.ToStepPoint` converts *without* storing, so
  a candidate can be sanity-checked before it is admitted.
- **Point accumulation + fit:** the same `_chuckFinder` → `ComputeCentre` → Pratt `CircleFit` → persist
  `ChuckCenterX/Y` + `ChuckRadius`.
- **Motion:** `IMotionHost.MoveToAsync` (i.e. `FrmMain`), the single motion entry point, so the
  bounds-check and the Y user↔raw flip are never re-implemented.
