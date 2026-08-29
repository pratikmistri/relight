using System;
using System.IO;
using System.Threading;

namespace Relight.Services;

/// <summary>Appends timestamped diagnostics to a log file next to the executable.</summary>
public static class DiagnosticLog
{
    private static readonly Lock Gate = new();
    private static readonly string Path = System.IO.Path.Combine(AppContext.BaseDirectory, "relight.log");

    static DiagnosticLog()
    {
        try
        {
            File.WriteAllText(Path, $"=== Relight started {DateTime.Now:O} ==={Environment.NewLine}");
        }
        catch (IOException)
        {
            // Diagnostics are best effort.
        }
    }

    public static string Location => Path;

    public static void Write(string message)
    {
        lock (Gate)
        {
            try
            {
                File.AppendAllText(Path, $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
            catch (IOException)
            {
                // Diagnostics are best effort.
            }
        }
    }
}
