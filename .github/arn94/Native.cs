using System;
using System.Linq;
using System.Runtime.InteropServices;
using Windows.Automation;

public static class DiagnosticsProbe
{
    [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        [PreserveSig]
        int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string id,
            [MarshalAs(UnmanagedType.LPWStr)] string args, uint options, out uint pid);
    }

    public static int Activate(string aumid, string args)
    {
        var cls = Type.GetTypeFromCLSID(new Guid("45ba127d-10a8-46ea-8ab7-56ea9078943c"), true);
        var manager = (IApplicationActivationManager)Activator.CreateInstance(cls);
        try
        {
            uint pid;
            Marshal.ThrowExceptionForHR(manager.ActivateApplication(aumid, args, 0, out pid));
            return (int)pid;
        }
        finally { Marshal.ReleaseComObject(manager); }
    }

    public static AutomationElement Error(int pid)
    {
        return AutomationElement.RootElement.FindFirst(TreeScope.Children,
            new AndCondition(new PropertyCondition(AutomationElement.ProcessIdProperty, pid),
                new PropertyCondition(AutomationElement.NameProperty, "Critical Application Error")));
    }

    public static string Contents(AutomationElement error)
    {
        return string.Join("\n", error.FindAll(TreeScope.Descendants, Condition.TrueCondition)
            .Cast<AutomationElement>().Select(item => item.Current.Name));
    }

    public static bool Dismiss(AutomationElement error)
    {
        var ok = error.FindFirst(TreeScope.Descendants,
            new AndCondition(new PropertyCondition(AutomationElement.NameProperty, "OK"),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)));
        object invoke;
        if (ok == null || !ok.TryGetCurrentPattern(InvokePattern.Pattern, out invoke)) return false;
        ((InvokePattern)invoke).Invoke();
        return true;
    }
}
