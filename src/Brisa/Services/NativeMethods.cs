using System.Runtime.InteropServices;

namespace Brisa.Services;

internal static class NativeMethods
{
    /// <summary>ASFW_ANY (-1): lets the process that receives the signal take the foreground.</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AllowSetForegroundWindow(int processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern uint RegisterWindowMessage(string name);

    /// <summary>MSGFLT_ADD (1): lets a message from a lower integrity process, such as Explorer, reach this one.</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ChangeWindowMessageFilter(uint message, uint flag);
}
