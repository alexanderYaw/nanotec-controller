using System;
using System.Threading.Tasks;

namespace MotorControlApp
{
    // FrmMain — owns the parameter read/write/save-to-NV actions; the FrmParams window is
    // pure UI that calls these. FrmMain owns the single NanoLib channel and timer
    // coordination, so all drive access is serialized here. Output is reported to the
    // caller's sink (the params window's own log). (Partial of FrmMain.)
    public partial class FrmMain
    {
        private FrmParams? _paramsWindow;

        // Repurposed: the main "Parameters..." button opens the params window.
        private void readParamsButton_Click(object? sender, EventArgs e)
        {
            if (_paramsWindow == null || _paramsWindow.IsDisposed)
                _paramsWindow = new FrmParams(this);
            _paramsWindow.Show();
            _paramsWindow.BringToFront();
        }

        /// <summary>Reading params needs the link up and idle (no motion map required).</summary>
        internal bool CanAccessParams => _connection.IsConnected && !_busy;

        /// <summary>Writing/saving needs the link up, idle, and the axis map present.</summary>
        internal bool CanWriteParams => _connection.IsConnected && !_busy && _motion != null;

        /// <summary>
        /// Read-only sweep of every connected drive's key objects, reported to
        /// <paramref name="sink"/>. Writes nothing, so it can't disturb the values it reads.
        /// </summary>
        internal async Task ReadAllParamsAsync(IProgress<string> sink)
        {
            if (!CanAccessParams) { sink.Report("Read: link not ready."); return; }

            _busy = true; RefreshButtons();
            statusTimer.Stop(); joystickTimer.Stop();
            sink.Report("=== Read drive parameters (read-only; writes nothing) ===");
            await Task.Run(() =>
            {
                try
                {
                    for (int i = 0; i < _connection.Handles.Count; i++)
                    {
                        DeviceIdentity id = _connection.Devices[i];
                        string axis = TableAxes.NameForBusPosition(id.BusPosition);
                        sink.Report($"--- [pos {id.BusPosition}] AXIS {axis}  ({id.Name})  serial={id.Serial}  fw={id.Firmware} ---");

                        sink.Report("  Protection / motor limits:");
                        foreach (ParameterReadout p in DriveDiagnostics.ReadLimits(_connection.Accessor!, _connection.Handles[i]))
                            sink.Report("    " + p);

                        sink.Report("  Units & scaling (defines position/velocity units):");
                        foreach (ParameterReadout p in DriveDiagnostics.ReadUnitsScaling(_connection.Accessor!, _connection.Handles[i]))
                            sink.Report("    " + p);
                    }
                }
                catch (Exception ex)
                {
                    sink.Report($"ERROR during parameter read: {ex.Message}");
                }
            });
            sink.Report("=== Done. Re-run after a power cycle and compare to confirm NV persistence. ===");

            _busy = false; RestartTimers(); RefreshButtons();
        }

        /// <summary>
        /// Writes one OD entry on an axis (expert). The write lands in RAM only — use
        /// <see cref="SaveParamsToNvAsync"/> to persist. Reports result to <paramref name="sink"/>.
        /// </summary>
        internal async Task WriteObjectAsync(AxisId id, ushort index, byte sub, long value, uint bits, IProgress<string> sink)
        {
            if (!CanWriteParams) { sink.Report("Write: link not ready."); return; }
            if (!_motion!.Has(id)) { sink.Report($"Write: axis {id} is not connected."); return; }

            _busy = true; RefreshButtons();
            statusTimer.Stop(); joystickTimer.Stop();
            try
            {
                await Task.Run(() => _motion.WriteObject(id, index, sub, value, bits));
                sink.Report($"Wrote 0x{index:X4}:{sub:X2} = {value} (0x{value:X}, {bits}-bit) to {id} [RAM].");
            }
            catch (Exception ex)
            {
                sink.Report($"Write FAILED: {ex.Message}");
            }
            _busy = false; RestartTimers(); RefreshButtons();
        }

        /// <summary>Persists one axis's current parameters to NV (0x1010:01). Reports to <paramref name="sink"/>.</summary>
        internal async Task SaveParamsToNvAsync(AxisId id, IProgress<string> sink)
        {
            if (!CanWriteParams) { sink.Report("Save NV: link not ready."); return; }
            if (!_motion!.Has(id)) { sink.Report($"Save NV: axis {id} is not connected."); return; }

            _busy = true; RefreshButtons();
            statusTimer.Stop(); joystickTimer.Stop();
            try
            {
                await Task.Run(() => _motion.SaveParametersToNV(id));
                sink.Report($"{id}: parameters saved to NV (0x1010:01).");
            }
            catch (Exception ex)
            {
                sink.Report($"Save NV FAILED: {ex.Message}");
            }
            _busy = false; RestartTimers(); RefreshButtons();
        }
    }
}
