// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.CopilotStudio.Sync;

internal sealed class BoundedWriteStream : Stream
{
    private readonly Stream inner;
    private readonly long maxBytes;
    private readonly string description;
    private long written;

    public BoundedWriteStream(Stream inner, long maxBytes, string description)
    {
        this.inner = inner;
        this.maxBytes = maxBytes;
        this.description = description;
    }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => this.written;

    public override long Position
    {
        get => this.written;
        set => throw new NotSupportedException();
    }

    public override void Flush() => this.inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => this.inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
    {
        this.Reserve(count);
        this.inner.Write(buffer, offset, count);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        this.Reserve(count);
        return this.inner.WriteAsync(buffer, offset, count, cancellationToken);
    }

#if !NETSTANDARD2_0
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        this.Reserve(buffer.Length);
        return this.inner.WriteAsync(buffer, cancellationToken);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        this.Reserve(buffer.Length);
        this.inner.Write(buffer);
    }
#endif

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            this.inner.Dispose();
        }

        base.Dispose(disposing);
    }

    private void Reserve(int count)
    {
        if (this.written + count > this.maxBytes)
        {
            throw new KnowledgeFileTooLargeException(this.description, this.maxBytes);
        }

        this.written += count;
    }
}
