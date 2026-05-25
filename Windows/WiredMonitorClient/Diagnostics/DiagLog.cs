using System.IO;

namespace WiredMonitorClient.Diagnostics;

public static class DiagLog
{
    private static readonly object Gate = new();
    private static readonly string PathValue = System.IO.Path.Combine(AppContext.BaseDirectory, "diag.log");
    private static readonly StreamWriter Writer = new(new FileStream(
        PathValue,
        FileMode.Append,
        FileAccess.Write,
        FileShare.ReadWrite))
    {
        AutoFlush = true,
    };

    public static string Path => PathValue;

    public static void Write(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        lock (Gate)
        {
            Writer.WriteLine(line);
        }
    }

    public static void Write(Exception ex, string message)
    {
        Write($"{message}: {ex}");
    }
}
