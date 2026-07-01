using System;
using System.Windows.Forms;

namespace NanotecController
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            // NanoLib's native EtherCAT driver is x64-only.
            if (!Environment.Is64BitProcess)
            {
                MessageBox.Show(
                    "This application must run as a 64-bit (x64) process.\n" +
                    "NanoLib native drivers require an x64 environment.",
                    "Architecture Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.Run(new FrmMain());
        }
    }
}
