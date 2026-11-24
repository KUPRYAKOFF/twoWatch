namespace CinemaTogether.Server;

internal sealed class CountingStream(Stream inner, Action<int> count) : Stream
{
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int length) { inner.Write(buffer, offset, length); count(length); }
    public override async Task WriteAsync(byte[] buffer, int offset, int length, CancellationToken cancellationToken)
    { await inner.WriteAsync(buffer.AsMemory(offset, length), cancellationToken); count(length); }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    { await inner.WriteAsync(buffer, cancellationToken); count(buffer.Length); }
}
