---
title: Chuck Center-Finding Analysis
---

# Deriving the Geometric Centre of the Chuck

Four ways to recover a circle's centre from sampled rim points, a recommendation for
production, and the sampling/validation steps that make the result repeatable.

> **Frame.** All points $(x, y)$ are **motor-step** coordinates, not pixels. Each
> detected edge pixel is converted to the stage position that would bring it onto the
> fixed crosshair,
> $$\mathbf{E} = \mathbf{M} + A\,(\mathbf{p}_\text{cross} - \mathbf{p}_\text{edge}),$$
> with $\mathbf{M}$ the current stage position and $A$ the calibrated $2\times2$
> pixel$\rightarrow$step affine. Every rim point sits at radius $R$ from the chuck
> centre, so the $\mathbf{E}$ values lie on a circle whose centre is the stage
> position that puts the *chuck* centre on the crosshair — the quantity we want.

## 0. Problem setup

Given $n \ge 3$ points $\{(x_i, y_i)\}$ nominally on a circle, find $(a, b)$ and $R$.
Two equivalent forms (Geometric and ):

$$(x - a)^2 + (y - b)^2 = R^2
\qquad\Longleftrightarrow\qquad
A\,(x^2 + y^2) + B\,x + C\,y + D = 0,$$

with $a = -B/2A$, $b = -C/2A$, $R = \sqrt{B^2 + C^2 - 4AD}\,/\,2\lvert A\rvert$.
The methods differ in how they define "best fit" and — for the algebraic ones — how
they fix the arbitrary scale of $(A, B, C, D)$, since any nonzero multiple is the
same circle.

## 1. Exact Interpolation (exact solution from 3 points)

> This method utilises the proprty of equidistance to construct a small system of
> **simultaneous linear equations**.

This method uses the fact that: **the centre is the same distance from every point
on the circle.**

Substituting the $(x, y)$ coordinates of $P_1$, $P_2$ and $P_3$ into the geometric form
of the equation of the circle will yield identical $R^2$. This gives us 3 **simultaneous equations**
$$1.\space \space (x_1 - a)^2 + (y_1 - b)^2 = R^2$$
$$2. \space \space (x_2 - a)^2 + (y_2 - b)^2 = R^2$$
$$3. \space \space (x_3 - a)^2 + (y_3 - b)^2 = R^2$$

### Closed form (as used in the visualiser)

`CenterFindingVisualiser.html` implements exactly this construction - combining the simultaneous equations
$1,2$ and $1,3$ leaves two linear equations:

$$
\begin{aligned}
2(x_2 - x_1)\,a + 2(y_2 - y_1)\,b &= (x_2^2 + y_2^2) - (x_1^2 + y_1^2), \\
2(x_3 - x_1)\,a + 2(y_3 - y_1)\,b &= (x_3^2 + y_3^2) - (x_1^2 + y_1^2).
\end{aligned}
$$

This is a system of 2 linear equations in the form:
$$\begin{Bmatrix} A_1a + B_1b = C_1 \\ A_2a + B_2b = C_2 \end{Bmatrix}$$
where $A_n$ , $B_n$ and $C_n$ are coefficients derived from the Point coordinates.

<br>
<br>

Solving this $2\times2$ system by Cramer's rule

$$
\begin{bmatrix}A_1 & B_1 \\ A_2 & B_2 \end{bmatrix} \begin{bmatrix}a \\ b \end{bmatrix} = \begin{bmatrix}C_1 \\ C_2 \end{bmatrix}
$$

$$
\Delta _{main} = \begin{vmatrix} 2(x_2 - x_1) & 2(y_2 - y_1) \\ 2(x_3 - x_1) & 2(y_3 - y_1) \end{vmatrix}
= 4\big[(x_2 - x_1)(y_3 - y_1) - (y_2 - y_1)(x_3 - x_1)\big].
$$

$$
\Delta _a = \begin{vmatrix}C_1 & B_1 \\ C_2 & B_2\end{vmatrix}
$$

$$
\Delta _b = \begin{vmatrix}A_1 & C_1 \\ A_2 & C_2\end{vmatrix}
$$

Finally, we derive the coordinates $(a, b)$ for the center of the circle

$$
a = \frac{(x_1^2 + y_1^2)(y_2 - y_3) + (x_2^2 + y_2^2)(y_3 - y_1) + (x_3^2 + y_3^2)(y_1 - y_2)}{\Delta /2}
$$

$$
b = \frac{(x_1^2 + y_1^2)(x_3 - x_2) + (x_2^2 + y_2^2)(x_1 - x_3) + (x_3^2 + y_3^2)(x_2 - x_1)}{\Delta /2}
$$

and the radius is simply the distance from $(a, b)$ to any of the three points,
$R = \sqrt{(x_1 - a)^2 + (y_1 - b)^2}$.


### Shortcomings

- **Uses exactly three points — no more.** This method solves for a system that is exactly determined. It cannot
  solve for a system that is overdetermined (i.e. >3 points)
- **Zero noise rejection.** Because it interpolates exactly, it fits the noise as
  faithfully as the signal — the returned circle passes *through* all three points,
  errors included. There is no residual to inspect, so a bad sample set it unidentifiable.
- **Degenerate near collinear.** If the three points are nearly in a line the bisectors
  become almost parallel and meet far away, so the centre is numerically ill-defined
  (the code guards this via $\lvert D\rvert \to 0$). This is the geometric reason rim
  points must be spread around the arc.

So it is the tool for **visualising and sanity-checking** what "the centre" means, and
the exact solver for the deliberate three-point case — but not the production
estimator, where we want all captured points to contribute and noise to average out.
For that, generalise to Section 2 and beyond.

## 2. Kåsa algebraic least-squares

Fix the scale with $A = 1$, giving residual $f_i = x_i^2 + y_i^2 + D x_i + E y_i + F$
and the objective $\Phi = \sum_i f_i^2$. Since $f_i$ is linear in $(D, E, F)$, the
normal equations are linear:

$$
\begin{pmatrix}
\sum x_i^2 & \sum x_i y_i & \sum x_i \\
\sum x_i y_i & \sum y_i^2 & \sum y_i \\
\sum x_i & \sum y_i & n
\end{pmatrix}
\begin{pmatrix} D \\ E \\ F \end{pmatrix}
= -\begin{pmatrix}
\sum x_i(x_i^2 + y_i^2) \\
\sum y_i(x_i^2 + y_i^2) \\
\sum (x_i^2 + y_i^2)
\end{pmatrix},
$$

then $a = -D/2$, $b = -E/2$, $R = \sqrt{(D^2 + E^2)/4 - F}$.

> Centre the points on their centroid before summing, then shift the result back.
> Motor-step coordinates are $\sim10^4$–$10^5$, so their squares dominate the sums and
> ruin the conditioning otherwise.

Linear, closed-form, fast; identical to the circumcircle at $n = 3$. Its weakness:
minimising the *algebraic* residual $f_i$ (units of distance$^2$) over-weights distant
points and **biases the radius low on short arcs**, and $A \equiv 1$ cannot represent
a line, so it degrades badly near collinear. On a **full** rim the bias largely
cancels and Kåsa is adequate — this is the current implementation.

## 3. Taubin

Same residual, but a scale constraint that makes $f_i$ an approximate *geometric*
distance. Using $\text{dist} \approx f_i / \lVert\nabla f_i\rVert$ with
$\nabla f_i = (2Ax_i + B,\ 2Ay_i + C)$, Taubin normalises the mean squared gradient:

$$\min \sum_i f_i^2 \quad\text{s.t.}\quad
\frac{1}{n}\sum_i \big[(2Ax_i + B)^2 + (2Ay_i + C)^2\big] = 1.$$

With $\mathbf{a} = (A, B, C, D)^\mathsf{T}$, $\mathbf{z}_i = (x_i^2 + y_i^2, x_i, y_i, 1)$,
$M = \sum_i \mathbf{z}_i \mathbf{z}_i^\mathsf{T}$, and $N$ the gradient constraint
matrix, this is the generalised eigenproblem $M\mathbf{a} = \lambda N\mathbf{a}$,
taking the smallest $\lambda \ge 0$. In practice it reduces to the smallest positive
root of a **cubic** (Newton from $0$, a few steps), reusing Kåsa's moments plus
$\sum z_i^2$. **Essentially unbiased, near the geometric optimum, no initial guess.**

## 4. Pratt

Fix the scale with $B^2 + C^2 - 4AD = 1$, which equals $4A^2R^2$ — translation/rotation
invariant and finite as $A \to 0$, so a line is the limiting case rather than a
singularity. The constraint matrix is

$$N_\text{Pratt} =
\begin{pmatrix} 0 & 0 & 0 & -2 \\ 0 & 1 & 0 & 0 \\ 0 & 0 & 1 & 0 \\ -2 & 0 & 0 & 0 \end{pmatrix},$$

and the solve is again a small generalised eigenproblem (smallest root of a
**quartic**). Accuracy is similar to Taubin, with a slight edge to Taubin in most
studies; Pratt is the more stable of the two when points span only a small arc.

## 5. Comparison and recommendation

| Method | Scale fix / criterion | Solve | $n>3$? | Arc / near-line | Accuracy |
|---|---|---|---|---|---|
| Circumcircle (bisectors) | interpolate 3 pts, $A = 1$ | $2\times2$ linear | ✗ | fails collinear; no averaging | exact at $n{=}3$; = Kåsa there |
| Kåsa | $A = 1$ | linear normal eqns | ✓ | radius biased low; poor near line | good on full rim |
| Taubin | unit mean gradient | gen. eigen (cubic) | ✓ | near-unbiased, graceful | best |
| Pratt | $B^2{+}C^2{-}4AD = 1$ | gen. eigen (quartic) | ✓ | near-unbiased, stable near line | very good |

All four approximate the **geometric fit** — the MLE under isotropic pixel noise,

$$\min_{a,b,R} \sum_i \Big(\sqrt{(x_i - a)^2 + (y_i - b)^2} - R\Big)^2,$$

which is nonlinear (Levenberg–Marquardt) and is normally **seeded with Taubin**.

**Recommendation: Taubin.** Near-unbiased and closest to the geometric optimum, no
arc bias, deterministic (one root-find, no seed), and reuses moments already computed.
It stays honest if the rim is ever sampled over a narrow angular range. The current
code uses **Kåsa**, which is fine *given* the full-rim sampling below cancels its arc
bias — but Taubin is the safer default because it does not depend on that assumption,
and swapping is a localised change to the fitting routine.

## 6. Reliability — concentric rings and averaging

One fit inherits the noise of a few detections and the calibration residual in $A$.
For repeatability, derive the centre three times from three concentric rings and
average:

1. Sample $\ge 3$ well-spread rim points at radius $R_1$; fit $\to \mathbf{C}_1$.
2. Move to a **different** radius $R_2$ (concentric ring); fit $\to \mathbf{C}_2$.
3. Repeat at $R_3$; fit $\to \mathbf{C}_3$.
4. Average: $\bar{\mathbf{C}} = \tfrac{1}{3}(\mathbf{C}_1 + \mathbf{C}_2 + \mathbf{C}_3)$.

Each ring is measured at a different stage configuration, so its detection noise,
backlash, and local calibration error are largely independent — averaging cuts the
centre's standard error by $\approx 1/\sqrt{3}$. All three are concentric about the
same physical centre, so the spread

$$\sigma_C = \max_k \lVert \mathbf{C}_k - \bar{\mathbf{C}} \rVert$$

is itself a health metric: if it exceeds a few steps, something systematic is wrong
(mis-calibrated $A$, stage non-orthogonality, drift) and the offending ring is
re-sampled before averaging.

## 7. Validation — rotational-invariance test

A circle is rotationally symmetric about its own centre, so if $\bar{\mathbf{C}}$ is
correct, a rim feature rotated by $\theta$ and then rotated back by $-\theta$ about
$\bar{\mathbf{C}}$ must return to exactly where it started.

1. Detect a reference rim feature; record $\mathbf{E}_0$.
2. Rotate the chuck by a known $\theta$ (a commanded $\Theta$ move).
3. Detect the feature again $\to \mathbf{E}_1$.
4. Require the inverse rotation about $\bar{\mathbf{C}}$ to recover the original:

$$\bar{\mathbf{C}} + R(-\theta)\,(\mathbf{E}_1 - \bar{\mathbf{C}})
\;\stackrel{!}{=}\; \mathbf{E}_0,
\qquad R(\theta) = \begin{pmatrix}\cos\theta & -\sin\theta \\ \sin\theta & \cos\theta\end{pmatrix},$$

with residual $\varepsilon = \big\lVert \bar{\mathbf{C}} + R(-\theta)(\mathbf{E}_1 - \bar{\mathbf{C}}) - \mathbf{E}_0 \big\rVert$.

A wrong centre offset by $\boldsymbol{\delta}$ gives
$\varepsilon \approx 2\lVert\boldsymbol{\delta}\rVert\,\lvert\sin(\theta/2)\rvert$, so a
larger test angle amplifies the error and sharpens the check. Crucially, this tests
the centre against the **physical rotation axis** — not the same points used to fit it
— so it catches biases the fit's RMS cannot, such as a skewed calibration $A$. Sweep a
few angles (e.g. $30°, 90°, 180°$) and require $\varepsilon$ within tolerance for all;
if it fails, the residual points toward the correction and Section 6 is repeated.

## Summary

- **Circumcircle (bisectors)** for exact 3-point visualisation — really Kåsa's $n{=}3$
  special case, no noise rejection; **Kåsa** for a fast linear fit (arc-biased, fine on
  a full rim); **Taubin**/**Pratt** for near-unbiased fits via a small eigenproblem.
- **Recommended: Taubin** — best accuracy for the effort, no arc bias, deterministic.
- **Repeatability** from three concentric-ring fits averaged, with their spread as a health check.
- **Validation** from the rotational-invariance test against the physical $\Theta$ axis.
