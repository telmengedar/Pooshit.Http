using System;
using System.IO;

namespace Http.Tests.TestSupport;

/// <summary>
/// readable stream which reports that it cannot seek and refuses every positioning request
/// </summary>
public class NonSeekableStream : Stream {
    readonly MemoryStream payload;

    /// <summary>
    /// creates a new <see cref="NonSeekableStream"/>
    /// </summary>
    /// <param name="payload">bytes handed out on read</param>
    public NonSeekableStream(byte[] payload) {
        this.payload = new(payload);
    }

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => false;

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
    public override int Read(byte[] buffer, int offset, int count) => payload.Read(buffer, offset, count);

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
