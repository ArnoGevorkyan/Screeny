namespace Windows.UI { public record struct Color(byte R, byte G, byte B); }
namespace Windows.Foundation { }
namespace Windows.Media.Control
{
    public enum GlobalSystemMediaTransportControlsSessionPlaybackStatus { Playing }
    public sealed class GlobalSystemMediaTransportControlsSessionManager
    {
        public static Task<GlobalSystemMediaTransportControlsSessionManager> RequestAsync() => throw new NotSupportedException();
        public IEnumerable<GlobalSystemMediaTransportControlsSessionManager> GetSessions() => throw new NotSupportedException();
        public GlobalSystemMediaTransportControlsSessionManager GetPlaybackInfo() => throw new NotSupportedException();
        public GlobalSystemMediaTransportControlsSessionPlaybackStatus PlaybackStatus => throw new NotSupportedException();
    }
}
namespace Microsoft.UI { }
namespace Microsoft.UI.Dispatching { public enum DispatcherQueuePriority { Low } }
namespace Microsoft.UI.Xaml.Controls
{
    public sealed class ContentDialog
    {
        public string? Title, Content, CloseButtonText;
        public object? XamlRoot;
        public Task ShowAsync() => Task.CompletedTask;
    }
}
namespace Microsoft.UI.Xaml
{
    public enum ApplicationTheme { Dark }
    public enum Visibility { Collapsed, Visible }
    public sealed class Application
    {
        public static Application Current { get; } = new();
        public ApplicationTheme RequestedTheme { get; }
        public Dictionary<string, object> Resources { get; } = new();
    }
}
namespace SkiaSharp
{
    public record struct SKColor(byte R, byte G, byte B)
    {
        public SKColor WithAlpha(byte alpha) => this;
    }
    public static class SKColors
    {
        public static SKColor Black, White, DodgerBlue, LightGray;
    }
}
namespace LiveChartsCore
{
    public interface ISeries { }
    public static class LiveCharts
    {
        public static void Configure(Action<Config> configure) => throw new NotSupportedException();
    }
    public sealed class Config
    {
        public Config AddSkiaSharp() => this;
        public Config AddDefaultMappers() => this;
    }
}
namespace LiveChartsCore.Defaults { }
namespace LiveChartsCore.Measure { public enum LegendPosition { Hidden } }
namespace LiveChartsCore.SkiaSharpView
{
    public sealed class ColumnSeries<T> : ISeries
    {
        public IEnumerable<T> Values { get; set; } = [];
        public object? Fill, Stroke, EasingFunction;
        public double Padding, MaxBarWidth;
        public string? Name;
        public TimeSpan AnimationsSpeed;
    }
    public sealed class Axis
    {
        public IEnumerable<string> Labels { get; set; } = [];
        public object? LabelsPaint, SeparatorsPaint, NamePaint;
        public double LabelsRotation, MinStep, TextSize, MinLimit, MaxLimit, NameTextSize;
        public bool ForceStepToMin;
        public string? Name;
        public Func<double, string>? Labeler;
    }
}
namespace LiveChartsCore.SkiaSharpView.Painting { public record SolidColorPaint(SkiaSharp.SKColor Color); }
namespace LiveChartsCore.SkiaSharpView.WinUI
{
    public sealed class CartesianChart
    {
        public double ActualWidth { get; } = 800;
        public IEnumerable<ISeries> Series { get; set; } = [];
        public SkiaSharpView.Axis[] XAxes { get; set; } = [];
        public SkiaSharpView.Axis[] YAxes { get; set; } = [];
        public TimeSpan AnimationsSpeed { get; set; }
        public Measure.LegendPosition LegendPosition { get; set; }
    }
}
