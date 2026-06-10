# Developer & Operational Guide: Low-Level Chuck Control System

This user guide documents the C# implementation designed to control the high-precision rotating chuck of a wafer inspection station. The system leverages Nanotec’s **NanoLib** library to communicate over **EtherCAT** via the **CoE (CANopen over EtherCAT) CiA 402** drive profile.

---

## 1. System Requirements & Prerequisites

To successfully build and run this application, the host computer and physical workspace must meet the following configuration parameters:

### Software Requirements
* **.NET SDK (v8.0 or later):** Required to compile and build the codebase toolchain.
* **Npcap (v1.88 or later):** Must be installed with the **"Install Npcap in WinPcap API-compatible Mode"** checkbox selected. This allows the underlying native NanoLib binaries (`wpcap.dll` hooks) to execute raw packet injection bypassing the Windows TCP/IP stack.
* **Nanotec NanoLib Package:** The `nanotec.services.nanolib` package must be present in a local repository and declared inside the `nuget.config` file.

### Project Layout & Architecture Target
* **Target Architecture:** The project file (`.csproj`) **must** be explicitly compiled for **`x64`** platforms. NanoLib's underlying hardware driver libraries (`nanolibm_ethercat.dll`) are strictly 64-bit binaries. Running under `Any CPU` will trigger a runtime `BadImageFormatException`.
* **File Separation:** The implementation utilizes standard C# modular architecture where files belong to the unified `namespace MotorControlApp`:
  * `Program.cs`: Acts as the orchestrator, executing network configuration scans, safe teardowns, and the interactive terminal polling loop.
  * `ChuckController.cs`: Encapsulates low-level object dictionary read/write primitives, CiA 402 state machine transitions, and encoder calibration logic.

### Physical Interconnects
1. An Ethernet cable must connect directly from the PC’s dedicated Network Interface Card (NIC) to the **EtherCAT IN** port of the Nanotec motor controller.
2. The motor driver stage must be powered by an external source matching the voltage constraints of the specific chuck actuator.

---

## 2. Low-Level Control Architecture (`ChuckController.cs`)

The `ChuckController` class acts as an abstraction barrier between your high-level application logic and raw hardware registers.

### Checked Communication Primitives
Rather than allowing silent failures or unhandled null values, the class implements explicit wrapper methods around `_accessor.writeNumber` and `_accessor.readNumber`:

* **`Write(long value, OdIndex od, uint bitLength, string what)`**: Automatically evaluates the returned `ResultVoid` from NanoLib. If a hardware or network timeout occurs, it wraps the internal error text into a descriptive `ChuckException`.
* **`Read(OdIndex od, string what)`**: Wraps the NanoLib `ResultInt` query. If the read fail condition triggers, it aborts immediately rather than passing a silent fallback value of `0` down to precision math wrappers.

### The CiA 402 State Machine Workflow
Industrial motor controllers do not accept movement variables when powered up initially. The drive stage must step through a mandatory hardware loop. The `EnableDrive(true)` method handles this progression automatically, checking the **Statusword (0x6041)** at every step:

1. **Fault Latch Verification:** Checks bit 3 (`SW_FAULT`). If active, fires a rising edge reset pulse (`0x0080`) and waits for completion.
2. **Shutdown Command (`0x0006`):** Transitions to the *Ready To Switch On* state. The system polls the Statusword until it matches `0x0021`.
3. **Switch On Command (`0x0007`):** Transitions to the *Switched On* state. The system polls until it matches `0x0023`.
4. **Enable Operation Command (`0x000F`):** Energizes the drive loops, placing the system into the *Operation Enabled* state (`0x0027`).

---

## 3. Position Calibration & Synchronization

Wafer inspection profiles require sub-millimeter geometric accuracy. When the machine boots up, the motor encoder value does not inherently match the physical orientation of the chuck. The `SynchronizeEncoderToPhysicalZero()` routine fixes this reference constraint:

1. **Latch Home Offsets:** It resets the **Home Offset register (0x607C)** to `0` prior to initiating movement. Writing offsets post-initialization will cause the register to fail to latch.
2. **Transition to Homing Mode:** It writes a value of `6` to the **Modes of Operation (0x6060)** object.
3. **Set Method 34:** Configures **Homing Method (0x6098)** to method `34` (Home on current position / alignment indexing).
4. **Assert Strobe:** Fires the `CW_START_HOMING (0x001F)` strobe pattern (asserting Bit 4) to force the internal hardware registry to redefine its baseline.
5. **Deterministic Check:** It polls the Statusword using an extended timeout envelope (`10,000ms`). Per the strict **CiA 402 standard**, homing is validated if and only if **both** `SW_HOMING_ATTAINED` (Bit 12) and `SW_TARGET_REACHED` (Bit 10) evaluate to true.

### Precision Scaling Constants
The actual angle computation maps encoder counts to a localized 360-degree floating-point coordinate space using the tracking variable:

$$\text{Angle} = \left(\frac{\text{Raw Ticks} \pmod{40000}}{40000}\right) \times 360.0^\circ$$

The constant `ENCODER_TICKS_PER_REV = 40000` defines the total quadrature resolution boundaries of your chuck assembly. If the readout counts downwards during a clockwise manual rotation, the internal scaling inversion can be applied directly by updating the hardware **Polarity Object (0x607E)**.

---

## 4. Manual Operation Framework (`Program.cs`)

The high-level UI layer processes asynchronous keyboard interaction from a user and pipes commands down into velocity vectors. 

### Operational Key Mappings
When running the system in terminal mode via `dotnet run`, the following live keyboard mapping binds to the active execution context:

| Input Key | Command Action | Underlying Hardware Vector Passed |
| :--- | :--- | :--- |
| **Right Arrow** | Continuous Clockwise Spin | Modes of Op $\rightarrow$ `3` (Velocity), Target Velocity $\rightarrow$ `1500` |
| **Left Arrow** | Continuous Counter-Clockwise Spin | Modes of Op $\rightarrow$ `3` (Velocity), Target Velocity $\rightarrow$ `-1500` |
| **Spacebar** | Immediate Motion Halt | Target Velocity $\rightarrow$ `0`, Controlword $\rightarrow$ `0x010F` (Halt bit 8) |
| **Escape** | Graceful Disconnect & Exit | Break tracking loop $\rightarrow$ Execute Teardown sequence |

*Note: The jog magnitude (`1500`) relies heavily on your driver's internal factor-group configuration (Objects 0x6091-0x6096). Verify your gear ratio scaling profiles if the physical rotation speed doesn't align with software expectations.*

### Safe Intercept Handling (Ctrl+C & Fault Recovery)
Abruptly killing a command-line utility controlling heavy mechanical hardware can leave an active induction loop energized or spin a motor out of bounds. To circumvent this, `Program.cs` implements a hardware protection lifecycle wrapper:

1. **`Console.CancelKeyPress` Interception:** If a user triggers a termination shortcut (`Ctrl+C`), the handler cancels the immediate operating system abort signal (`e.Cancel = true`) and raises an internal thread flag `_exitRequested = true`.
2. **Falling Through to `finally`:** The manual loop drops naturally into a structural `finally` teardown block.
3. **Safe Shutdown Sequence:**
   * `chuck?.StopManualJog()` is immediately asserted to decelerate rotation down to zero velocity.
   * `chuck?.EnableDrive(false)` drops the Controlword to `0x0000`, removing bias voltage from the driver stage and allowing the chuck to enter a low-energy state safely.
   * `TeardownHardware()` releases the logical device maps, frees memory handles, and closes the physical network sockets gracefully.

---

## 5. API Reference & Status Monitoring

The application aggregates tracking telemetry via the `GetStatus()` method, returning an atomic, immutable snapshot structure:

```csharp
public readonly struct ChuckStatus
{
    public double AngleDegrees { get; init; } // Normalized orientation value (0.00° - 359.99°)
    public string State { get; init; }        // Decoded string variant ("Ready", "Operation Enabled", etc.)
    public bool HasFault { get; init; }       // Active error latch flag read directly from hardware
}