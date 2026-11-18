using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Notaekinesis.Core;

/// <summary>
/// "Start with Windows" for an unpackaged app: a value under the user's Run key, which needs no
/// admin rights and shows up in Task Manager's Startup apps. Task Manager can switch it off
/// without removing it (in StartupApproved), so both places are read to report the real state.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class StartupRegistration(string valueName, string? keyRoot = null)
{
    public const string BackgroundArg = "--background";

    private readonly string _run = (keyRoot ?? @"Software\Microsoft\Windows\CurrentVersion") + @"\Run";
    private readonly string _approved = (keyRoot ?? @"Software\Microsoft\Windows\CurrentVersion") + @"\Explorer\StartupApproved\Run";

    public static string CommandFor(string exePath) => $"\"{exePath}\" {BackgroundArg}";

    /// <summary>Registered and not disabled in Task Manager.</summary>
    public bool IsEnabled
    {
        get
        {
            using var run = Registry.CurrentUser.OpenSubKey(_run);
            return run?.GetValue(valueName) is string && !DisabledInTaskManager();
        }
    }

    public void Enable(string exePath)
    {
        using (var run = Registry.CurrentUser.CreateSubKey(_run))
            run.SetValue(valueName, CommandFor(exePath), RegistryValueKind.String);
        // Turning it on here also undoes a "Disable" from Task Manager.
        using var approved = Registry.CurrentUser.OpenSubKey(_approved, writable: true);
        approved?.DeleteValue(valueName, throwOnMissingValue: false);
    }

    public void Disable()
    {
        using var run = Registry.CurrentUser.OpenSubKey(_run, writable: true);
        run?.DeleteValue(valueName, throwOnMissingValue: false);
        using var approved = Registry.CurrentUser.OpenSubKey(_approved, writable: true);
        approved?.DeleteValue(valueName, throwOnMissingValue: false);
    }

    /// <summary>If it's registered for a different copy of the app (it was moved or updated elsewhere), point it at this one.</summary>
    public bool UpdatePath(string exePath)
    {
        using var run = Registry.CurrentUser.OpenSubKey(_run, writable: true);
        if (run?.GetValue(valueName) is not string current || current == CommandFor(exePath)) return false;
        run.SetValue(valueName, CommandFor(exePath), RegistryValueKind.String);
        return true;
    }

    // Task Manager writes 12 bytes; an odd first byte (0x03, 0x01) means disabled, even (0x02, 0x06) enabled.
    private bool DisabledInTaskManager()
    {
        using var approved = Registry.CurrentUser.OpenSubKey(_approved);
        return approved?.GetValue(valueName) is byte[] { Length: > 0 } state && (state[0] & 1) == 1;
    }
}
