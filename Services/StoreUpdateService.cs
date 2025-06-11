using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.Services.Store;

namespace ScreenTimeTracker.Services;

/// <summary>
/// Provides helper methods for querying Microsoft Store for app updates and installing them programmatically.
/// </summary>
public static class StoreUpdateService
{
    /// <summary>
    /// Checks the Microsoft Store for updates to the current application and, if found, downloads and installs them.
    /// </summary>
    /// <returns>
    /// True when a newer version was successfully installed and a restart is required; otherwise, false.
    /// </returns>
    public static async Task<bool> CheckAndApplyUpdatesAsync()
    {
        try
        {
            var context = StoreContext.GetDefault();

            // Query the Store for any updates available to this package (including optional packages)
            var updates = await context.GetAppAndOptionalStorePackageUpdatesAsync();
            if (updates == null || updates.Count == 0)
            {
                return false; // Already up to date
            }

            Debug.WriteLine($"StoreUpdateService: Found {updates.Count} update(s). Starting download …");

            var result = await context.RequestDownloadAndInstallStorePackageUpdatesAsync(updates);
            Debug.WriteLine($"StoreUpdateService: Overall update state = {result.OverallState}");

            // Completed = all updates installed; Any other result means installation failed or was canceled
            return result.OverallState == StorePackageUpdateState.Completed;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"StoreUpdateService: Failed to check / apply updates – {ex.Message}");
            return false;
        }
    }
} 