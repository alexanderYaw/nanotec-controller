---
title: Chuck Center-Finding Analysis
---

# Deriving the Geometric Centre of the Chuck

Three ways to recover a circle's centre from sampled rim points, a recommendation for
production, and the sampling/validation steps that make the result repeatable.

> **Frame.** All points $(x, y)$ are **motor-step** coordinates. Each
> detected edge pixel is converted to the stage position that would bring it onto the
> fixed crosshair,
> $$\mathbf{E} = \mathbf{M} + A\,(\mathbf{p}_\text{cross} - \mathbf{p}_\text{edge}),$$
> with $\mathbf{M}$ the current stage position and $A$ the calibrated $2\times2$
> pixel$\rightarrow$step affine. Every rim point sits at radius $R$ from the chuck
> centre, so the $\mathbf{E}$ values lie on a circle whose centre is the stage
> position that puts the *chuck* centre on the crosshair.

## 0. Problem setup

Given $n \ge 3$ points $\{(x_i, y_i)\}$ nominally on a circle, find $(a, b)$ and $R$.
Two equivalent forms (Geometric and algebraic):

$$(x - a)^2 + (y - b)^2 = R^2
\qquad\Longleftrightarrow\qquad
A\,(x^2 + y^2) + B\,x + C\,y + D = 0,$$

with $a = -B/2A$, $b = -C/2A$, $R = \sqrt{B^2 + C^2 - 4AD}\,/\,2\lvert A\rvert$.
The methods differ in how they define "best fit" and — for the algebraic ones — how
they fix the arbitrary scale of $(A, B, C, D)$, since any nonzero multiple is the
same circle.

## 1. Exact Interpolation (exact solution from 3 points)

> This method utilises the property of equidistance to construct a small system of
> **simultaneous linear equations**.

<iframe src="../CenterFindingVisualiser.html" width="100%" height="700px" style="border:none;">
    Your browser does not support iframes.
</iframe>


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
  errors included. There is no residual to inspect, so a bad sample set is unidentifiable.
- **Degenerate near collinear.** If the three points are nearly in a line the bisectors
  become almost parallel and meet far away, so the centre is numerically ill-defined
  (the code guards this via $\lvert D\rvert \to 0$). This is the geometric reason rim
  points must be spread around the arc.

### Considerations
**Averaging the result of sets of 3 samples.** This method allows for a sample set of >3 points, where all permutations of 3 points are considered and used to calculate their respective centers. The centers are simply averaged to produce a true center estimate. However, an unweighted center means greatly disrupt the true center calculation - near-collinear points in the sample set will greatly throw off the center averaging if left unweighted.

So it is the tool for **visualising** what "the centre" means. It is an exact solver for deliberate three-point cases — but not an accurate or reliable estimator for real-world use, where we want all captured points to contribute and noise to average out. For that, generalise to Section 2 and beyond.

## 2. Kåsa algebraic least-squares (n-generalised method)

> Recall $A\,(x^2 + y^2) + B\,x + C\,y + D = 0$

$$
A\,(x^2 + y^2) + B\,x + C\,y + D = 0
$$

$$
(x^2 + y^2) + \frac BA\,x + \frac CA\,y + \frac DA = 0
$$

$$
(x^2 + y^2) + E\,x + F\,y + G = 0
$$

The error of each sample point $i$ to the best fit circle can be modelled by $f_i = x_i^2 + y_i^2 + E\,x + F\,y + G,$ where $f_i = 0$ means that the point lies on the circle.

> To find the best fit circle, we minimise $\Phi \,,\, where\, \Phi = \sum f_i^2$

<div align="center">

<img src="../Images/sumOfPhi.jpg" alt="Model of Error Phi" style="max-width:100%;">

</div>

To minimise $\Phi ,$ we find $E,\, F,\, G$ such that $\frac {d\Phi}{dE} = \frac {d\Phi}{dF} = \frac {d\Phi}{dG} = 0$

$$
\sum f_ix_i = 0
$$

$$
\sum f_iy_i = 0
$$

$$
\sum f_i = 0
$$

<div align="center">
&#8942;
</div>

$$
\begin{pmatrix}
\sum x_i^2 & \sum x_i y_i & \sum x_i \\
\sum x_i y_i & \sum y_i^2 & \sum y_i \\
\sum x_i & \sum y_i & n
\end{pmatrix}
\begin{pmatrix} E \\ F \\ G \end{pmatrix}
= -\begin{pmatrix}
\sum x_i(x_i^2 + y_i^2) \\
\sum y_i(x_i^2 + y_i^2) \\
\sum (x_i^2 + y_i^2)
\end{pmatrix},
$$

Derive $E,\, F\, and\, G$ using Gaussian elimination, then $a = -E/2$, $b = -F/2$, $R = \sqrt{(E^2 + F^2)/4 - G}$

### Advantages
- Adding more sample points averages out the noise
- Calculates the minimum squared error across all the points

### Shortcomings
- Supplying exactly 3 points still causes overfitting by forcing the circle to fit exactly all 3 sample points
- Outliers are incorporated into the squared error, causing the plotted circle to shift significantly towards massive outliers
- Short arc bias - many points clustered around a small arc causes the prediction to systematically predict a smaller than expected radius

### Small-radius bias
> Recall $f_i = (x_i - a)^2 + (y_i - b)^2 - R^2,$ where $(x_i - a)^2 + (y_i - b)^2\,$ is the squared distance, $r_i^2$ of any point $(x,\, y)$ to the center $(a,\, b)$

$$f_i = r_i^2 - R^2$$

$$f_i=(r_i + R)(r_i - R)$$

We can assume that in a well-sampled circle, $r_i\approx R\,,$ and therefore $(r_i + R)\approx 2R$
<br>
We can also let $d_i = (r_i - R),\,$ where $d_i$ is the geometric error (distance between a point and the circumference of the circle)

$$f_i = d_i\cdot 2R$$

$$f_i^2 = 4d_i^2R^2$$

Thus, it can be seen that the algebraic error of this model is proportional to the geometric error, $d_i^2$ and the radius, $R^2$. Given 2 projected circles with identical geometric errors, this model would favour one with a smaller radius.

## 3. Pratt

### Goal
Address the small-radius bias of the Kåsa model by making approximating the algebraic error as closely to the geometric error as much as possible, $f_{pratt}\approx d_i$

> The Pratt model is simply the algebraic expression, $A\,(x^2 + y^2) + B\,x + C\,y + D$

Hence, the relationship between the Pratt model and Kåsa model is as such, $F(pratt) = A\cdot F(kåsa)$
<br>
Therefore, their algebraic errors have the following relationship as well, $f_{pratt} = A\cdot f_{kåsa}$

$$f_{pratt} = A\cdot f_{kåsa}$$

$$f_{pratt} = A\cdot (d_i\cdot 2R)$$

$$f_{pratt} = 2AR\cdot d_i$$

For $f_{pratt}\approx d_i\,,\,$ we set the condition $2AR\approx 1$
<br>
Additionally, since $A$ can be negative, the final condition should be

$$(2AR)^2\approx 1$$

$$4A^2R^2\approx 1$$

### Implementing the constraint

> Recall that $E = \frac BA\,,\, F = \frac CA\,,\, G = \frac DA$

$$4R^2 = E^2 + F^2 - 4G$$

$$4R^2 = \frac {B^2}{A^2} + \frac {C^2}{A^2} - \frac {4D}A$$

$$4R^2 = \frac {B^2 + C^2 - 4AD}{A^2}$$

$$4A^2R^2 = B^2 + C^2 - 4AD$$

$$B^2 + C^2 -4AD\approx 1$$

This can be represented in matrices

$$u^TNu\approx 1\,,$$

$$where\, N = \begin{bmatrix}0 & 0 & 0 & -2 \\ 0 & 1 & 0 & 0 \\ 0 & 0 & 1 & 0 \\ -2 & 0 & 0 & 0\end{bmatrix},\, and\, u = \begin{bmatrix}A \\ B \\ C \\ D\end{bmatrix}$$

### Solving with the Lagrange Multiplier

> We solve using the Lagrange Multiplier $(\lambda )$ to find the minimum point in our objective cost function, $\Phi_p\,,$ within the constraint bounds

$$\Phi_p = u^TMu\,,\,$$

$$where\, M = \sum_{i=0}^n \, v_i^Tv_i\,, \,and\, v_i^T = \begin{bmatrix} x_i^2 + y_i^2 \\ x_i \\ y_i \\ 1\end{bmatrix}$$

We construct the Lagrangian function

$$L(u,\, \lambda ) = u^TMu - \lambda (u^TNu - 1)$$

and find the solution for

$$\frac {dL}{du} = 2Mu - 2\lambda Nu = 0$$

$$2Mu = 2\lambda Nu$$

$$Mu = \lambda Nu$$

## 5. Comparison and recommendation

| Method | Scale fix / constraint | Solve | $n>3$? | Arc / near-line | Accuracy |
|---|---|---|---|---|---|
| Exact Interpolation | Interpolate 3 pts, $A = 1$ | $2\times2$ linear | ✗ | Fails collinear; No averaging | Exact at $n{=}3$; Accurate for 3 **perfect** sample points |
| Kåsa | $A = 1$ | Linear normal eqns | ✓ | Small-radius bias; Poor near-collinear performance | Good on full rim; Inaccurate for partial arcs and highly noisy data |
| Pratt | $B^2{+}C^2{-}4AD = 1$ | Generalised eigenvalue quadratics | ✓ | Near-unbiased; Stable near line | Provides highly stable, nearly unbiased fit regardless if the data forms a full circle or small arc |

**Recommendation: Pratt**
<br>

- **No essential bias.** Kåsa under-estimates the radius under noise even on uniform
  360° data; Pratt's constraint $B^2 + C^2 - 4AD = 1$ removes that bias, especially when there is high noise - which is to be expected when locating edge points via visual methods.
- **Robust.** Stable on flat data, partial arcs, and noisy scatter — a shallow arc is
  read as a large-radius circle instead of failing.
- **Numerically stable.** The generalised eigenvalue solve tolerates floating-point
  error where Kåsa's normal equations degrade.
- **Computationally cheap.** The eigenproblem is a fixed $4\times4$, so its cost is constant in $O(n)$.

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

<div align="center">

<img src="../Images/ResidualVisual.png" alt="Visual of Residual" style="max-width:100%;">

</div>

A wrong centre offset by $\boldsymbol{\delta}$ gives
$\varepsilon \approx 2\lVert\boldsymbol{\delta}\rVert\,\lvert\sin(\theta/2)\rvert$, so a
larger test angle amplifies the error and sharpens the check. Crucially, this tests
the centre against the **physical rotation axis** — not the same points used to fit it
— so it catches biases the fit's RMS cannot, such as a skewed calibration $A$. Sweep a
few angles (e.g. $30°, 90°, 180°$) and require $\varepsilon$ within tolerance for all;
if it fails, the residual points toward the correction and Section 6 is repeated.
