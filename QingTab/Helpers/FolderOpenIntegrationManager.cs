using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace QingTab.Helpers;

/// <summary>
/// Owns QingTab's per-user Folder default-open command. It never overwrites an
/// existing unowned HKCU Folder class and only removes values that still
/// exactly match the command QingTab installed.
/// </summary>
public static class FolderOpenIntegrationManager
{
    private const string StateKeyPath = @"Software\QingTab";
    private const string FolderClassPath = @"Software\Classes\Folder";
    private const string ShellKeyPath = FolderClassPath + @"\shell";
    private const string OpenKeyPath = ShellKeyPath + @"\open";
    private const string CommandKeyPath = OpenKeyPath + @"\command";
    private const string OwnedValueName = "DirectFolderOpenOwned";
    private const string InstalledCommandValueName = "DirectFolderOpenCommand";

    public static bool IsEnabled()
    {
        try
        {
            if (!IsOwned(out var installedCommand)) return false;

            var expectedCommand = GetCommand(AppPaths.ExecutablePath);
            if (!EqualsIgnoreCase(installedCommand, expectedCommand)) return false;

            using var commandKey = Registry.CurrentUser.OpenSubKey(CommandKeyPath, false);
            return commandKey != null
                   && EqualsIgnoreCase(ReadString(commandKey, string.Empty), expectedCommand)
                   && string.Equals(ReadString(commandKey, "DelegateExecute"), string.Empty, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    public static bool TrySetEnabled(bool enabled, out string error)
    {
        return enabled ? TryEnable(out error) : TryDisable(out error);
    }

    public static bool TryRepairMovedExecutableRegistration(out string error)
    {
        try
        {
            if (!IsOwned(out var installedCommand))
            {
                error = string.Empty;
                return true;
            }

            if (string.IsNullOrWhiteSpace(installedCommand))
            {
                error = "新标签接管的所有权记录不完整，已停止自动修复。";
                return false;
            }

            using var commandKey = Registry.CurrentUser.OpenSubKey(CommandKeyPath, true);
            if (commandKey == null)
            {
                error = "新标签接管的注册项已被删除，已停止自动修复。";
                return false;
            }

            var actualCommand = ReadString(commandKey, string.Empty);
            var delegateExecute = ReadString(commandKey, "DelegateExecute");
            var expectedCommand = GetCommand(AppPaths.ExecutablePath);
            if (!string.Equals(delegateExecute, string.Empty, StringComparison.Ordinal)
                || (!EqualsIgnoreCase(actualCommand, installedCommand)
                    && !EqualsIgnoreCase(actualCommand, expectedCommand)))
            {
                error = "文件夹打开方式已被其他程序修改，轻页没有覆盖它。";
                return false;
            }

            commandKey.SetValue(string.Empty, expectedCommand, RegistryValueKind.String);
            WriteOwnership(expectedCommand);
            NotifyAssociationChanged();
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryEnable(out string error)
    {
        try
        {
            if (IsOwned(out _))
            {
                if (!TryRepairMovedExecutableRegistration(out error)) return false;
                if (IsEnabled()) return true;
                error = "新标签接管没有处于完整状态；为保护现有文件夹设置，轻页没有强行覆盖。";
                return false;
            }

            using (var existingFolder = Registry.CurrentUser.OpenSubKey(FolderClassPath, false))
            {
                if (existingFolder != null)
                {
                    error = "检测到其他程序已经自定义当前用户的文件夹打开方式。为避免覆盖它，轻页没有开启新标签接管。";
                    return false;
                }
            }

            var command = GetCommand(AppPaths.ExecutablePath);
            try
            {
                using (var commandKey = Registry.CurrentUser.CreateSubKey(CommandKeyPath, true)
                                        ?? throw new InvalidOperationException("无法创建当前用户的文件夹打开设置。"))
                {
                    commandKey.SetValue(string.Empty, command, RegistryValueKind.String);
                    // An empty per-user value suppresses the inherited system
                    // DelegateExecute handler so the explicit command is used.
                    commandKey.SetValue("DelegateExecute", string.Empty, RegistryValueKind.String);
                }

                WriteOwnership(command);
            }
            catch
            {
                TryRemoveExactCommand(command, allowCurrentCommand: false);
                ClearOwnership();
                throw;
            }

            NotifyAssociationChanged();
            if (IsEnabled())
            {
                error = string.Empty;
                return true;
            }

            TryRemoveExactCommand(command, allowCurrentCommand: false);
            ClearOwnership();
            error = "文件夹打开命令写入后未通过完整性检查。";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryDisable(out string error)
    {
        try
        {
            if (!IsOwned(out var installedCommand))
            {
                error = string.Empty;
                return true;
            }

            if (string.IsNullOrWhiteSpace(installedCommand))
            {
                error = "新标签接管的所有权记录不完整；轻页没有删除无法确认归属的注册项。";
                return false;
            }

            if (!TryRemoveExactCommand(installedCommand!, allowCurrentCommand: true))
            {
                error = "文件夹打开方式在启用后又被修改。为保护其他程序的设置，轻页已停止清理。";
                return false;
            }

            ClearOwnership();
            NotifyAssociationChanged();
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool IsOwned(out string? installedCommand)
    {
        using var stateKey = Registry.CurrentUser.OpenSubKey(StateKeyPath, false);
        var owned = stateKey?.GetValue(OwnedValueName) is int value && value == 1;
        installedCommand = owned ? ReadString(stateKey!, InstalledCommandValueName) : null;
        return owned;
    }

    private static void WriteOwnership(string command)
    {
        using var stateKey = Registry.CurrentUser.CreateSubKey(StateKeyPath, true)
                             ?? throw new InvalidOperationException("无法保存轻页的当前用户状态。");
        stateKey.SetValue(OwnedValueName, 1, RegistryValueKind.DWord);
        stateKey.SetValue(InstalledCommandValueName, command, RegistryValueKind.String);
    }

    private static void ClearOwnership()
    {
        using var stateKey = Registry.CurrentUser.OpenSubKey(StateKeyPath, true);
        stateKey?.DeleteValue(OwnedValueName, false);
        stateKey?.DeleteValue(InstalledCommandValueName, false);
        // Remove the experimental v0.2 development value if present.
        stateKey?.DeleteValue("DirectFolderOpenServer", false);
    }

    private static bool TryRemoveExactCommand(string installedCommand, bool allowCurrentCommand)
    {
        using (var commandKey = Registry.CurrentUser.OpenSubKey(CommandKeyPath, true))
        {
            if (commandKey != null)
            {
                var actualCommand = ReadString(commandKey, string.Empty);
                var delegateExecute = ReadString(commandKey, "DelegateExecute");
                var matchesInstalled = EqualsIgnoreCase(actualCommand, installedCommand);
                var matchesCurrent = allowCurrentCommand
                                     && EqualsIgnoreCase(actualCommand, GetCommand(AppPaths.ExecutablePath));
                if ((!matchesInstalled && !matchesCurrent)
                    || !string.Equals(delegateExecute, string.Empty, StringComparison.Ordinal))
                    return false;

                commandKey.DeleteValue(string.Empty, false);
                commandKey.DeleteValue("DelegateExecute", false);
            }
        }

        DeleteKeyIfEmpty(CommandKeyPath);
        DeleteKeyIfEmpty(OpenKeyPath);
        DeleteKeyIfEmpty(ShellKeyPath);
        DeleteKeyIfEmpty(FolderClassPath);
        return true;
    }

    private static void DeleteKeyIfEmpty(string path)
    {
        using (var key = Registry.CurrentUser.OpenSubKey(path, false))
        {
            if (key == null || key.SubKeyCount != 0 || key.ValueCount != 0) return;
        }

        Registry.CurrentUser.DeleteSubKey(path, false);
    }

    private static string? ReadString(RegistryKey key, string valueName)
    {
        return key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    private static bool EqualsIgnoreCase(string? left, string? right)
    {
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetCommand(string executablePath)
    {
        return $"\"{executablePath}\" --open-tab \"%1\"";
    }

    private static void NotifyAssociationChanged()
    {
        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
}
