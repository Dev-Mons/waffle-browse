using System.Diagnostics;
using System.IO;
using Waffle.Browse.App.Settings;

namespace Waffle.Browse.App.Diagnostics;

internal static class FocusTraceLogger
{
    public const string EnabledEnvironmentVariable = "WAFFLE_BROWSE_FOCUS_TRACE";
    public const string PathEnvironmentVariable = "WAFFLE_BROWSE_FOCUS_TRACE_PATH";

    private static readonly object Sync = new();
    private static bool? isEnabled;
    private static string? logPath;

    public static bool IsEnabled
    {
        get
        {
            isEnabled ??= ResolveEnabled();
            return isEnabled.Value;
        }
    }

    public static string LogPath
    {
        get
        {
            logPath ??= ResolveLogPath();
            return logPath;
        }
    }

    public static void StartSession()
    {
        WriteLine("=== Waffle Browse focus trace session started ===");
    }

    public static void Write(FocusTraceEntry entry)
    {
        WriteLine(entry.Format());
    }

    public static void WriteLine(string line)
    {
        if (!IsEnabled)
        {
            return;
        }

        try
        {
            lock (Sync)
            {
                var path = LogPath;
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.AppendAllText(path, line + Environment.NewLine);
            }

            Debug.WriteLine(line);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static bool ResolveEnabled()
    {
        var value = Environment.GetEnvironmentVariable(EnabledEnvironmentVariable);
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveLogPath()
    {
        var explicitPath = Environment.GetEnvironmentVariable(PathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return explicitPath;
        }

        return Path.Combine(ApplicationDataPath.Resolve(), "focus-trace.log");
    }
}
