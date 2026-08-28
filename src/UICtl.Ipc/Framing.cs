using System.Text;

namespace UICtl.Ipc;

/// <summary>A length-prefixed (4-byte little-endian) UTF-8 JSON message, one request/response per connection. See ENGINEERING.md.</summary>
internal static class Framing
{
    private const int MaxMessageBytes = 64 * 1024 * 1024;

    public static async Task WriteMessageAsync(Stream stream, string json, CancellationToken ct = default)
    {
        byte[] payload = Encoding.UTF8.GetBytes(json);
        byte[] header = BitConverter.GetBytes(payload.Length);
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(payload, ct);
        await stream.FlushAsync(ct);
    }

    public static async Task<string> ReadMessageAsync(Stream stream, CancellationToken ct = default)
    {
        byte[] header = await ReadExactAsync(stream, 4, ct);
        int length = BitConverter.ToInt32(header);
        if (length < 0 || length > MaxMessageBytes)
            throw new IOException($"invalid message length {length}");

        byte[] payload = await ReadExactAsync(stream, length, ct);
        return Encoding.UTF8.GetString(payload);
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int count, CancellationToken ct)
    {
        byte[] buffer = new byte[count];
        int offset = 0;
        while (offset < count)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), ct);
            if (read == 0)
                throw new IOException("connection closed while reading a message");
            offset += read;
        }
        return buffer;
    }
}
