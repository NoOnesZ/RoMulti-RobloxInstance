using System;
using System.Threading;
using System.Windows;

namespace RoMulti
{
    internal static class Program
    {
        private const string AppMutexName = "RoMulti_SingleInstance_AppGuard";

        [STAThread]
        public static void Main(string[] args)
        {
            bool createdNew;
            using (var mutex = new Mutex(true, AppMutexName, out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show(
                        "RoMulti is already running. Check your taskbar or System Tray.",
                        "RoMulti",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                // Best-effort SeDebugPrivilege when running elevated
                NativeMethods.TryEnableDebugPrivilege();

                var app = new Application();
                app.DispatcherUnhandledException += (s, e) =>
                {
                    MessageBox.Show(
                        "Unexpected error:\n" + e.Exception.Message,
                        "RoMulti Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    e.Handled = true;
                };

                app.Run(new MainWindow());
            }
        }
    }
}
