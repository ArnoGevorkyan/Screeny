// Test-only stand-in for the WinRT media API. Only the members WindowTrackingService touches.
namespace Windows.Foundation { internal static class ShimMarker { } }
namespace Windows.Media.Control
{
    public enum GlobalSystemMediaTransportControlsSessionPlaybackStatus { Closed, Opened, Changing, Stopped, Playing, Paused }
    public sealed class GlobalSystemMediaTransportControlsSessionPlaybackInfo
    {
        public GlobalSystemMediaTransportControlsSessionPlaybackStatus PlaybackStatus { get; init; }
    }
    public sealed class GlobalSystemMediaTransportControlsSession
    {
        public GlobalSystemMediaTransportControlsSessionPlaybackInfo GetPlaybackInfo() => new() { PlaybackStatus = MediaShim.Playing ? GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing : GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused };
    }
    public sealed class GlobalSystemMediaTransportControlsSessionManager
    {
        public static Task<GlobalSystemMediaTransportControlsSessionManager> RequestAsync() => Task.FromResult(new GlobalSystemMediaTransportControlsSessionManager());
        public IReadOnlyList<GlobalSystemMediaTransportControlsSession> GetSessions() => new[] { new GlobalSystemMediaTransportControlsSession() };
    }
    public static class MediaShim { public static volatile bool Playing; }
}
