using System.Text;

namespace CaddyManager.Ops.Logs;

/// <summary>
/// Reads the last N lines of a (possibly huge, possibly still being written) text file by scanning
/// backwards from the end in chunks — cost is proportional to the lines returned, not the file size.
/// Files are opened with FileShare.ReadWrite | Delete so Caddy / the logger can keep writing and rolling.
/// </summary>
internal static class LogTail
{
    public const int MaxLines = 5000;
    private const int ChunkSize = 64 * 1024;
    /// <summary>Longest single line kept; longer lines keep their last part and are prefixed with "…".</summary>
    internal const int MaxLineBytes = 64 * 1024;
    /// <summary>
    /// Upper bound on bytes scanned when a filter is applied (keeps the worst case bounded). Caddy rolls its logs
    /// at 20 MB, so 64 MB covers a whole active file while bounding the cost of a viewer's request.
    /// </summary>
    public const long MaxScanBytesFiltered = 64L * 1024 * 1024;

    public static List<string> Read(string path, int lines, string? filter = null, long maxScanBytes = MaxScanBytesFiltered)
    {
        lines = Math.Clamp(lines, 1, MaxLines);
        var result = new List<string>(Math.Min(lines, 1024));
        if (!File.Exists(path)) return result;

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1, FileOptions.RandomAccess);
        var q = string.IsNullOrWhiteSpace(filter) ? null : filter.Trim();
        var pos = fs.Length;
        var scanned = 0L;
        var chunk = new byte[ChunkSize];

        // Pieces of the line being assembled when it spans chunks, from the line's end backwards.
        var carry = new List<byte[]>();
        var carryLength = 0;
        var overflow = false;
        var first = true;

        void Emit(ReadOnlySpan<byte> head)
        {
            var isFirst = first;
            first = false;
            byte[] bytes;
            if (overflow)
            {
                // Line exceeded MaxLineBytes: keep only its tail (what is in carry).
                bytes = Concat(ReadOnlySpan<byte>.Empty, carry, carryLength);
            }
            else bytes = Concat(head, carry, carryLength);
            var truncated = overflow;
            carry.Clear();
            carryLength = 0;
            overflow = false;

            // The segment after a trailing newline at end of file is not a line.
            if (isFirst && bytes.Length == 0) return;
            var len = bytes.Length;
            if (len > 0 && bytes[len - 1] == '\r') len--;
            var start = 0;
            if (len > MaxLineBytes)
            {
                start = len - MaxLineBytes;
                truncated = true;
            }
            var text = Encoding.UTF8.GetString(bytes, start, len - start);
            if (truncated) text = "…" + text;
            if (q is null || text.Contains(q, StringComparison.OrdinalIgnoreCase))
                result.Add(text);
        }

        while (pos > 0 && result.Count < lines && (q is null || scanned < maxScanBytes))
        {
            var size = (int)Math.Min(ChunkSize, pos);
            pos -= size;
            fs.Position = pos;
            fs.ReadExactly(chunk, 0, size);
            scanned += size;

            var end = size;
            for (var i = size - 1; i >= 0 && result.Count < lines; i--)
            {
                if (chunk[i] != (byte)'\n') continue;
                Emit(chunk.AsSpan(i + 1, end - i - 1));
                end = i;
            }
            if (result.Count >= lines) break;
            if (end > 0)
            {
                if (carryLength + end <= MaxLineBytes)
                {
                    carry.Add(chunk.AsSpan(0, end).ToArray());
                    carryLength += end;
                }
                else overflow = true;
            }
            if (pos == 0) Emit(ReadOnlySpan<byte>.Empty); // first line of the file
        }

        result.Reverse();
        return result;
    }

    private static byte[] Concat(ReadOnlySpan<byte> head, List<byte[]> carry, int carryLength)
    {
        var bytes = new byte[head.Length + carryLength];
        head.CopyTo(bytes);
        var offset = head.Length;
        for (var i = carry.Count - 1; i >= 0; i--)
        {
            carry[i].CopyTo(bytes, offset);
            offset += carry[i].Length;
        }
        return bytes;
    }
}
