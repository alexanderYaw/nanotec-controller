---
title: Automated Chuck Centre-Finding
---

# Automating Chuck Centre-Finding

The manual flow (see the **[Chuck Center-Finding Analysis](ChuckCenterFindingAnalysis/)**)
has the operator jog the rim onto the crosshair at several spots, capturing one rim point
each time, then circle-fits them. This page describes how to **automate the point
collection**: the operator roughly centres the chuck once, and the stage then drives itself
outward in several directions, detecting a rim point in each, and fits the centre — with no
per-point jogging.

> **Frame.** As in the analysis page, every detected edge pixel becomes the stage position
> that would bring it onto the fixed crosshair,
> $$\mathbf{E} = \mathbf{M} + A\,(\mathbf{p}_\text{cross} - \mathbf{p}_\text{edge}),$$
> with $\mathbf{M}$ the stage position at capture and $A$ the calibrated pixel$\rightarrow$step
> affine. The collected $\mathbf{E}$ all lie on the chuck's rim circle; fitting it (Pratt)
> gives the centre.

## The core idea

The operator's proposed loop is:

1. Manually jog the chuck to roughly the centre.
2. Automatically jog outward until an edge is detected.
3. Repeat in several unique directions.

This is the right shape, with two adjustments below.

### Detect *in frame*, not *on the crosshair*

`ChuckEdgeDetector.TryDetect(frame, crossRow, crossCol)` returns the rim point **nearest the
crosshair as soon as the rim is anywhere in the field of view** — it does not require the edge
to sit *on* the crosshair. `OnEdgeGrabbed` then converts that pixel to a step-space rim point
through the affine above. So "jog until an edge is detected" means **until the rim enters the
frame**, and the moment `TryDetect` succeeds you already have a valid rim point. No null-ing
servo loop is needed — each direction is a plain *coarse-step-until-detected* scan.

> For maximum optical accuracy you may add a few fine steps to bring the detected point closer
> to the crosshair before recording, since the affine and lens are most trustworthy near
> centre. This is an optional refinement, not a requirement.

### Command absolute targets, not a continuous jog

Because the soft master can't cleanly stop a continuous move on a vision trigger, and because
motion here is **step-and-settle**, each outward move should be an **absolute computed target**:

$$\mathbf{target}_k = \mathbf{centre} + \hat{\mathbf{d}} \cdot (k \cdot \Delta s),$$

for step index $k = 1, 2, \dots$, heading unit vector $\hat{\mathbf{d}}$, and step size
$\Delta s$. After each move: settle, grab a frame, run `TryDetect`. This fits the architecture,
removes stop-latency, and — critically — makes the travel bound *inherent*: you never
**command** a position beyond the guard.

## The per-direction scan

For each of $N$ headings (evenly spaced angles in motor-XY space):

```
for each heading d̂ (evenly spaced around 360°):
    move to rough centre                 # each direction independent; no accumulated travel
    for k = 1, 2, … until guard:
        target = centre + d̂ · (k · Δs)
        if target outside host soft-limit envelope: break        # safety (see below)
        MoveAbsolute(target); settle
        frame = grab()
        if TryDetect(frame) succeeds and point passes sanity check:
            record rim point via _chuckFinder.Add(...)
            break                          # got this direction's point
    # if guard reached with no detection: skip this direction
fit centre with Pratt CircleFit over the collected points   # need ≥ 3
```

Returning to the rough centre before each direction keeps the scans independent and stops
travel from creeping toward a limit. Always approaching each rim point **outward** also keeps
backlash consistent across all points.

## Safety — the constraint that shapes the design

The hardware offers **no protection against a runaway outward scan**: X's **+end limit switch
is dead**, **Z has none**, and the drives' soft limits (`0x607D`) read a fake $\pm9999999$. If
the rough centre is off, or a direction never sees an edge, an unbounded scan can drive **into
a hard stop** (worst on X+). Therefore:

- **Per-direction max-travel guard.** Size it from the *known nominal chuck radius* plus a
  margin (e.g. $R \cdot 1.3$). No detection within that distance → abort the direction. This is
  the primary crash guard.
- **Host-side soft position limits.** Because the drive's limits are fake, the **application**
  must clamp every $\mathbf{target}_k$ to a safe XY envelope before commanding it. Never rely on
  the drive to stop you.
- **False-positive rejection.** Accept a detection only if the point's distance from the rough
  centre falls in the expected band $R \pm \text{tol}$; a stray ridge detected mid-travel would
  otherwise poison the fit. Requiring detection on **two consecutive frames** further hardens
  this.
- **Enough points.** The fit needs $\ge 3$; with $N = 7$ attempts you can tolerate a couple of
  skipped directions.

## Parameters

| Parameter | Meaning | Guidance |
|---|---|---|
| $N$ (directions) | How many outward scans | $N = 7$ is fine; **even angular spacing** (~51° apart) matters more than the count for a well-conditioned fit. $\ge 5$ around 360° is plenty. |
| $\Delta s$ (step size) | Outward move per check | A fraction of the field of view (convert the frame height to steps via $A$, take ~⅓–½) so consecutive frames overlap and the rim can't be **skipped** between checks. |
| $R$ (expected radius) | Feeds the guard + sanity band | From the last saved `ChuckCenterX/Y` fit, or a machine config constant. |

## Preconditions

- **Pixel↔step affine calibrated** ($A$) — the conversion and the whole centre-find depend on it.
- **Z / focus set** — the detector keys on a *focus* ridge; at the wrong Z the ridge never forms
  and nothing detects. Rough-centring must include getting focus right.
- **Rough centre** provided by the operator (step 1).

## Where it fits in the code

The building blocks already exist in `Vision/FrmVisionProtocols` and its partials:

- **Grab + detect + convert:** `RequestEdge` / `OnEdgeGrabbed` already grab a frame on the grab
  thread, run `ChuckEdgeDetector`, and convert the pixel to a step-space rim point via the affine.
- **Point accumulation + fit:** `CentreFinder` (`_chuckFinder`) collects points; `ComputeCentre`
  runs the Pratt `CircleFit` and persists `ChuckCenterX/Y`.
- **Motion:** absolute moves go through `FrmMain` (`_owner.MoveToAsync` / `MultiAxisController`) —
  the single motion entry point.

The automation is therefore an **orchestration layer**: a new async routine that loops the
headings, issues the guarded absolute moves, reuses `RequestEdge`-style grab/detect, adds
accepted points to `_chuckFinder`, and finally calls the existing compute-and-save. No change to
the detector or the fit is required.

## Reliability extension

The single-pass result can be strengthened exactly as the analysis page's **concentric-rings**
method (§6 there) prescribes: run the whole $N$-direction scan at a **different radius** twice
more and average the three fitted centres, cutting the centre's standard error by
$\approx 1/\sqrt{3}$. In automation terms this is just an outer loop over a few guard/step
radii. The **rotational-invariance test** (§7 there) then validates the averaged centre against
the physical rotation axis.
