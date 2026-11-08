using System.IO;

namespace Playout;

internal static class PlayoutLog
{
    private static readonly object Sync = new();

    public static string GetLogPath(string applicationName) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Playout", "Logs", $"{applicationName}.log");

    public static void Write(string path, string level, string category, string message, Exception? exception = null)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            lock (Sync)
            {
                using FileStream stream = new(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using StreamWriter writer = new(stream);
                writer.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {category}: {message}");
                if (exception is not null)
                    writer.WriteLine(exception);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
