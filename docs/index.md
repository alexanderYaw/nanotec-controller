---
title: Home
---

# Nanotec Inspection-Table Controller

Documentation for the multi-axis motion application that drives the wafer
inspection table's four EtherCAT axes — **X, Y, Z, and Θ (the rotary chuck)** —
through Nanotec drives using **NanoLib** over **EtherCAT (CoE / CiA 402)** with
an **Npcap soft master**, plus a **HALCON** camera for the vision-guided
calibration, centre-finding, and crosshair-pinned rotation.

## Guides

* **[User Guide](user-guide/)** — operator instructions: jogging and the RAW/VISION
  mode switch, the joystick, homing and calibration, the position map, the camera
  and vision protocols, relative moves, and the parameters window.
  ([PDF version](user-guide/Wafer%20Inspection%20Workstation%20User-Guide.pdf) — this
  export predates the vision features; the web version is authoritative.)
* **[Developer Guide](developer-guide/)** — how the application is built and how
  each feature works internally: architecture, the drive layer, the soft-limit guard,
  rotation, and vision.
* **[EtherCAT Setup](setup/)** — connecting the application to a drive, and
  verifying the connection at every layer before commanding motion.

## Design notes

* **[Chuck Center-Finding Analysis](developer-guide/ChuckCenterFindingAnalysis/)** —
  the three circle-fit methods, why **Pratt** won, and the error behaviour of each.
* **[Automated Chuck Centre-Finding](developer-guide/ChuckCenterFindingAutomation/)** —
  why the automatic rim-point collection is shaped the way it is: step-and-settle probes,
  bisect-then-diagonals, and the safety guards that stand in for the missing limit switches.
