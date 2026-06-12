using System.Diagnostics;
using System.Security.Principal;
using System.Windows;

namespace Reclaim.App.Services;

/// <summary>
/// Detects whether the process is running with administrator rights and can
/// relaunch the app elevated. Elevation is always an explicit user choice
/// (triggers a UAC prompt) — never automatic — because admin rights remove a
/// layer of OS-level protection around system files.
/// </summary>
public static class Elevation
{
    /// <summary>True if the current process holds the Administrator role.</summary>
    public static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Relaunches the current app with elevation (UAC prompt) and shuts down the
    /// current instance. Returns false if the user declined the prompt or it
    /// failed, leaving the current (non-elevated) instance running.
    /// </summary>
    public static bool RestartElevated()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
                return false;

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true, // required for the "runas" verb
                Verb = "runas",         // triggers the UAC elevation prompt
            };

            Process.Start(psi);
            // Started elevated copy; close this one.
            Application.Current.Shutdown();
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // User clicked "No" on the UAC prompt, or elevation is unavailable.
            return false;
        }
        catch
        {
            return false;
        }
    }
}
