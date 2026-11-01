using System.Diagnostics;
using Microsoft.Win32;

namespace AgenticSdr.Radio;

/// <summary>
/// The Windows driver bound to a USB radio, read from the device's registry entry.
/// </summary>
internal static class UsbDriverFacts
{
    public static void Add(List<RadioFact> facts, string vid, string pid, string? serial)
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            string path = $@"SYSTEM\CurrentControlSet\Enum\USB\VID_{vid}&PID_{pid}";
            using RegistryKey? usb = Registry.LocalMachine.OpenSubKey(path);
            if (usb is null)
                return;

            string? instance = Match(usb.GetSubKeyNames(), serial);
            if (instance is null)
                return;
            using RegistryKey? device = usb.OpenSubKey(instance);
            if (device is null)
                return;

            string? service = device.GetValue("Service") as string;
            string? driverKey = device.GetValue("Driver") as string;
            string? description = null;
            string? version = null;
            if (!string.IsNullOrWhiteSpace(driverKey))
            {
                using RegistryKey? driver = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Control\Class\{driverKey}");
                description = driver?.GetValue("DriverDesc") as string;
                version = driver?.GetValue("DriverVersion") as string;
            }

            string text = string.Join(" ", new[] { description, service, version }.Where(part => !string.IsNullOrWhiteSpace(part)));
            RadioText.Add(facts, "USB driver", text);
        }
        catch (System.Security.SecurityException)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public static void AddLibraryFile(List<RadioFact> facts, string label, string fileName)
    {
        string path = Path.Combine(AppContext.BaseDirectory, fileName);
        if (!File.Exists(path))
            return;
        FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
        string? version = string.IsNullOrWhiteSpace(info.FileVersion) ? info.ProductVersion : info.FileVersion;
        RadioText.Add(facts, label, version);
    }

    private static string? Match(string[] names, string? serial)
    {
        if (!string.IsNullOrWhiteSpace(serial))
        {
            string? exact = names.FirstOrDefault(name => name.Equals(serial, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
                return exact;
            string? tail = names.FirstOrDefault(name => name.EndsWith(serial, StringComparison.OrdinalIgnoreCase));
            if (tail is not null)
                return tail;
        }

        return names.FirstOrDefault();
    }
}
