using System.Diagnostics;

namespace FastPick.Services;

public static class DebugService
{
    [Conditional("DEBUG")]
    public static void WriteLine(string message)
    {
        Debug.WriteLine(message);
    }

    [Conditional("DEBUG")]
    public static void WriteLine(string format, params object[] args)
    {
        Debug.WriteLine(string.Format(format, args));
    }

    [Conditional("DEBUG")]
    public static void WriteLine(string category, string message)
    {
        Debug.WriteLine(message, category);
    }
}