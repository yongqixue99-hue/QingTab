using System;

namespace QingTab.Hooks;

public delegate bool TryRestoreWindowsFolderOpen(out string error);

public sealed class ApplicationExitPreparation
{
    internal ApplicationExitPreparation(
        bool canExit,
        bool windowsFolderOpenRestored,
        string error)
    {
        CanExit = canExit;
        WindowsFolderOpenRestored = windowsFolderOpenRestored;
        Error = error;
    }

    public bool CanExit { get; }
    public bool WindowsFolderOpenRestored { get; }
    public string Error { get; }
}

/// <summary>
/// Defines the visible Exit command's safety contract: QingTab may terminate
/// only after its owned Folder-open override has been removed, so a later
/// folder click cannot silently relaunch the resident.
/// </summary>
public static class ApplicationExitPolicy
{
    public static ApplicationExitPreparation Prepare(
        TryRestoreWindowsFolderOpen restoreWindowsFolderOpen)
    {
        if (restoreWindowsFolderOpen == null)
            throw new ArgumentNullException(nameof(restoreWindowsFolderOpen));

        try
        {
            if (restoreWindowsFolderOpen(out var error))
            {
                return new ApplicationExitPreparation(
                    canExit: true,
                    windowsFolderOpenRestored: true,
                    error: string.Empty);
            }

            return new ApplicationExitPreparation(
                canExit: false,
                windowsFolderOpenRestored: false,
                error: error ?? string.Empty);
        }
        catch (Exception ex)
        {
            return new ApplicationExitPreparation(
                canExit: false,
                windowsFolderOpenRestored: false,
                error: ex.Message);
        }
    }
}
