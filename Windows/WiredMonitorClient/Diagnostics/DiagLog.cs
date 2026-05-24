using System.IO;

namespace WiredMonitorClient.Diagnostics;

public static class DiagLog
{
    private static readonly object Gate = new();
    private static readonly string PathValue = System.IO.Path.Combine(AppContext.BaseDirectory, "diag.log");

    public static string Path => PathValue;

    public static void Write(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}";
        lock (Gate)
        {
            File.AppendAllText(PathValue, line);
        }
    }

    public static void Write(Exception ex, string message)
    {
        Write($"{message}: {ex}");
    }
}
