using System;
using System.IO;
using System.Text;

namespace QingTab.Helpers;

/// <summary>
/// Bounded UTF-8 text log. Rotation happens immediately before an append that
/// would exceed the configured size, and archives are numbered newest-first.
/// </summary>
public sealed class RotatingTextLog
{
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly string _path;
    private readonly long _maximumBytes;
    private readonly int _archiveCount;
    private readonly object _sync = new();

    public RotatingTextLog(string path, long maximumBytes, int archiveCount)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("日志路径不能为空。", nameof(path));
        if (maximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (archiveCount < 0) throw new ArgumentOutOfRangeException(nameof(archiveCount));

        _path = path;
        _maximumBytes = maximumBytes;
        _archiveCount = archiveCount;
    }

    public void Append(string text)
    {
        if (text == null) throw new ArgumentNullException(nameof(text));

        lock (_sync)
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var boundedText = LimitToMaximumBytes(text);
            EnsureExistingFileIsBounded(_path);

            var incomingBytes = Utf8WithoutBom.GetByteCount(boundedText);
            var existingBytes = File.Exists(_path) ? new FileInfo(_path).Length : 0;
            if (existingBytes > 0 && existingBytes + incomingBytes > _maximumBytes)
                Rotate();

            File.AppendAllText(_path, boundedText, Utf8WithoutBom);
        }
    }

    private string LimitToMaximumBytes(string text)
    {
        var bytes = Utf8WithoutBom.GetBytes(text);
        if (bytes.LongLength <= _maximumBytes) return text;

        var length = (int)Math.Min(_maximumBytes, int.MaxValue);
        while (length > 0 && (bytes[length] & 0xC0) == 0x80)
            length--;
        return Utf8WithoutBom.GetString(bytes, 0, length);
    }

    private void EnsureExistingFileIsBounded(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length <= _maximumBytes) return;

        var boundedText = LimitToMaximumBytes(File.ReadAllText(path, Utf8WithoutBom));
        File.WriteAllText(path, boundedText, Utf8WithoutBom);
    }

    private void Rotate()
    {
        if (_archiveCount == 0)
        {
            File.Delete(_path);
            return;
        }

        var oldestArchive = GetArchivePath(_archiveCount);
        if (File.Exists(oldestArchive))
            File.Delete(oldestArchive);

        for (var index = _archiveCount - 1; index >= 1; index--)
        {
            var source = GetArchivePath(index);
            if (!File.Exists(source)) continue;

            EnsureExistingFileIsBounded(source);

            var destination = GetArchivePath(index + 1);
            if (File.Exists(destination))
                File.Delete(destination);
            File.Move(source, destination);
        }

        if (File.Exists(_path))
            File.Move(_path, GetArchivePath(1));
    }

    private string GetArchivePath(int index)
    {
        return _path + "." + index;
    }
}
