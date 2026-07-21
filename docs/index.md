---
title: Home
---

# Nanotec Inspection-Table Controller

Documentation for the multi-axis motion application that drives the wafer
inspection table's four EtherCAT axes — **X, Y, Z, and Θ (the rotary chuck)** —
through Nanotec drives using **NanoLib** over **EtherCAT (CoE / CiA 402)** with
an **Npcap soft master**.

> **Status:** this application is still being brought up on real hardware. Treat
> every first motion on a new machine as a commissioning step.

## Guides

* **[User Guide](user-guide/)** — operator instructions: jogging, homing,
  calibration, the position map, and the parameters window.
  ([PDF version](user-guide/Wafer%20Inspection%20Workstation%20User-Guide.pdf))
* **[Developer Guide](developer-guide/)** — how the application is built and how
  each feature works internally: architecture, the drive layer, rotation and
  vision.
* **[EtherCAT Setup](setup/)** — connecting the application to a drive, and
  verifying the connection at every layer before commanding motion.

## Tools

* **[Center-Finding Visualiser](developer-guide/CenterFindingVisualiser.html)** —
  interactive page for exploring circle-centre fitting from sampled edge points.
* **[Chuck Center-Finding Analysis](developer-guide/ChuckCenterFindingAnalysis/)** —
  write-up of the centre-finding approach and its error behaviour.
