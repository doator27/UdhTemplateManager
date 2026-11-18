using HardwareTemplateBuilder.Core.Platform;

namespace HardwareTemplateBuilder.Tests;

/// <summary>Shortcut clean-up that keeps the apps pinnable to the taskbar (Windows only).</summary>
public class AppShortcutsTests
{
    [Fact]
    public void StripsOldIdsFromOurShortcutsOnly()
    {
        if (!OperatingSystem.IsWindows()) return;

        var folder = Path.Combine(Path.GetTempPath(), $"htb_lnk_{Guid.NewGuid():N}");
        var sub = Path.Combine(folder, "Pinned");
        Directory.CreateDirectory(sub);
        try
        {
            var exe = Path.Combine(Environment.SystemDirectory, "notepad.exe");
            var other = Path.Combine(Environment.SystemDirectory, "calc.exe");
            var oldPin = Path.Combine(sub, "Hardware Template Builder 95.lnk");
            var sameExe = Path.Combine(folder, "Same exe.lnk");
            var plain = Path.Combine(folder, "Plain.lnk");
            var foreign = Path.Combine(folder, "Someone else.lnk");

            AppShortcuts.CreateShortcut(oldPin, other, "old pin", "HardwareTemplateBuilder.Wpf");
            AppShortcuts.CreateShortcut(sameExe, exe, "same exe", "Some.Other.Id");
            AppShortcuts.CreateShortcut(plain, exe, "plain");
            AppShortcuts.CreateShortcut(foreign, other, "foreign", "Contoso.App");
            Assert.Equal("HardwareTemplateBuilder.Wpf", AppShortcuts.Read(oldPin).AppUserModelId);
            Assert.Equal(exe, AppShortcuts.Read(plain).Target, ignoreCase: true);

            var changed = AppShortcuts.StripOldAppIds(exe, new[] { folder });

            Assert.Equal(new[] { oldPin, sameExe }.OrderBy(x => x), changed.OrderBy(x => x));
            Assert.Null(AppShortcuts.Read(oldPin).AppUserModelId);
            Assert.Null(AppShortcuts.Read(sameExe).AppUserModelId);
            Assert.Equal(exe, AppShortcuts.Read(sameExe).Target, ignoreCase: true);
            Assert.Null(AppShortcuts.Read(plain).AppUserModelId);
            Assert.Equal("Contoso.App", AppShortcuts.Read(foreign).AppUserModelId);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
