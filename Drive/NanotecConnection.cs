using System;
using Nlc;

namespace MotorControlApp
{
    /// <summary>
    /// Encapsulates the EtherCAT bring-up sequence (scan → open → scan devices →
    /// add → connect → verify) so it can be driven from a GUI or console without
    /// duplicating the NanoLib calls. Each step is reported via an
    /// <see cref="IProgress{String}"/> so the UI can show the connection process.
    ///
    /// This class only *connects and verifies* - it never enables the drive or
    /// commands motion. Motion stays the responsibility of <see cref="AxisDriver"/>.
    /// </summary>
    public class NanotecConnection
    {
        private NanoLibAccessor? _accessor;
        private BusHardwareId? _adapter;
        private DeviceHandle? _handle;

        public bool IsConnected { get; private set; }
        public string AdapterName { get; private set; } = "";
        public string DeviceName { get; private set; } = "";
        public string FirmwareBuild { get; private set; } = "";

        /// <summary>The live accessor (valid once connected).</summary>
        public NanoLibAccessor? Accessor => _accessor;

        /// <summary>The connected device handle (valid once connected).</summary>
        public DeviceHandle? Handle => _handle;

        /// <summary>
        /// Runs the full connection sequence. Returns true only when the drive is
        /// verified Connected. Safe to call on a background thread; pass a
        /// <see cref="Progress{String}"/> created on the UI thread for log updates.
        /// </summary>
        public bool Connect(IProgress<string> log)
        {
            if (IsConnected)
            {
                log.Report("Already connected.");
                return true;
            }

            log.Report("Initializing NanoLib accessor...");
            _accessor = Nanolib.getNanoLibAccessor();

            log.Report("Scanning for network interfaces...");
            ResultBusHwIds hwIds = _accessor.listAvailableBusHardware();
            if (hwIds.hasError() || hwIds.getResult().Count == 0)
            {
                log.Report("ERROR: No network interfaces detected.");
                return false;
            }

            // Pick an EtherCAT-capable adapter (not Wi-Fi / VPN / loopback).
            BusHWIdVector busIds = hwIds.getResult();
            BusHardwareId? adapter = null;
            for (int i = 0; i < busIds.Count; i++)
            {
                if (busIds[i].getProtocol() == Nanolib.BUS_HARDWARE_ID_PROTOCOL_ETHERCAT)
                {
                    adapter = busIds[i];
                    break;
                }
            }
            if (adapter == null)
            {
                log.Report("ERROR: No EtherCAT-capable network interface found.");
                log.Report("       Check Npcap install and that the EtherCAT NIC is present.");
                return false;
            }
            _adapter = adapter;
            AdapterName = adapter.getName();
            log.Report($"Using adapter: {adapter.getName()} [{adapter.getProtocol()}]");

            log.Report("Opening bus hardware...");
            ResultVoid open = _accessor.openBusHardwareWithProtocol(adapter, new BusHardwareOptions());
            if (open.hasError())
            {
                log.Report($"ERROR: Failed to open adapter: {open.getError()}");
                _adapter = null;
                return false;
            }

            log.Report("Scanning EtherCAT line for drives...");
            ResultDeviceIds devices = _accessor.scanDevices(adapter, null);
            if (devices.hasError() || devices.getResult().Count == 0)
            {
                log.Report("ERROR: No EtherCAT drives responded. Check cabling (IN vs OUT) and power.");
                _accessor.closeBusHardware(adapter);
                _adapter = null;
                return false;
            }

            // Register the first device found. Multi-drive lines should select by id.
            DeviceId dev = devices.getResult()[0];
            log.Report($"Found device: {dev.getDescription()} (id {dev.getDeviceId()})");

            ResultDeviceHandle add = _accessor.addDevice(dev);
            if (add.hasError())
            {
                log.Report($"ERROR: addDevice failed: {add.getError()}");
                _accessor.closeBusHardware(adapter);
                _adapter = null;
                return false;
            }
            DeviceHandle handle = add.getResult();

            log.Report("Connecting to drive...");
            if (_accessor.connectDevice(handle).hasError())
            {
                log.Report("ERROR: connectDevice failed.");
                _accessor.removeDevice(handle);
                _accessor.closeBusHardware(adapter);
                _adapter = null;
                return false;
            }
            _handle = handle;

            // Verify: connection state + identity reads (proves the SDO channel works).
            log.Report("Verifying connection...");
            ResultConnectionState connResult = _accessor.getConnectionState(handle);
            if (connResult.hasError())
            {
                log.Report($"ERROR: connection check failed: {connResult.getError()}");
                return false;
            }

            DeviceConnectionStateInfo conn = connResult.getResult();
            log.Report($"Connection state: {conn}");
            if (conn != DeviceConnectionStateInfo.Connected)
            {
                log.Report(conn == DeviceConnectionStateInfo.ConnectedBootloader
                    ? "ERROR: Drive is in BOOTLOADER mode (firmware-update). Power-cycle and retry."
                    : "ERROR: Drive is not connected. Check power and cabling.");
                return false;
            }

            ResultString nameResult = _accessor.getDeviceName(handle);
            ResultString fwResult = _accessor.getDeviceFirmwareBuildId(handle);
            if (nameResult.hasError() || fwResult.hasError())
            {
                log.Report("ERROR: identity read failed - SDO channel not usable.");
                return false;
            }

            DeviceName = nameResult.getResult();
            FirmwareBuild = fwResult.getResult();
            log.Report($"Device   : {DeviceName}");
            log.Report($"Firmware : {FirmwareBuild}");
            log.Report("Connection verified. Drive is DISABLED (no motion).");

            IsConnected = true;
            return true;
        }

        /// <summary>Releases the device and bus. Safe to call when not connected.</summary>
        public void Disconnect(IProgress<string> log)
        {
            if (_accessor == null)
            {
                IsConnected = false;
                return;
            }

            try
            {
                DeviceHandle? handle = _handle;
                if (handle != null)
                {
                    log.Report("Disconnecting device...");
                    _accessor.disconnectDevice(handle.Value);
                    _accessor.removeDevice(handle.Value);
                    _handle = null;
                }

                BusHardwareId? adapter = _adapter;
                if (adapter != null)
                {
                    log.Report("Closing bus hardware...");
                    _accessor.closeBusHardware(adapter);
                    _adapter = null;
                }

                log.Report("Disconnected.");
            }
            catch (Exception ex)
            {
                log.Report($"Disconnect error: {ex.Message}");
            }
            finally
            {
                IsConnected = false;
                DeviceName = "";
                FirmwareBuild = "";
                AdapterName = "";
            }
        }
    }
}
