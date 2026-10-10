namespace NetLedger.Service;

/// <summary>Minimal file log in the data directory, rolled over at 1 MB.</summary>
internal static class Log
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly object Gate = new();
    private static string? s_path;
    private static bool s_console;

    public static void Init(string directory, bool console)
    {
        Directory.CreateDirectory(directory);
        s_path = Path.Combine(directory, "service.log");
        s_console = console;
    }

    public static void Info(string message) => Write("INFO ", message);

    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {message}";
        lock (Gate)
        {
            if (s_console)
            {
                Console.WriteLine(line);
            }

            if (s_path is null)
            {
                return;
            }

            try
            {
                var info = new FileInfo(s_path);
                if (info.Exists && info.Length > MaxBytes)
                {
                    File.Move(s_path, Path.ChangeExtension(s_path, ".old.log"), overwrite: true);
                }

                File.AppendAllText(s_path, line + Environment.NewLine);
            }
            catch (IOException)
            {
                // Logging must never take the service down.
            }
        }
    }
}
