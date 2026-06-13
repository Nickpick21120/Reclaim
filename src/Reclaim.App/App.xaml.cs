using System;
using System.Windows;
using System.Windows.Threading;
using Reclaim.App.Services;

namespace Reclaim.App;

public partial class App : Application
{
    public App()
    {
        // Capture ANY unhandled exception (including XAML/startup failures) and
        // write a detailed report, so crashes are diagnosable even when nothing
        // prints to the console. Reports are stored locally and only shared if the
        // user chooses to on the next launch.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Diagnostics.WriteCrash("AppDomain", e.ExceptionObject as Exception);

        DispatcherUnhandledException += (_, e) =>
        {
            Diagnostics.WriteCrash("Dispatcher", e.Exception);
            // Keep the window up if possible so the user sees something.
            e.Handled = true;
        };
    }
}
