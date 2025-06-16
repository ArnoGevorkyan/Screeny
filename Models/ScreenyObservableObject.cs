using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ScreenTimeTracker.Models
{
    /// <summary>
    /// Simple base class that provides <see cref="INotifyPropertyChanged"/> plumbing for view-model style objects.
    /// </summary>
    public abstract class ScreenyObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// Raises <see cref="PropertyChanged"/> for the supplied member name.
        /// </summary>
        protected void NotifyPropertyChanged([CallerMemberName] string propertyName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
} 