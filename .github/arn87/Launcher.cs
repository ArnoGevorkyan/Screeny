using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static class Launcher
{
    [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        [PreserveSig]
        int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string id,
            [MarshalAs(UnmanagedType.LPWStr)] string arguments, uint options, out uint pid);
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [STAThread]
    private static void Main(string[] args)
    {
        var lines = File.ReadAllLines(args[0]);
        using (var window = new Form { Text = "Screeny probe launcher", TopMost = true,
            StartPosition = FormStartPosition.Manual, Location = new Point(24, 24), Size = new Size(300, 100) })
        using (var timer = new Timer { Interval = 300 })
        {
            window.Shown += (sender, e) => { window.Activate(); timer.Start(); };
            timer.Tick += (sender, e) =>
            {
                uint foreground;
                GetWindowThreadProcessId(GetForegroundWindow(), out foreground);
                if (foreground != Process.GetCurrentProcess().Id) return;
                timer.Stop();
                try
                {
                    var count = int.Parse(lines[3]);
                    var requests = new Task<uint>[count];
                    for (var i = 0; i < count; i++)
                    {
                        requests[i] = Task.Run(() => Launch(lines[0], lines[1], lines[2]));
                    }
                    Task.WaitAll(requests);
                    File.WriteAllText(args[0] + ".tmp", string.Join(",", requests.Select(r => r.Result)));
                }
                catch (Exception ex)
                {
                    File.WriteAllText(args[0] + ".tmp", "ERROR " + ex.GetType().Name + " 0x" + ex.HResult.ToString("X8"));
                }
                File.Move(args[0] + ".tmp", args[0] + ".out");
            };
            Application.Run(window);
        }
    }

    private static uint Launch(string mode, string target, string argument)
    {
        if (mode == "loose")
        {
            var info = new ProcessStartInfo(target, argument) { WorkingDirectory = Path.GetDirectoryName(target), UseShellExecute = true };
            using (var p = Process.Start(info)) return (uint)p.Id;
        }

        var cls = Type.GetTypeFromCLSID(new Guid("45ba127d-10a8-46ea-8ab7-56ea9078943c"), true);
        var manager = (IApplicationActivationManager)Activator.CreateInstance(cls);
        try
        {
            uint pid;
            int hr = manager.ActivateApplication(target, argument.Length == 0 ? null : argument, 0, out pid);
            Marshal.ThrowExceptionForHR(hr);
            return pid;
        }
        finally
        {
            Marshal.ReleaseComObject(manager);
        }
    }
}
