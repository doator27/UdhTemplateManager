using System;
using System.IO;

namespace HardwareTemplateBuilder.Core.Data;

/// <summary>
/// Provides a stable, per-machine identifier that persists across application restarts.
/// The identifier is a GUID stored in a flat file next to the application database.
/// It does not change if the machine is renamed.
/// </summary>
public static class MachineIdentityService
{
    private const string FileName = "machine_id.txt";

    /// <summary>
    /// Returns the stable identifier for the current machine.
    /// Creates and stores a new GUID on the first call.
    /// </summary>
    public static string GetMachineId()
    {
        var folder   = GetAppFolder();
        var filePath = Path.Combine(folder, FileName);

        if (File.Exists(filePath))
        {
            var stored = File.ReadAllText(filePath).Trim();
            if (!string.IsNullOrEmpty(stored))
                return stored;
        }

        var newId = Guid.NewGuid().ToString("N");
        File.WriteAllText(filePath, newId);
        return newId;
    }

    private static string GetAppFolder()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var folder  = Path.Combine(appData, "HardwareTemplateBuilder");
        Directory.CreateDirectory(folder);
        return folder;
    }
}
