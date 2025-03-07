using Microsoft.UI.Dispatching;

namespace ScreenTimeTracker
{
    public static class DispatcherHelper
    {
        private static DispatcherQueue? _mainDispatcherQueue;

        public static void Initialize(DispatcherQueue dispatcherQueue)
        {
            _mainDispatcherQueue = dispatcherQueue;
        }

        public static DispatcherQueue? MainDispatcherQueue => _mainDispatcherQueue;

        public static bool EnqueueOnUIThread(DispatcherQueueHandler callback)
        {
            return _mainDispatcherQueue?.TryEnqueue(callback) ?? false;
        }
    }
} 