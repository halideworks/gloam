using System;
using System.IO;
using System.Text;

namespace Gloam.Core;

/// <summary>Bounded text reads and atomic replacement shared by settings and profiles.</summary>
internal static class TextFileStore
{
    internal static string ReadBounded(string path, long maximumBytes, int maximumCharacters)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > maximumBytes)
            throw new InvalidDataException("File exceeds the size limit.");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (count > maximumCharacters - text.Length)
                throw new InvalidDataException("File exceeds the character limit.");
            text.Append(buffer, 0, count);
        }
        return text.ToString();
    }

    internal static void WriteAtomic(string path, string text,
        long maximumBytes = long.MaxValue, int maximumCharacters = int.MaxValue)
    {
        if (text.Length > maximumCharacters || Encoding.UTF8.GetByteCount(text) > maximumBytes)
            throw new InvalidDataException("Text exceeds the file size limit.");
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        string temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, text);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Keep the original write error; cleanup must not replace it.
                Log.Info($"Could not remove staged text file '{temporary}': {ex.Message}");
            }
        }
    }
}
