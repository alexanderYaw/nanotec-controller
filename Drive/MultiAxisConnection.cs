using System;
using System.Collections.Generic;
using Nlc;

namespace MotorControlApp
{
    /// <summary>Identity read from a drive at connect — logged so a mis-cabled axis is caught.</summary>
    public readonly record struct DeviceIdentity(
        int BusPosition, string Name, string Serial, string Firmware);

    /// <summary>
    /// Multi-drive version of the EtherCAT bring-up: scans the line and connects to
    /// EVERY drive found, preserving bus (scan) order. The four inspection-table
    /// axes are then addressed by <see cref="AxisConfig.BusPosition"/>.
    ///
    /// Like <see cref="NanotecConnection"/> this only connects + verifies — it never
    /// enables a drive or commands motion. Motion is the job of the per-axis
    /// controllers built on top of <see cref="Handles"/>.
    /// </summary>
    public sealed class MultiAxisConnection
    {
        private NanoLibAccessor? _accessor;
        private BusHardwareId? _adapter;
        // Bus-hardware scan kept alive between ListBuses() and Connect()/Disconnect(): the
        // chosen BusHardwareId references it and must stay valid until closeBusHardware.
        private ResultBusHwIds? _busScan;
        private readonly List<BusHardwareId> _buses = new();
        private readonly List<DeviceHandle> _handles = new();
        private readonly List<DeviceIdentity> _devices = new();

        public NanoLibAccessor? Accessor => _accessor;

        /// <summary>Connected device handles, in EtherCAT bus order.</summary>
        public IReadOnlyList<DeviceHandle> Handles => _handles;

        /// <summary>Identity of each connected drive, in bus order (for logging / verification).</summary>
        public IReadOnlyList<DeviceIdentity> Devices => _devices;

        public bool IsConnected { get; private set; }
        public string AdapterName { get; private set; } = "";

        /// <summary>
        /// Enumerates the available bus hardware (network interfaces) so the user can pick
        /// one. The returned display strings are index-aligned with the selection passed
        /// back to <see cref="Connect"/>. Re-scannable; the result is held internally so
        /// the chosen adapter stays valid through Connect.
        /// </summary>
        public IReadOnlyList<string> ListBuses(IProgress<string> log)
        {
            log.Report("Initializing NanoLib accessor...");
            _accessor ??= Nanolib.getNanoLibAccessor();

            log.Report("Scanning for network interfaces...");
            _busScan?.Dispose();
            _buses.Clear();
            _busScan = _accessor.listAvailableBusHardware();
            if (_busScan.hasError())
            {
                log.Report($"ERROR: Failed to list bus hardware: {_busScan.getError()}");
                return Array.Empty<string>();
            }

            BusHWIdVector busIds = _busScan.getResult();
            var names = new List<string>(busIds.Count);
            for (int i = 0; i < busIds.Count; i++)
            {
                BusHardwareId b = busIds[i];
                _buses.Add(b);
                bool ecat = b.getProtocol() == Nanolib.BUS_HARDWARE_ID_PROTOCOL_ETHERCAT;
                names.Add($"{b.getName()}  ({b.getProtocol()}){(ecat ? "  [EtherCAT]" : "")}");
            }
            log.Report($"Found {names.Count} network bus(es).");
            return names;
        }

        /// <summary>
        /// Opens the chosen adapter (by its index in the last <see cref="ListBuses"/>
        /// result), then adds + connects every drive found. <paramref name="expectedDeviceCount"/>
        /// is used only to warn on a mismatch (e.g. an unpowered drive); whatever is found
        /// is still connected so partial bring-up works. Returns true only if at least one
        /// drive connected cleanly.
        /// </summary>
        public bool Connect(int busIndex, int expectedDeviceCount, IProgress<string> log)
        {
            if (IsConnected)
            {
                log.Report("Already connected.");
                return true;
            }
            if (_accessor == null || busIndex < 0 || busIndex >= _buses.Count)
            {
                log.Report("ERROR: No bus selected - scan for buses first.");
                return false;
            }

            BusHardwareId adapter = _buses[busIndex];
            _adapter = adapter;
            AdapterName = adapter.getName();
            log.Report($"Using adapter: {adapter.getName()} ({adapter.getProtocol()})");

            log.Report("Opening bus hardware...");
            using (ResultVoid open = _accessor.openBusHardwareWithProtocol(adapter, new BusHardwareOptions()))
            {
                if (open.hasError())
                {
                    log.Report($"ERROR: Failed to open adapter: {open.getError()}");
                    _adapter = null;
                    return false;
                }
            }

            log.Report("Scanning EtherCAT line for drives...");
            using ResultDeviceIds scan = _accessor.scanDevices(adapter, null);
            if (scan.hasError() || scan.getResult().Count == 0)
            {
                log.Report("ERROR: No EtherCAT drives responded. Check cabling (IN vs OUT) and power.");
                _accessor.closeBusHardware(adapter);
                _adapter = null;
                return false;
            }

            DeviceIdVector found = scan.getResult();
            log.Report($"Found {found.Count} drive(s) on the line.");
            if (found.Count != expectedDeviceCount)
            {
                log.Report($"WARNING: expected {expectedDeviceCount} drives but found {found.Count}. " +
                           "Check power/cabling, or update the expected count.");
            }

            // Add + connect each drive, preserving scan (bus) order.
            for (int pos = 0; pos < found.Count; pos++)
            {
                DeviceId dev = found[pos];
                using ResultDeviceHandle add = _accessor.addDevice(dev);
                if (add.hasError())
                {
                    log.Report($"ERROR: addDevice failed at bus position {pos}: {add.getError()}");
                    TeardownPartial(log);
                    return false;
                }
                DeviceHandle handle = add.getResult();

                using (ResultVoid conn = _accessor.connectDevice(handle))
                {
                    if (conn.hasError())
                    {
                        log.Report($"ERROR: connectDevice failed at bus position {pos}: {conn.getError()}");
                        _accessor.removeDevice(handle);
                        TeardownPartial(log);
                        return false;
                    }
                }

                DeviceIdentity id = ReadIdentity(pos, handle);
                _handles.Add(handle);
                _devices.Add(id);
                log.Report($"  [pos {pos}] {id.Name}  serial={id.Serial}  fw={id.Firmware}");
            }

            log.Report("All drives connected and DISABLED (no motion). " +
                       "Run the wiggle test to confirm which bus position is which axis.");
            IsConnected = true;
            return true;
        }

        private DeviceIdentity ReadIdentity(int pos, DeviceHandle handle)
        {
            string name = SafeRead(() => _accessor!.getDeviceName(handle));
            string serial = SafeRead(() => _accessor!.getDeviceSerialNumber(handle));
            string fw = SafeRead(() => _accessor!.getDeviceFirmwareBuildId(handle));
            return new DeviceIdentity(pos, name, serial, fw);
        }

        private static string SafeRead(Func<ResultString> read)
        {
            using ResultString r = read();
            return r.hasError() ? "?" : r.getResult();
        }

        /// <summary>Releases everything connected so far (used on a mid-sequence failure).</summary>
        private void TeardownPartial(IProgress<string> log)
        {
            if (_accessor == null) return;
            foreach (DeviceHandle h in _handles)
            {
                try { _accessor.disconnectDevice(h); _accessor.removeDevice(h); }
                catch (Exception ex) { log.Report($"Teardown error: {ex.Message}"); }
            }
            _handles.Clear();
            _devices.Clear();
            if (_adapter != null)
            {
                _accessor.closeBusHardware(_adapter);
                _adapter = null;
            }
            ReleaseBusScan();
        }

        /// <summary>Disposes the cached bus-hardware list (the chosen adapter must be closed first).</summary>
        private void ReleaseBusScan()
        {
            _buses.Clear();
            _busScan?.Dispose();
            _busScan = null;
        }

        /// <summary>Disconnects every drive and closes the bus. Safe when not connected.</summary>
        public void Disconnect(IProgress<string> log)
        {
            if (_accessor == null)
            {
                IsConnected = false;
                return;
            }

            try
            {
                foreach (DeviceHandle h in _handles)
                {
                    _accessor.disconnectDevice(h);
                    _accessor.removeDevice(h);
                }
                _handles.Clear();
                _devices.Clear();

                if (_adapter != null)
                {
                    log.Report("Closing bus hardware...");
                    _accessor.closeBusHardware(_adapter);
                    _adapter = null;
                }
                ReleaseBusScan();
                log.Report("Disconnected.");
            }
            catch (Exception ex)
            {
                log.Report($"Disconnect error: {ex.Message}");
            }
            finally
            {
                IsConnected = false;
                AdapterName = "";
            }
        }
    }
}
