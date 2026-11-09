// entry point. build with the csc that ships with windows:
//
//   %WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:winexe
//       /out:Archon.exe *.cs
//       /r:System.dll /r:System.Core.dll /r:System.Drawing.dll
//       /r:System.Windows.Forms.dll /r:System.Security.dll
//
// that compiler is C# 5, so no interpolated strings and no ?. anywhere.
// files are utf-8 with a BOM or csc mangles the symbols in the ui.

using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace Archon
{
    static class Program
    {
        private static int _reporting;

        [STAThread]
        static void Main()
        {
            // has to happen before any window exists. MainForm does its own
            // scaling, so letting windows stretch it too would blur everything.
            try { Native.SetProcessDPIAware(); }
            catch (EntryPointNotFoundException) { }

            bool createdNew;
            Mutex appMutex;
            try
            {
                appMutex = new Mutex(true, "Archon_SingleInstance", out createdNew);
            }
            catch (UnauthorizedAccessException)
            {
                // a copy running as administrator made the name, and its default
                // acl shuts this unelevated one out. it's still already running.
                appMutex = null;
                createdNew = false;
            }

            using (appMutex)
            {
                if (!createdNew)
                {
                    MessageBox.Show(
                        "Archon is already running.\nCheck your system tray.",
                        "Already running",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

                MainForm form = new MainForm();

                Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
                {
                    // if the throw came from OnPaint, showing a dialog repaints
                    // and throws again. without this guard they stack faster
                    // than you can close them.
                    if (Interlocked.Exchange(ref _reporting, 1) == 1) return;

                    try
                    {
                        form.HaltTimers();

                        DialogResult answer = MessageBox.Show(
                            "Something went wrong:\n\n" + e.Exception.Message +
                            "\n\nUpdates have been paused so the error cannot repeat. " +
                            "Resume them?",
                            "Archon",
                            MessageBoxButtons.YesNo,
                            MessageBoxIcon.Error);

                        if (answer == DialogResult.Yes) form.ResumeTimers();
                        else Application.Exit();
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _reporting, 0);
                    }
                };

                Application.Run(form);

                GC.KeepAlive(appMutex);
            }
        }
    }

    static class AppIcon
    {
        public static Icon Create()
        {
            return AppMark.MakeIcon(32);
        }
    }
}
