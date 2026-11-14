using System;
using System.Windows.Forms;

namespace AlarmClock2112;
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Report(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) Report(ex);
        };
        try { Application.Run(new MainForm()); }
        catch (Exception ex) { Report(ex); }
    }
    private static void Report(Exception ex)
    {
        string path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AlarmClock2112", "error.log");
        try { System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!); System.IO.File.AppendAllText(path, $"[{DateTime.Now:O}]\n{ex}\n\n"); } catch { }
        try { MessageBox.Show($"Unexpected error: {ex.Message}\n\nDetails: {path}", "AlarmClock 2112", MessageBoxButtons.OK, MessageBoxIcon.Error); } catch { }
    }
}
