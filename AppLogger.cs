using System;
using System.IO;
using System.Text;
using System.Threading;

namespace FloatingTodo;

internal static class AppLogger
{
    private const long MaxLogBytes = 2 * 1024 * 1024;
    private static readonly object Gate = new();

    public static string LogPath { get; } = Path.Combine(AppContext.BaseDirectory, "FloatingTodo.log");

    public static void Warning(
        string message,
        Exception exception,
        params (string Key, string? Value)[] context) =>
        Write("WARN", message, exception, context);

    public static void Error(
        string message,
        Exception exception,
        params (string Key, string? Value)[] context) =>
        Write("ERROR", message, exception, context);

    private static void Write(
        string level,
        string message,
        Exception exception,
        params (string Key, string? Value)[] context)
    {
        try
        {
            lock (Gate)
            {
                RotateIfNeeded();
                var builder = new StringBuilder()
                    .Append('[').Append(DateTimeOffset.Now.ToString("O")).Append("] ")
                    .Append(level)
                    .Append(" [process=").Append(Environment.ProcessId)
                    .Append(", thread=").Append(Thread.CurrentThread.ManagedThreadId)
                    .Append("] ")
                    .AppendLine(message);

                foreach (var (key, value) in context)
                {
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        builder.Append("  ").Append(key).Append(": ").AppendLine(Sanitize(value));
                    }
                }

                builder.AppendLine(exception.ToString()).AppendLine();
                File.AppendAllText(LogPath, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
        }
        catch
        {
            // Logging must never turn a recoverable application error into a crash.
        }
    }

    private static void RotateIfNeeded()
    {
        var file = new FileInfo(LogPath);
        if (!file.Exists || file.Length < MaxLogBytes)
        {
            return;
        }

        File.Move(LogPath, LogPath + ".old", overwrite: true);
    }

    private static string Sanitize(string value) =>
        value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}
