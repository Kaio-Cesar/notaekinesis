using System.Runtime.Versioning;
using Microsoft.Win32;
using Notaekinesis.Core;

namespace Notaekinesis.Core.Tests;

/// <summary>Uses a scratch key under HKCU\Software, never the real Run key.</summary>
[SupportedOSPlatform("windows")]
public sealed class StartupRegistrationTests : IDisposable
{
    private readonly string _root = @"Software\notaekinesis-tests\" + Guid.NewGuid().ToString("N");
    private readonly StartupRegistration _startup;

    public StartupRegistrationTests() => _startup = new StartupRegistration("notaekinesis", _root);

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(@"Software\notaekinesis-tests", throwOnMissingSubKey: false);

    private string? RunValue()
    {
        using var run = Registry.CurrentUser.OpenSubKey(_root + @"\Run");
        return run?.GetValue("notaekinesis") as string;
    }

    private void SetTaskManagerState(byte first)
    {
        using var approved = Registry.CurrentUser.CreateSubKey(_root + @"\Explorer\StartupApproved\Run");
        approved.SetValue("notaekinesis", new byte[] { first, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
    }

    [Fact]
    public void Enable_registers_a_background_launch_and_disable_removes_it()
    {
        Assert.False(_startup.IsEnabled);
        _startup.Enable(@"C:\Apps\notaekinesis\Notaekinesis.exe");
        Assert.True(_startup.IsEnabled);
        Assert.Equal("\"C:\\Apps\\notaekinesis\\Notaekinesis.exe\" --background", RunValue());

        _startup.Disable();
        Assert.False(_startup.IsEnabled);
        Assert.Null(RunValue());
    }

    [Fact]
    public void Disabled_in_task_manager_reads_as_off_and_enabling_again_clears_it()
    {
        _startup.Enable(@"C:\a\Notaekinesis.exe");
        SetTaskManagerState(0x03);
        Assert.False(_startup.IsEnabled);
        SetTaskManagerState(0x02);
        Assert.True(_startup.IsEnabled);

        SetTaskManagerState(0x03);
        _startup.Enable(@"C:\a\Notaekinesis.exe");
        Assert.True(_startup.IsEnabled);
    }

    [Fact]
    public void A_moved_app_updates_its_registration_but_an_unregistered_one_stays_unregistered()
    {
        Assert.False(_startup.UpdatePath(@"C:\new\Notaekinesis.exe"));
        Assert.Null(RunValue());

        _startup.Enable(@"C:\old\Notaekinesis.exe");
        Assert.True(_startup.UpdatePath(@"C:\new\Notaekinesis.exe"));
        Assert.Equal("\"C:\\new\\Notaekinesis.exe\" --background", RunValue());
        Assert.False(_startup.UpdatePath(@"C:\new\Notaekinesis.exe"));
    }
}
