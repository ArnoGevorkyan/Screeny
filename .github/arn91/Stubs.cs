using System.Collections.Concurrent;

internal sealed class FixtureDispatcher : SynchronizationContext
{
    private readonly ConcurrentQueue<Action> _actions = new();
    public bool TryEnqueue(Action action) { _actions.Enqueue(action); return true; }
    public override void Post(SendOrPostCallback callback, object? state) => TryEnqueue(() => callback(state));
    public void Pump() { while (_actions.TryDequeue(out var action)) action(); }
}
internal sealed class FixtureTimer { public event EventHandler? Tick; public void Start() { } public void Stop() { } }
internal sealed class FrameworkElement { public object XamlRoot { get; } = new(); public event EventHandler? Loaded; }
internal sealed class AppWindow { public event EventHandler? Closing; public void Show() { } }
internal sealed class TrayIconHelper
{
    public event EventHandler? ShowClicked;
    public event EventHandler? ExitClicked;
    public void Dispose() { }
}
internal static class ToolTipService { public static void SetToolTip(object ctl, string text) { } }
internal enum ContentDialogResult { Primary, None }
internal enum ContentDialogButton { Close }
internal sealed class ContentDialog
{
    public static Func<string, ContentDialogResult> OnShow = _ => ContentDialogResult.None;
    public string Title { get; init; } = "";
    public object? Content { get; init; }
    public string? PrimaryButtonText { get; init; }
    public string? CloseButtonText { get; init; }
    public object? XamlRoot { get; init; }
    public ContentDialogButton DefaultButton { get; init; }
    public Task<ContentDialogResult> ShowAsync() => Task.FromResult(OnShow(Title));
}
internal sealed class Application
{
    public static readonly Application Current = new();
    public Action OnExit = () => throw new Exception("Unexpected exit");
    public void Exit() => OnExit();
}
namespace ScreenTimeTracker
{
    internal static class App
    {
        public static readonly List<string> Logs = new();
        public static void WriteToLog(string message) => Logs.Add(message);
    }
}
