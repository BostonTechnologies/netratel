using System;
using System.Buffers;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NetRatel.Client.Service.RemoteDesktop;

/// <summary>Reads the resident helper's UTF-8 frames without buffering past the frame limit.</summary>
internal sealed class BoundedHelperPipeReader(Stream stream)
{
    public const int MaximumHelloBytes = 16 * 1024;
    public const int MaximumMessageBytes = 2 * 1024 * 1024;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private readonly byte[] _buffer = new byte[4096];
    private int _offset;
    private int _end;
    private bool _firstFrame = true;

    public async Task<string?> ReadAsync(int maximumBytes, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var frame = new ArrayBufferWriter<byte>();
        while (true)
        {
            if (_offset == _end)
            {
                _offset = 0;
                _end = await stream.ReadAsync(_buffer, deadline.Token).ConfigureAwait(false);
                if (_end == 0)
                {
                    if (frame.WrittenCount == 0)
                        return null;
                    throw new InvalidDataException("Incomplete helper pipe frame.");
                }
            }

            var newline = Array.IndexOf(_buffer, (byte)'\n', _offset, _end - _offset);
            var count = (newline < 0 ? _end : newline) - _offset;
            if (count > maximumBytes - frame.WrittenCount)
                throw new InvalidDataException("Helper pipe frame exceeds its limit.");

            frame.Write(_buffer.AsSpan(_offset, count));
            _offset += count;
            if (newline >= 0)
            {
                _offset++;
                return Decode(frame.WrittenSpan);
            }
        }
    }

    private string Decode(ReadOnlySpan<byte> bytes)
    {
        if (_firstFrame && bytes.StartsWith(Encoding.UTF8.Preamble))
            bytes = bytes[Encoding.UTF8.Preamble.Length..];
        _firstFrame = false;
        return Utf8.GetString(bytes);
    }
}
