using Microsoft.Win32;

namespace CapyBro.Services;

internal sealed class WindowsRegistryBackend : IRegistryBackend
{
    public string? GetValue(string keyPath, string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: false);
        return key?.GetValue(valueName) as string;
    }

    public void SetValue(string keyPath, string valueName, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath);
        key.SetValue(valueName, value, RegistryValueKind.String);
    }

    public void DeleteValue(string keyPath, string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }
}
