using System;
using System.Drawing;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MotorControlApp
{
    /// <summary>
    /// Separate window for reading and writing drive parameters. Pure UI: all drive access,
    /// timer coordination, and the single NanoLib channel live in <see cref="FrmMain"/> — this
    /// form only calls FrmMain's params actions and shows their output in its own log.
    ///
    /// "Read Params" is the read-only sweep (writes nothing). "Write" sets any OD index:sub on
    /// a chosen drive in RAM; "Save to NV" persists the drive's current values across a
    /// power-cycle (object 0x1010:01). Writing arbitrary objects is an expert action — both
    /// write paths confirm first.
    /// </summary>
    public sealed class FrmParams : Form
    {
        private readonly FrmMain _owner;
        private readonly IProgress<string> _sink;
        private readonly System.Windows.Forms.Timer _refresh = new() { Interval = 300 };

        private readonly ComboBox _axis, _bits;
        private readonly TextBox _index, _sub, _value, _output;
        private readonly Button _read, _write, _saveNv;

        public FrmParams(FrmMain owner)
        {
            _owner = owner;
            Text = "Parameters - read / write drive objects";
            FormBorderStyle = FormBorderStyle.SizableToolWindow;
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9F);
            ClientSize = new Size(624, 470);
            MinimumSize = new Size(560, 360);

            _sink = new Progress<string>(AppendOutput);

            // --- Write-object group (one input row + a button row) ---
            var writeGroup = new GroupBox { Text = "Write object (expert)", Location = new Point(12, 8), Size = new Size(600, 100) };

            Label Lbl(string t, int x, int y)
            {
                var l = new Label { Text = t, Location = new Point(x, y), AutoSize = true };
                writeGroup.Controls.Add(l);
                return l;
            }

            Lbl("Axis", 14, 33);
            _axis = new ComboBox { Location = new Point(56, 29), Size = new Size(78, 28), DropDownStyle = ComboBoxStyle.DropDownList };
            foreach (AxisId a in Enum.GetValues<AxisId>()) _axis.Items.Add(a);
            _axis.SelectedIndex = 0;
            writeGroup.Controls.Add(_axis);

            Lbl("0x", 146, 33);
            _index = new TextBox { Location = new Point(170, 29), Size = new Size(58, 27), Text = "6084" };
            writeGroup.Controls.Add(_index);

            Lbl(":", 232, 33);
            _sub = new TextBox { Location = new Point(244, 29), Size = new Size(38, 27), Text = "00" };
            writeGroup.Controls.Add(_sub);

            Lbl("Value", 292, 33);
            _value = new TextBox { Location = new Point(338, 29), Size = new Size(112, 27) };
            writeGroup.Controls.Add(_value);

            Lbl("Bits", 458, 33);
            _bits = new ComboBox { Location = new Point(492, 29), Size = new Size(56, 28), DropDownStyle = ComboBoxStyle.DropDownList };
            _bits.Items.AddRange(new object[] { "8", "16", "32" });
            _bits.SelectedItem = "32";
            writeGroup.Controls.Add(_bits);

            _write = new Button { Text = "Write", Location = new Point(56, 62), Size = new Size(92, 30) };
            _write.Click += async (s, e) => await DoWrite();
            writeGroup.Controls.Add(_write);

            _saveNv = new Button { Text = "Save to NV", Location = new Point(154, 62), Size = new Size(110, 30) };
            _saveNv.Click += async (s, e) => await DoSaveNv();
            writeGroup.Controls.Add(_saveNv);

            writeGroup.Controls.Add(new Label
            {
                Text = "Value: decimal, or 0x.. for hex. Write = RAM; Save to NV persists across power-cycle.",
                Location = new Point(276, 68), AutoSize = true, ForeColor = Color.DimGray,
            });
            Controls.Add(writeGroup);

            // --- Read button ---
            _read = new Button { Text = "Read Params (all axes)", Location = new Point(12, 116), Size = new Size(600, 34) };
            _read.Click += async (s, e) => await DoRead();
            Controls.Add(_read);

            // --- Output log ---
            _output = new TextBox
            {
                Location = new Point(12, 158),
                Size = new Size(600, 300),
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Color.White,
                Font = new Font("Consolas", 9F),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            };
            Controls.Add(_output);

            _refresh.Tick += (s, e) => RefreshEnabled();
            _refresh.Start();
            RefreshEnabled();
        }

        private void AppendOutput(string s) => _output.AppendText($"{DateTime.Now:HH:mm:ss}  {s}{Environment.NewLine}");

        private async Task DoRead() => await _owner.ReadAllParamsAsync(_sink);

        private async Task DoWrite()
        {
            if (!TryHexU16(_index.Text, out ushort index)) { AppendOutput($"Bad index '{_index.Text}' (hex, e.g. 6084)."); return; }
            if (!TryHexByte(_sub.Text, out byte sub)) { AppendOutput($"Bad sub-index '{_sub.Text}' (hex, e.g. 00)."); return; }
            if (!TryParseValue(_value.Text, out long value)) { AppendOutput($"Bad value '{_value.Text}' (decimal, or 0x.. for hex)."); return; }
            uint bits = uint.Parse((string)_bits.SelectedItem!);
            var id = (AxisId)_axis.SelectedItem!;

            if (MessageBox.Show(this,
                    $"Write 0x{index:X4}:{sub:X2} = {value} (0x{value:X}, {bits}-bit) to {id}?\n\n" +
                    "This changes a live drive object (RAM only). Use Save to NV afterwards to persist.",
                    "Confirm object write", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
                return;

            await _owner.WriteObjectAsync(id, index, sub, value, bits, _sink);
        }

        private async Task DoSaveNv()
        {
            var id = (AxisId)_axis.SelectedItem!;
            if (MessageBox.Show(this,
                    $"Save ALL current parameters on {id} to non-volatile memory (object 0x1010:01)?\n\n" +
                    "This persists the drive's current values across power-cycles.",
                    "Confirm save to NV", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
                return;

            await _owner.SaveParamsToNvAsync(id, _sink);
        }

        /// <summary>Enables read vs write controls from FrmMain's live state.</summary>
        private void RefreshEnabled()
        {
            _read.Enabled = _owner.CanAccessParams;
            bool write = _owner.CanWriteParams;
            _axis.Enabled = _index.Enabled = _sub.Enabled = _value.Enabled = _bits.Enabled = write;
            _write.Enabled = _saveNv.Enabled = write;
        }

        // --- Parsers: index/sub are hex (0x optional); value is decimal unless 0x-prefixed ---
        private static bool TryHexU16(string s, out ushort v)
        {
            s = s.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
            return ushort.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v);
        }

        private static bool TryHexByte(string s, out byte v)
        {
            s = s.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
            if (s.Length == 0) { v = 0; return true; }   // blank sub-index = 0x00
            return byte.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v);
        }

        private static bool TryParseValue(string s, out long v)
        {
            s = s.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return long.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v);
            return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _refresh.Stop();
            base.OnFormClosing(e);
        }
    }
}
