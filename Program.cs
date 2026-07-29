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

            // Capture any unhandled exception (UI thread or background) to a crash log + dialog,
            // instead of the process dying silently. Route UI-thread exceptions through
            // Application.ThreadException so they're catchable here rather than terminating.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => ReportCrash("UI thread", e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => ReportCrash("background thread", e.ExceptionObject as Exception);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.Run(new FrmMain());
        }

        // Writes the full exception (message + stack) to Desktop\nanotec_crash.log and shows it,
        // so a crash becomes a concrete, reportable error rather than a silent exit.
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
            catch { /* logging is best-effort; still show the dialog below */ }

            MessageBox.Show(
                "An unexpected error occurred (details saved to Desktop\\nanotec_crash.log):\r\n\r\n" + detail,
                "Nanotec Controller - error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
