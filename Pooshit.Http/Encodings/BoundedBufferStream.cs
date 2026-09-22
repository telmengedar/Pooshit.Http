using System;
using System.IO;

namespace Pooshit.Http.Encodings;

/// <summary>
/// write only stream which retains what is written to it until a byte limit is exceeded
/// </summary>
class BoundedBufferStream : Stream {
    readonly int limit;
    readonly MemoryStream retained = new();

    /// <summary>
    /// creates a new <see cref="BoundedBufferStream"/>
    /// </summary>
    /// <param name="limit">number of bytes which are retained before the stream overflows</param>
    public BoundedBufferStream(int limit) => this.limit = limit;

    /// <summary>
    /// whether more bytes were written than the limit allows
    /// </summary>
    public bool Overflowed { get; private set; }

    /// <summary>
    /// bytes retained so far
    /// </summary>
    public byte[] Buffer => retained.GetBuffer();

    /// <summary>
    /// number of valid bytes in <see cref="Buffer"/>
    /// </summary>
    public int Count => (int)retained.Length;

    /// <inheritdoc />
    public override bool CanRead => false;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => true;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override void Flush() { }

    /// <inheritdoc />
    public override int Read(byte[] data, int offset, int size) => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] data, int offset, int size) {
        if (Overflowed)
            return;

        if (retained.Length + size > limit) {
            Overflowed = true;
            retained.SetLength(0);
            retained.Capacity = 0;
            return;
        }

        retained.Write(data, offset, size);
    }
}
