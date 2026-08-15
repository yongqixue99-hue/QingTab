using System;
using Microsoft.Win32;

namespace QingTab.Helpers;

public static class AutoStartManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupApprovedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string StateKeyPath = @"Software\QingTab";
    private const string ValueName = "QingTab";
    private const string InitializedValueName = "Initialized";

    public static bool IsEnabled()
    {
        try
        {
            return IsRegisteredToCurrentExecutable() && IsStartupApproved();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Enables startup once, on the first launch. Later launches preserve the user's choice.
    /// Returns true only when this is the first launch.
    /// </summary>
    public static bool InitializeFirstRun(out bool autoStartEnabled)
    {
        var firstRun = false;
        try
        {
            using (var stateKey = Registry.CurrentUser.CreateSubKey(StateKeyPath, true))
            {
                firstRun = stateKey?.GetValue(InitializedValueName) == null;
                if (firstRun)
                    stateKey?.SetValue(InitializedValueName, 1, RegistryValueKind.DWord);
            }

            if (firstRun)
            {
                TrySetEnabled(true, out _);
            }
            else
            {
                RepairMovedExecutableRegistration();
            }
        }
        catch
        {
            // The app remains usable even if registry access is restricted.
        }

        autoStartEnabled = IsEnabled();
        return firstRun;
    }

    public static bool TrySetEnabled(bool enabled, out string error)
    {
        try
        {
            if (enabled)
                AddToStartup();
            else
                RemoveFromStartup();

            error = string.Empty;
            return IsEnabled() == enabled;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool TryRemoveAll(out string error)
    {
        try
        {
            RemoveFromStartup();
            Registry.CurrentUser.DeleteSubKeyTree(StateKeyPath, false);
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void RepairMovedExecutableRegistration()
    {
        using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
        if (runKey?.GetValue(ValueName) is not string currentValue)
            return;

        if (!string.Equals(currentValue, GetLaunchCommand(), StringComparison.OrdinalIgnoreCase))
            runKey.SetValue(ValueName, GetLaunchCommand(), RegistryValueKind.String);
    }

    private static bool IsRegisteredToCurrentExecutable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
        var value = key?.GetValue(ValueName) as string;
        return string.Equals(value, GetLaunchCommand(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsStartupApproved()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupApprovedKeyPath, false);
        var value = key?.GetValue(ValueName) as byte[];
        return value == null || value.Length == 0 || value[0] % 2 == 0;
    }

    private static void AddToStartup()
    {
        using (var runKey = Registry.CurrentUser.CreateSubKey(RunKeyPath, true))
            runKey?.SetValue(ValueName, GetLaunchCommand(), RegistryValueKind.String);

        // Matches the StartupApproved representation used by the maintained upstream project.
        var enabledData = new byte[12];
        enabledData[0] = 0x02;
        using var approvedKey = Registry.CurrentUser.CreateSubKey(StartupApprovedKeyPath, true);
        approvedKey?.SetValue(ValueName, enabledData, RegistryValueKind.Binary);
    }

    private static void RemoveFromStartup()
    {
        using (var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
            runKey?.DeleteValue(ValueName, false);

        using var approvedKey = Registry.CurrentUser.OpenSubKey(StartupApprovedKeyPath, true);
        approvedKey?.DeleteValue(ValueName, false);
    }

    private static string GetLaunchCommand()
    {
        return $"\"{AppPaths.ExecutablePath}\" --startup";
    }
}
