using System;
using System.IO;

namespace Http.Tests.TestSupport;

/// <summary>
/// write only sink which retains nothing and records how the bytes arrived
/// </summary>
public class WriteRecordingSink : Stream {

    /// <summary>
    /// number of bytes the sink received
    /// </summary>
    public long BytesWritten { get; private set; }

    /// <summary>
    /// size of the largest single write the sink received
    /// </summary>
    public int LargestWrite { get; private set; }

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
        BytesWritten += size;
        if (size > LargestWrite)
            LargestWrite = size;
    }
}
