using System.Collections.Generic;
using System.Threading.Tasks;
using ScreenTimeTracker.Models;

namespace ScreenTimeTracker.Services
{
    /// <summary>
    /// Centralises all non-tray icon refresh operations so that UI code does not have to loop over
    /// <see cref="AppUsageRecord"/> items manually.  It deliberately keeps no WinUI/XAML knowledge –
    /// only record manipulation.
    /// </summary>
    public sealed class IconRefreshService
    {
        private bool _primed;

        /// <summary>
        /// Performs an initial one-shot refresh after a small delay.  Subsequent calls become no-ops.
        /// </summary>
        public async Task PrimeIconsAsync(IEnumerable<AppUsageRecord> records)
        {
            if (_primed) return;
            _primed = true;

            // Give the UI a short moment to stabilise so the refresh does not cause noticeable jank.
            await Task.Delay(2000);
            await RefreshIconsAsync(records);
        }

        /// <summary>
        /// Clears cached images then reloads them via <see cref="AppUsageRecord.LoadAppIconIfNeeded"/>.
        /// </summary>
        public async Task RefreshIconsAsync(IEnumerable<AppUsageRecord> records)
        {
            foreach (var rec in records)
            {
                rec.ClearIcon();
                rec.LoadAppIconIfNeeded();
                // Yield control periodically so we do not block the UI thread when called via dispatcher.
                await Task.Yield();
            }
        }
    }
} 