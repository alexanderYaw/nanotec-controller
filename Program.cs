using System;
using System.IO;
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

            // CatchException routes UI-thread exceptions through ThreadException so they reach
            // ReportCrash rather than terminating the process.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => ReportCrash("UI thread", e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => ReportCrash("background thread", e.ExceptionObject as Exception);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.Run(new FrmMain());
        }

        /// <summary>Appends the exception to Desktop\nanotec_crash.log and shows it, so a crash is a
        /// reportable error rather than a silent exit. Logging is best-effort; the dialog always shows.</summary>
        private static void ReportCrash(string where, Exception? ex)
        {
            string detail = ex?.ToString() ?? "(no exception object)";
            string text = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  [{where}]\r\n{detail}\r\n\r\n";
            try
            {
                string path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "nanotec_crash.log");
                File.AppendAllText(path, text);
            }
            catch { }

            MessageBox.Show(
                "An unexpected error occurred (details saved to Desktop\\nanotec_crash.log):\r\n\r\n" + detail,
                "Nanotec Controller - error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
