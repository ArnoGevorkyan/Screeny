namespace Windows.Foundation { }

namespace Windows.Media.Control
{
    internal abstract class GlobalSystemMediaTransportControlsSessionManager
    {
        public static Task<GlobalSystemMediaTransportControlsSessionManager> RequestAsync() =>
            throw new PlatformNotSupportedException();

        public abstract IEnumerable<MediaSession> GetSessions();
    }

    internal abstract class MediaSession
    {
        public abstract PlaybackInfo GetPlaybackInfo();
    }

    internal abstract class PlaybackInfo
    {
        public abstract GlobalSystemMediaTransportControlsSessionPlaybackStatus PlaybackStatus { get; }
    }

    internal enum GlobalSystemMediaTransportControlsSessionPlaybackStatus { Playing }
}
