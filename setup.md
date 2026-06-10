# Nanotec Controller — EtherCAT Setup & Connection Guide

How to connect this application to a Nanotec drive over **EtherCAT**, and how to
confirm — at every layer — that it is *fully* connected before commanding motion.

This guide is cross-checked against the official Nanotec example
(`NanoLib_csharp_1.4.0/NanolibExample/`), in particular
`BusFunctionsExample.cs`, `DeviceFunctionsExample.cs`, and
`MotorFunctionsExample.cs`.

> **Safety first.** Commission with **no wafer loaded** and a hand on the e-stop.
> See the companion machine checklist before the first powered move.

---

## 1. Prerequisites (PC / master side)

| Requirement | Why | How to confirm |
|---|---|---|
| **x64 process** | `nanolibm_ethercat.dll` is x64-only | `Program.cs` already aborts if `!Environment.Is64BitProcess` |
| **Npcap (WinPcap-compatible mode)** | EtherCAT master sends raw Ethernet frames via the packet-capture driver | Without it, no EtherCAT NIC appears in the scan |
| **Run as Administrator** | Raw frame capture/injection needs elevation | — |
| **Native NanoLib DLLs deployed** | `nanolibm_ethercat.dll` + friends must sit next to the exe | Check `bin\Debug\net10.0\` after build; they ship in the package's `runtimes/win-x64/native/` |
| **Dedicated NIC for EtherCAT** | Needs exclusive, real-time use of the port; **no IP address required** | Use a separate Ethernet port from your office/LAN |

**Recommended NIC tuning** (for stable cyclic operation, not strictly required to
connect): on the EtherCAT adapter, disable *Energy Efficient Ethernet*,
*Green Ethernet*, and *interrupt moderation*; optionally unbind TCP/IP.

---

## 1a. Npcap / NIC configuration (software EtherCAT master)

NanoLib's EtherCAT master is a **software master**: it uses **Npcap** to put raw
Ethernet frames (EtherType `0x88A4`) directly on the wire and read them back. It
does **not** "override" the Windows TCP/IP stack — EtherCAT doesn't use TCP/IP at
all; Npcap simply **bypasses** the OS networking stack for raw Layer-2 I/O. This
is the standard way a soft EtherCAT master runs on Windows.

### Npcap install options (these gate whether it works at all)
- [ ] **"Install Npcap in WinPcap API-compatible Mode"** — *enabled*. NanoLib's
      native master is built against the libpcap-style API; without this the
      EtherCAT adapter may not appear in `listAvailableBusHardware()`.
- [ ] **"Restrict Npcap driver's access to Administrators only"** — if enabled,
      the app **must run elevated** (Administrator) or the open/scan fails.
- [ ] The **Npcap Loopback Adapter** may show up in the adapter list; harmless
      here because the app filters on `BUS_HARDWARE_ID_PROTOCOL_ETHERCAT`.
- [ ] Npcap **service running** (`sc query npcap`); no NIC enumerates if it isn't.

### NIC binding & tuning (do this on the EtherCAT adapter only)
- [ ] **Dedicated port** — Ensure that the ethernet cable is the only ethernet connection to the machine
- [ ] **No IP address / disable DHCP** — EtherCAT needs no IP  
      1. ```Win + R```, type ```ncpa.cpl```  
      2. Right click ethernet adapter and select Properties.  
      3. Uncheck both IPv4 and IPv6  
      4. Ensure Npcap Packet Driver is checked
- [ ] **Disable offload/power features**: Navigate to Ethernet adapter again  
      1. **Properties -> Configure**  
      2. Under **Power Management**, uncheck "Allow the computer to turn off this device to save power"  
      3. Under **Advanced tab** disable **Energy Efficient Ethernet (EEE)**, **Green Ethernet**, or **Interrupt Moderation**

### Timing expectations & robustness
- **For this app (acyclic SDO only):** soft Npcap timing is fully adequate.
  SDO is request/response with no deadline; the 50 ms status poll is fine.
- **A software master is NOT real-time.** A frame can occasionally be lost or
  arrive late under Windows scheduling. NanoLib retries internally per its
  EtherCAT bus options (see below). The app's live-readout loop also tolerates a
  few consecutive failed reads before escalating to safe shutdown
  (`MAX_CONSECUTIVE_READ_FAILURES` in `Program.cs`) — a single hiccup shows a
  transient "link hiccup" indicator instead of aborting.
- **If you later add cyclic PDO / the Sampler:** revisit this seriously. Missed
  cycles drop slaves from OPERATIONAL back to SAFE-OP (watchdog ▸ drive stops),
  and **Distributed Clocks (DC) sync is effectively unavailable** with a pure soft
  master. A single velocity-jogged chuck does not need DC.

### Tunable EtherCAT bus options (from `EtherCATBus.cs`)
The app currently passes a bare `new BusHardwareOptions()` (NanoLib defaults). If
you see spurious comm errors under load, add options via `busHwOptions.addOption(...)`:
- `READ_TIMEOUT_OPTION_NAME` / `WRITE_TIMEOUT_OPTION_NAME` — per-transfer timeout.
- `READ_WRITE_ATTEMPTS_OPTION_NAME` — retry count before an error surfaces (raise this first).
- `CHANGE_NETWORK_STATE_ATTEMPTS_OPTION_NAME` — retries for state transitions.
- `EXCLUSIVE_LOCK_TIMEOUT` / `SHARED_LOCK_TIMEOUT` — multi-process/master locking.
- `PDO_IO_ENABLED_OPTION_NAME` — only relevant if/when you enable cyclic PDO.

### Other Npcap-specific gotchas
- [ ] **Co-running capture tools** (Wireshark) on the same adapter to *observe* is
      generally OK (Npcap supports multiple read handles). A second *master* or any
      tool that **injects** will conflict — NanoLib expects to own frame I/O.
- [ ] **Endpoint security / firewall** products occasionally block raw sockets; if
      `openBusHardwareWithProtocol` fails inexplicably, check these.

---

## 2. Physical connection

1. **Power the drive.** Most Nanotec drives have separate **logic** and **motor**
   supplies — apply at least logic power or the EtherCAT interface won't enumerate.
   Confirm the power LED.
2. **Cable into the correct port.** EtherCAT is directional: PC NIC → drive's
   **ECAT IN** (labelled `IN` / `X1` / inward arrow). Daisy-chain
   `OUT → next IN`. **Wrong IN/OUT is the #1 cause of "scan finds nothing."**
3. **Point-to-point cable** PC ↔ drive for bring-up. Industrial Cat5e/6.
   *Do not* use a regular office switch — standard switches break EtherCAT.
4. **Check link LEDs** on both the NIC and the drive's ECAT-IN port. No link =
   physical problem; fix before touching software.

---

## 3. Software connection sequence

This is the ladder the app climbs. **Each rung is a gate** — if it fails, the
ones above are meaningless. (Method names are the actual NanoLib API; see the
`Program.cs` flow and `BusFunctionsExample.cs` / `DeviceFunctionsExample.cs`.)

| # | Step | API call | Confirms |
|---|------|----------|----------|
| 1 | Init master | `Nanolib.getNanoLibAccessor()` | NanoLib loaded |
| 2 | List adapters | `listAvailableBusHardware()` | OS sees an interface |
| 3 | **Pick EtherCAT adapter** | filter on `getProtocol() == Nanolib.BUS_HARDWARE_ID_PROTOCOL_ETHERCAT` | Right NIC chosen (not Wi-Fi/VPN/loopback) |
| 4 | **Open the bus** | `openBusHardwareWithProtocol(adapter, options)` | Master has exclusive control of the NIC |
| 5 | **Scan for drives** | `scanDevices(adapter, callback)` returns ≥1 | Frames reach a drive and return |
| 6 | Register | `addDevice(deviceId)` → `DeviceHandle` | Drive is in the master's table |
| 7 | **Connect** | `connectDevice(handle)` no error | Logical link up; SDO/mailbox usable |

### Bus-hardware options for EtherCAT
The official helper `BusHardwareOptionsHelper.CreateBusHardwareOptions()` adds
options **only for CANopen (baud rate) and Modbus RTU (baud/parity)**. For
**EtherCAT it returns an empty `BusHardwareOptions`** — so the app's
`new BusHardwareOptions()` is correct as-is. No EtherCAT-specific options needed.

### EtherCAT state machine — do I need to force OPERATIONAL?
EtherCAT slaves climb `INIT → PRE-OP → SAFE-OP → OPERATIONAL`.
- **For this app (SDO/mailbox only):** **No manual action needed.** The official
  example never calls `setBusState`/`setDeviceState` and still does SDO
  read/writes right after `connectDevice` — NanoLib brings the slave up far
  enough (PRE-OP+) automatically. Mailbox/SDO works from PRE-OP up.
- **OPERATIONAL is required only for cyclic PDO I/O** (the `PDO_IO_ENABLED` bus
  option) or the high-speed **Sampler**. If you add those later, drive it with
  `accessor.setBusState(adapter, new EtherCATState().OPERATIONAL)` then poll
  `getDeviceState(handle)` until it reports OPERATIONAL.

---

## 4. How to know it is FULLY connected

"No error from `connectDevice`" is necessary but **not** sufficient. Check all
three independent layers. (The example's `GetConnectionState`,
`GetDevice*` identity reads, and `GetErrorFields` mirror these checks.)

### Layer 1 — Connection state (is the link alive *now*?)
```csharp
var conn = accessor.getConnectionState(handle).getResult();   // cached
// var conn = accessor.checkConnectionState(handle).getResult(); // active re-probe
```
- `Connected` ✅ usable
- `Disconnected` ❌ link/power/cable problem
- `ConnectedBootloader` ⚠️ **drive booted into bootloader (firmware-update mode)**,
  not the application — answers pings but won't run motion. **Power-cycle the drive.**

Use `checkConnectionState` when you want the truth right now (it re-probes).

### Layer 2 — Identity reads (is it the drive I think it is, and does it really talk?)
A successful identity read is hard proof the SDO channel works end-to-end:
```csharp
accessor.getDeviceName(handle).getResult();          // e.g. "C5-E-2-09"
accessor.getDeviceVendorId(handle).getResult();
accessor.getDeviceProductCode(handle).getResult();
accessor.getDeviceSerialNumber(handle).getResult();
accessor.getDeviceFirmwareBuildId(handle).getResult();
```
Log these on connect. On a multi-drive line, match vendor/product/serial to the
**intended** axis instead of trusting scan order (`device[0]`).

### Layer 3 — Error stack (is the drive itself faulted?)
Read the error count and decode any entries (object `0x1003`):
```csharp
long count = accessor.readNumber(handle, /* 0x1003:00 */ OdErrorCount).getResult();
// for i in 1..count: readNumber(handle, 0x1003:i) and decode number/class/code
```
A connected-but-faulted drive will refuse to enable. The example's
`GetErrorFields` shows the decode pattern.

### (Separate) Layer 4 — CiA 402 drive state (is the *motor* ready?)
This is **not** "connection" — it's readiness to move, handled by
`ChuckController.EnableDrive`. Even fully connected + OPERATIONAL, the motor
won't move until the statusword reaches **Operation Enabled** (`& 0x6F == 0x27`).

**Bottom line — "fully connected" =** Layer 1 `Connected` **+** Layer 2 identity
reads succeed **+** Layer 3 error stack clear. Then proceed to enable/home.

---

## 5. One-time / per-session drive preparation

Discovered from `MotorFunctionsExample.cs` — do these or motion may misbehave:

1. **Stop any running NanoJ program before commanding motion.** Every motion
   routine in the example first does:
   ```csharp
   accessor.writeNumber(handle, 0x00, /* 0x2300:00 */ OdNanoJControl, 32);
   ```
   A NanoJ program running on the drive can fight the host's controlword writes.
   *(The app does not currently do this — add it before `EnableDrive` if a NanoJ
   program may be present.)*

2. **Run motor Auto-Setup once for a new motor/encoder/drive combination**
   (mode of operation `0xFE`, started via controlword `0x06 → 0x07 → 0x0F → 0x1F`,
   wait until statusword `& 0x1237 == 0x1237`, then reboot). Closed-loop modes and
   homing only behave correctly **after** auto-setup has been run once and saved.
   Use the Nanotec example/Plug&Drive Studio for this, or port `MotorAutoSetup`.

3. **Verify velocity scaling — this matters for safety.** The example's Profile
   Velocity demo writes `0x3C` (= **60**) with the comment *"desired speed in
   rpm (60)"*. So the default Target Velocity (`0x60FF`) unit is **rpm**.
   This app's `JOG_VELOCITY = 1500` therefore likely means **~1500 rpm**, which is
   *very* fast for a wafer chuck. Before first motion:
   - Confirm the scaling/factor-group objects (`0x6091`–`0x6096`) on your drive.
   - Set `JOG_VELOCITY` to a deliberately low, validated value (start small).

---

## 6. Connection self-test (recommended addition to `Program.cs`)

Drop this in right after `connectDevice` succeeds, **before** `EnableDrive`, and
route a failure through the existing safe-teardown:
```csharp
var conn = accessor.getConnectionState(handle).getResult();
Console.WriteLine($"Connection state : {conn}");
Console.WriteLine($"Device           : {accessor.getDeviceName(handle).getResult()}");
Console.WriteLine($"Firmware build   : {accessor.getDeviceFirmwareBuildId(handle).getResult()}");
Console.WriteLine($"EtherCAT state   : {accessor.getDeviceState(handle).getResult()}");
if (conn != DeviceConnectionStateInfo.Connected)
{
    Console.WriteLine("Not fully connected — aborting before enabling drive.");
    return; // finally block runs safe shutdown
}
// (optional) read 0x1003:00 error count and abort if > 0
```

---

## 7. Quick troubleshooting

| Symptom | Most likely layer / cause |
|---|---|
| No NIC in `listAvailableBusHardware` | Npcap missing, not x64, or not elevated |
| `openBusHardwareWithProtocol` errors | NIC already in use / wrong adapter / driver |
| Scan returns 0 devices | Cable in wrong port (IN vs OUT), drive unpowered, no link LED |
| Connected but reads error out | Slave stuck in INIT, or link dropped |
| `ConnectedBootloader` | Drive in firmware-update mode — power-cycle |
| Connected, PRE-OP, but motor won't move | CiA 402 not at Operation Enabled, or drive in Fault (check `0x1003`) |
| Motor moves unexpectedly fast | Velocity unit is rpm — `JOG_VELOCITY` too high |
| Commands ignored / fought | A NanoJ program is running — write `0` to `0x2300` |

---

## 8. Reference — official example files

Located in `NanoLib_csharp_1.4.0/NanolibExample/`:

- **`BusFunctionsExample.cs`** — `ScanBusHardware`, `OpenBusHardware`,
  `CloseBusHardware` (bus open/close flow + options helper).
- **`DeviceFunctionsExample.cs`** — `ScanDevices`, `ConnectDevice`,
  `DisconnectDevice`, `GetConnectionState`, `GetErrorFields`, identity getters,
  `RebootDevice`, `RestoreDefaults`, firmware/NanoJ functions.
- **`MotorFunctionsExample.cs`** — `MotorAutoSetup`, `ExecuteProfileVelocityMode`
  (enable sequence, rpm unit, NanoJ stop).
- **`MenuUtils.cs`** — `BusHardwareOptionsHelper.CreateBusHardwareOptions`
  (confirms EtherCAT needs no extra options).
- **`Example.cs`** — overall menu flow and Ctrl+C handler registration.
