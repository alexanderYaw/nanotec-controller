using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NanotecController
{
    // FrmMain — connection lifecycle: pick a bus and connect/disconnect all drives,
    // enable/disable them, and the read-only parameter sweep. (Partial of FrmMain.)
    public partial class FrmMain
    {
        // --- Connection -----------------------------------------------------------

        private async void connectButton_Click(object? sender, EventArgs e) => await ConnectAsync();
        private async void disconnectButton_Click(object? sender, EventArgs e) => await DisconnectAsync();

        private async Task ConnectAsync()
        {
            SetState(connected: false, busy: true, "Scanning buses...");
            logBox.Clear();

            IReadOnlyList<string> buses = await Task.Run(() => _connection.ListBuses(_log));
            if (buses.Count == 0)
            {
                AppendLog("No network buses found - check Npcap and the EtherCAT NIC.");
                SetState(connected: false, busy: false, "No buses found");
                return;
            }

            // Let the user choose which bus to connect to (modal; -1 = cancelled).
            int choice = BusPicker.Choose(this, buses);
            if (choice < 0)
            {
                AppendLog("Connection cancelled.");
                SetState(connected: false, busy: false, "Disconnected");
                return;
            }

            SetState(connected: false, busy: true, "Connecting...");
            bool ok = await Task.Run(() => _connection.Connect(choice, EXPECTED_AXES, _log));
            if (!ok)
            {
                SetState(connected: false, busy: false, "Connection FAILED");
                return;
            }

            try
            {
                _motion = new MultiAxisController(_connection, TableAxes.Default);
            }
            catch (Exception ex)
            {
                AppendLog($"Axis-mapping error: {ex.Message}");
                AppendLog("Disconnecting - not all expected axes are present.");
                await Task.Run(() => _connection.Disconnect(_log));
                _motion = null;
                SetState(connected: false, busy: false, "Connection FAILED");
                return;
            }

            _drivesEnabled = false;
            _statusFailures = 0;
            rbOff.Checked = true;
            SetState(connected: true, busy: false, $"Connected ({_connection.Handles.Count} axes)");
            statusTimer.Start();
        }

        private async Task DisconnectAsync()
        {
            statusTimer.Stop();
            joystickTimer.Stop();
            rbOff.Checked = true;
            SetState(connected: _connection.IsConnected, busy: true, "Disconnecting...");

            if (_motion != null && _drivesEnabled)
                await Task.Run(() => _motion.DisableAll());
            _drivesEnabled = false;

            await Task.Run(() => _connection.Disconnect(_log));
            _motion = null;

            foreach (AxisRow row in _axisRows.Values) row.Status.Text = "-";
            ResetSoftLimitTracking();
            SetState(connected: false, busy: false, "Disconnected");
        }

        // --- Enable / disable (all axes) ------------------------------------------

        private async void enableButton_Click(object? sender, EventArgs e)
        {
            if (_motion == null) return;
            _busy = true;
            RefreshButtons();
            AppendLog("Enabling all drives (holding torque, no motion)...");

            bool ok = await RunDriveOp(() => _motion.EnableAll());
            _drivesEnabled = ok;
            AppendLog(ok ? "All drives ENABLED." : "Enable FAILED - see error above.");

            _busy = false;
            RestartTimers();
            RefreshButtons();
        }

        private async void disableButton_Click(object? sender, EventArgs e)
        {
            if (_motion == null) return;
            rbOff.Checked = true;
            _busy = true;
            RefreshButtons();

            await RunDriveOp(() => _motion.DisableAll());
            _drivesEnabled = false;
            AppendLog("All drives DISABLED.");

            _busy = false;
            RestartTimers();
            RefreshButtons();
        }
    }
}
