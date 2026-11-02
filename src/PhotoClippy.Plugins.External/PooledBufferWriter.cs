using System.Buffers;

namespace PhotoClippy.Plugins.External;

/// <summary>Growable <see cref="IBufferWriter{T}"/> backed by <see cref="ArrayPool{T}.Shared"/>.</summary>
internal sealed class PooledBufferWriter : IBufferWriter<byte>, IDisposable
{
    private byte[] _buffer;
    private int _written;

    public PooledBufferWriter(int initialCapacity = 4096) => _buffer = ArrayPool<byte>.Shared.Rent(initialCapacity);

    public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _written);

    public int WrittenCount => _written;

    public void Advance(int count) => _written += count;

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return _buffer.AsMemory(_written);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return _buffer.AsSpan(_written);
    }

    public void Write(byte value)
    {
        Ensure(1);
        _buffer[_written++] = value;
    }

    public void Dispose()
    {
        var buffer = _buffer;
        _buffer = [];
        _written = 0;
        if (buffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void Ensure(int sizeHint)
    {
        ObjectDisposedException.ThrowIf(_buffer.Length == 0, this);
        var needed = Math.Max(sizeHint, 1);
        if (_buffer.Length - _written >= needed)
        {
            return;
        }

        var next = ArrayPool<byte>.Shared.Rent(Math.Max(_buffer.Length * 2, _written + needed));
        _buffer.AsSpan(0, _written).CopyTo(next);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = next;
    }
}
