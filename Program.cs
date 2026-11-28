using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Win32.SafeHandles;
using ScreenTimeTracker.Services;
using Windows.ApplicationModel.Activation;

namespace ScreenTimeTracker;

internal static class Program
{
    private const int SharingViolation = unchecked((int)0x80070020);
    private const int NoPackage = 15700;
    private const byte ShowWindow = 1;
    private static FileStream? _profileLease;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            string databasePath = Path.GetFullPath(DatabaseService.DefaultDatabasePath);
            Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
            try
            {
                _profileLease = File.Open(databasePath + ".owner", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex) when (ex.HResult == SharingViolation)
            {
            }

            string family = GetPackageFamily();
            WinRT.ComWrappersSupport.InitializeComWrappers();
            bool startupTask = family.Length != 0 &&
                Windows.ApplicationModel.AppInstance.GetActivatedEventArgs()?.Kind == ActivationKind.StartupTask;
            using var process = Process.GetCurrentProcess();
            string identity = $"{process.SessionId}|{family}|{databasePath.ToUpperInvariant()}";
            string pipeName = "Screeny.Activate." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));

            if (_profileLease is null)
            {
                if (startupTask || args.Any(arg =>
                    arg.Equals("--startup", StringComparison.OrdinalIgnoreCase) ||
                    arg.Equals("/startup", StringComparison.OrdinalIgnoreCase) ||
                    arg.Equals("-startup", StringComparison.OrdinalIgnoreCase)))
                {
                    return 0;
                }

                using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out,
                    PipeOptions.CurrentUserOnly, TokenImpersonationLevel.Anonymous);
                client.Connect(5000);
                if (!GetNamedPipeServerProcessId(client.SafePipeHandle, out uint ownerPid))
                {
                    return 1;
                }

                AllowSetForegroundWindow(ownerPid);
                client.WriteByte(ShowWindow);
                return 0;
            }

            using var user = WindowsIdentity.GetCurrent();
            var security = new PipeSecurity();
            security.SetOwner(user.Owner!);
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
                PipeAccessRights.FullControl, AccessControlType.Deny));
            security.AddAccessRule(new PipeAccessRule(user.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
            using var server = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.In, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
            using var stopping = new CancellationTokenSource();
            try
            {
                Application.Start(initialization =>
                {
                    var dispatcher = DispatcherQueue.GetForCurrentThread();
                    SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(dispatcher));
                    var app = new App(startupTask);
                    _ = Task.Run(() => ListenAsync(server, dispatcher, app, stopping.Token));
                });
            }
            finally
            {
                stopping.Cancel();
            }
            return 0;
        }
        catch (Exception ex)
        {
            App.WriteToLog($"Screeny launch failed: {ex.GetType().Name} (0x{ex.HResult:X8}).");
            return 1;
        }
    }

    private static async Task ListenAsync(NamedPipeServerStream server, DispatcherQueue dispatcher,
        App app, CancellationToken stopping)
    {
        var command = new byte[1];
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                await server.WaitForConnectionAsync(stopping);
                using var readDeadline = CancellationTokenSource.CreateLinkedTokenSource(stopping);
                readDeadline.CancelAfter(TimeSpan.FromSeconds(2));
                try
                {
                    if (await server.ReadAsync(command, readDeadline.Token) == 1 && command[0] == ShowWindow)
                    {
                        dispatcher.TryEnqueue(app.ShowExistingWindow);
                    }
                }
                catch (OperationCanceledException) when (readDeadline.IsCancellationRequested)
                {
                }
                catch (IOException)
                {
                }
                finally
                {
                    server.Disconnect();
                }
            }
        }
        catch (Exception ex) when (stopping.IsCancellationRequested ||
            ex is IOException or UnauthorizedAccessException)
        {
            if (!stopping.IsCancellationRequested)
            {
                App.WriteToLog($"Screeny activation listener stopped: 0x{ex.HResult:X8}.");
            }
        }
    }

    private static string GetPackageFamily()
    {
        uint length = 65;
        var family = new StringBuilder((int)length);
        return GetCurrentPackageFamilyName(ref length, family) switch
        {
            0 => family.ToString(),
            NoPackage => string.Empty,
            int error => throw new System.ComponentModel.Win32Exception(error)
        };
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetCurrentPackageFamilyName(ref uint length, StringBuilder family);

    [DllImport("kernel32.dll")]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
