using System;
using System.Collections.Generic;
using Nlc;

namespace NanotecController
{
    /// <summary>Identity read from a drive at connect — logged so a mis-cabled axis is caught.</summary>
    public readonly record struct DeviceIdentity(
        int BusPosition, string Name, string Serial, string Firmware);

    /// <summary>
    /// EtherCAT bring-up: scans the line and connects to EVERY drive found, preserving bus (scan)
    /// order, so axes can be addressed by <see cref="AxisConfig.BusPosition"/>. Connects and
    /// verifies only — never enables a drive or commands motion.
    /// </summary>
    public sealed class MultiAxisConnection
    {
        #region State

        private NanoLibAccessor? _accessor;
        private BusHardwareId? _adapter;

        // BOTH must stay fields: _busIds owns the native storage that every BusHardwareId in
        // _buses points into non-owningly. See Developer Guide §3, "SWIG ownership".
        private ResultBusHwIds? _busScan;
        private BusHWIdVector? _busIds;

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

        #endregion

        #region Scan and connect

        /// <summary>Enumerates the available network interfaces. The returned display strings are
        /// index-aligned with the selection passed back to <see cref="Connect"/>; the scan is held
        /// internally so the chosen adapter stays valid through Connect.</summary>
        public IReadOnlyList<string> ListBuses(IProgress<string> log)
        {
            log.Report("Initializing NanoLib accessor...");
            _accessor ??= Nanolib.getNanoLibAccessor();

            log.Report("Scanning for network interfaces...");
            ReleaseBusScan();
            _busScan = _accessor.listAvailableBusHardware();
            if (_busScan.hasError())
            {
                log.Report($"ERROR: Failed to list bus hardware: {_busScan.getError()}");
                return [];
            }

            _busIds = _busScan.getResult();
            var names = new List<string>(_busIds.Count);
            for (int i = 0; i < _busIds.Count; i++)
            {
                BusHardwareId b = _busIds[i];
                _buses.Add(b);
                bool ecat = b.getProtocol() == Nanolib.BUS_HARDWARE_ID_PROTOCOL_ETHERCAT;
                names.Add($"{b.getName()}  ({b.getProtocol()}){(ecat ? "  [EtherCAT]" : "")}");
            }
            log.Report($"Found {names.Count} network bus(es).");
            return names;
        }

        /// <summary>Opens the adapter at <paramref name="busIndex"/> in the last <see cref="ListBuses"/>
        /// result, then adds + connects every drive found in scan order. A count mismatch against
        /// <paramref name="expectedDeviceCount"/> only warns, so partial bring-up still works.</summary>
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

        #endregion

        #region Teardown

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

        /// <summary>Disposes the cached bus-hardware list (the chosen adapter must be closed first).
        /// Drops the non-owning element proxies BEFORE the vector that owns their storage.</summary>
        private void ReleaseBusScan()
        {
            _buses.Clear();
            _busIds?.Dispose();
            _busIds = null;
            _busScan?.Dispose();
            _busScan = null;
        }

        /// <summary>Disconnects every drive and closes the bus; safe when not connected. Each drive's
        /// release is isolated and the bus close runs regardless, so one failure can't leave the
        /// adapter open while the caller is told (IsConnected = false) that it is free.</summary>
        public void Disconnect(IProgress<string> log)
        {
            if (_accessor == null)
            {
                IsConnected = false;
                return;
            }

            foreach (DeviceHandle h in _handles)
            {
                try { _accessor.disconnectDevice(h); _accessor.removeDevice(h); }
                catch (Exception ex) { log.Report($"Disconnect error on a drive: {ex.Message}"); }
            }
            _handles.Clear();
            _devices.Clear();

            try
            {
                if (_adapter != null)
                {
                    log.Report("Closing bus hardware...");
                    _accessor.closeBusHardware(_adapter);
                }
                log.Report("Disconnected.");
            }
            catch (Exception ex)
            {
                log.Report($"Bus close error: {ex.Message}");
            }
            finally
            {
                _adapter = null;
                ReleaseBusScan();
                IsConnected = false;
                AdapterName = "";
            }
        }

        #endregion
    }
}
