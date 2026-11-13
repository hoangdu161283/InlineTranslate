using System;
using System.IO;

namespace InlineTranslate;

/// <summary>Log chẩn đoán: %TEMP%\InlineTranslate.log (tự cắt khi > 512KB).</summary>
static class Log
{
    public static readonly string FilePath =
        Path.Combine(Path.GetTempPath(), "InlineTranslate.log");

    private static readonly object _lock = new();

    public static void Write(string msg)
    {
        try
        {
            lock (_lock)
            {
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 512 * 1024) fi.Delete();
                File.AppendAllText(FilePath, $"{DateTime.Now:HH:mm:ss.fff} {msg}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
