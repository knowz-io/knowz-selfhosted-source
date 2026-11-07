using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Knowz.Core.Portability;

/// <summary>
/// Runtime-only protected temporary storage: append once, authenticate completion, then read/seek.
/// Its fresh key stays in memory; a process restart invalidates the associated uncommitted plan.
/// </summary>
public sealed class ProtectedSpoolStream : Stream
{
    public const int ChunkBytes = 65536;
    public const int HeaderBytes = 36;
    public const int FullRecordBytes = ChunkBytes + 32;
    private readonly FileStream _file;
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private readonly byte[] _identity = RandomNumberGenerator.GetBytes(16);
    private readonly byte[] _noncePrefix = RandomNumberGenerator.GetBytes(4);
    private readonly byte[] _header = new byte[HeaderBytes];
    private readonly byte[] _plaintext = new byte[ChunkBytes];
    private long _length, _position, _count, _physicalLength;
    private long _cachedIndex = -1;
    private bool _sealed, _disposed;
    private const int FooterBytes = 48;

    private ProtectedSpoolStream(string? directory)
    {
        directory ??= Path.GetTempPath();
        if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
        BackingPath = Path.Combine(directory, $"knowz-protected-{Guid.NewGuid():N}.tmp");
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite,
            Share = FileShare.ReadWrite | FileShare.Delete, Options = FileOptions.DeleteOnClose,
            // Do not cache authenticated footer bytes across another handle modifying the file.
            BufferSize = 1,
        };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        _file = new FileStream(BackingPath, options);
        "KNZ-SPOOL-V1"u8.CopyTo(_header);
        _identity.CopyTo(_header, 16); _noncePrefix.CopyTo(_header, 32);
        _file.Write(_header);
    }

    public string BackingPath { get; }
    public static async Task<ProtectedSpoolStream> CreateAsync(Stream input, string? directory = null, CancellationToken ct = default, long? maxBytes = null)
    {
        var limit = maxBytes ?? KnowzArchiveLimits.MaxArchiveBytes;
        if (limit is < 0 or > KnowzArchiveLimits.MaxArchiveBytes) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        var result = new ProtectedSpoolStream(directory);
        var buffer = new byte[ChunkBytes];
        try
        {
            using var cipher = new AesGcm(result._key, 16);
            while (true)
            {
                var filled = 0;
                while (filled < ChunkBytes)
                {
                    var read = await input.ReadAsync(buffer.AsMemory(filled, ChunkBytes - filled), ct);
                    if (read == 0) break;
                    filled += read;
                }
                if (filled == 0) break;
                if (result._length + filled > limit) throw new KnowzArchiveException("Temporary archive exceeds its declared size limit.");
                var ciphertext = new byte[filled]; var nonce = result.Nonce(result._count); var tag = new byte[16];
                cipher.Encrypt(nonce, buffer.AsSpan(0, filled), ciphertext, tag, result.Aad(result._count, filled, final: false));
                var length = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(length, filled);
                await result._file.WriteAsync(length, ct); await result._file.WriteAsync(nonce, ct);
                await result._file.WriteAsync(ciphertext, ct); await result._file.WriteAsync(tag, ct);
                result._length += filled; result._count++;
            }
            var footer = new byte[16]; BinaryPrimitives.WriteInt64BigEndian(footer, result._length);
            BinaryPrimitives.WriteInt64BigEndian(footer.AsSpan(8), result._count);
            var footerCiphertext = new byte[16]; var footerTag = new byte[16]; var footerNonce = result.Nonce(result._count);
            cipher.Encrypt(footerNonce, footer, footerCiphertext, footerTag, result.Aad(result._count, 16, final: true));
            var footerLength = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(footerLength, 16);
            await result._file.WriteAsync(footerLength, ct); await result._file.WriteAsync(footerNonce, ct);
            await result._file.WriteAsync(footerCiphertext, ct); await result._file.WriteAsync(footerTag, ct);
            await result._file.FlushAsync(ct);
            result._physicalLength = result._file.Length; result._sealed = true;
            result.VerifyCompletion(); return result;
        }
        catch { result.Dispose(); throw; }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    private byte[] Nonce(long index)
    {
        var bytes = new byte[12]; _noncePrefix.CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(4), index); return bytes;
    }
    private byte[] Aad(long index, int length, bool final)
    {
        var bytes = new byte[29]; _identity.CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(16), index);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(24), length); bytes[28] = final ? (byte)2 : (byte)1;
        return bytes;
    }
    private void VerifyCompletion()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_sealed || _file.Length != _physicalLength) throw new CryptographicException("Protected spool is incomplete or altered.");
        try
        {
            var actualHeader = new byte[HeaderBytes]; _file.Position = 0; _file.ReadExactly(actualHeader);
            if (!CryptographicOperations.FixedTimeEquals(actualHeader, _header)) throw new CryptographicException("Protected spool header changed.");
            _file.Position = _physicalLength - FooterBytes;
            var footer = new byte[FooterBytes]; _file.ReadExactly(footer);
            if (BinaryPrimitives.ReadInt32BigEndian(footer) != 16 || !footer.AsSpan(4, 12).SequenceEqual(Nonce(_count)))
                throw new CryptographicException("Protected spool completion record is invalid.");
            Span<byte> plain = stackalloc byte[16]; using var cipher = new AesGcm(_key, 16);
            cipher.Decrypt(footer.AsSpan(4, 12), footer.AsSpan(16, 16), footer.AsSpan(32, 16), plain, Aad(_count, 16, true));
            if (BinaryPrimitives.ReadInt64BigEndian(plain) != _length || BinaryPrimitives.ReadInt64BigEndian(plain[8..]) != _count)
                throw new CryptographicException("Protected spool final count or length changed.");
            CryptographicOperations.ZeroMemory(plain);
        }
        catch (EndOfStreamException ex) { throw new CryptographicException("Protected spool was truncated.", ex); }
    }
    private void LoadChunk(long index)
    {
        if (_cachedIndex == index) return;
        var expected = (int)Math.Min(ChunkBytes, _length - index * ChunkBytes);
        if (expected <= 0) throw new CryptographicException("Invalid protected spool chunk index.");
        try
        {
            _file.Position = HeaderBytes + index * FullRecordBytes;
            var header = new byte[16]; _file.ReadExactly(header);
            if (BinaryPrimitives.ReadInt32BigEndian(header) != expected || !header.AsSpan(4).SequenceEqual(Nonce(index)))
                throw new CryptographicException("Protected spool chunk identity or length changed.");
            var encrypted = new byte[expected]; var tag = new byte[16]; _file.ReadExactly(encrypted); _file.ReadExactly(tag);
            CryptographicOperations.ZeroMemory(_plaintext); using var cipher = new AesGcm(_key, 16);
            cipher.Decrypt(header.AsSpan(4), encrypted, tag, _plaintext.AsSpan(0, expected), Aad(index, expected, false));
            _cachedIndex = index;
        }
        catch (EndOfStreamException ex) { throw new CryptographicException("Protected spool chunk was truncated.", ex); }
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        VerifyCompletion(); var copied = 0;
        while (copied < buffer.Length && _position < _length)
        {
            var index = _position / ChunkBytes; LoadChunk(index);
            var within = (int)(_position % ChunkBytes);
            var take = (int)Math.Min(buffer.Length - copied, Math.Min(ChunkBytes - within, _length - _position));
            _plaintext.AsSpan(within, take).CopyTo(buffer[copied..]); _position += take; copied += take;
        }
        return copied;
    }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); return ValueTask.FromResult(Read(buffer.Span)); }
    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var next = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => checked(_position + offset), SeekOrigin.End => checked(_length + offset), _ => throw new ArgumentOutOfRangeException(nameof(origin)) };
        if (next < 0 || next > _length) throw new IOException("Seek is outside the sealed spool.");
        _position = next; return next;
    }
    public override bool CanRead => !_disposed && _sealed;
    public override bool CanSeek => CanRead;
    public override bool CanWrite => false;
    public override long Length { get { ObjectDisposedException.ThrowIf(_disposed, this); return _length; } }
    public override long Position { get => _position; set => Seek(value, SeekOrigin.Begin); }
    public override void Flush() { ObjectDisposedException.ThrowIf(_disposed, this); }
    public override void SetLength(long value) => throw new NotSupportedException("Protected spool is append-only and sealed.");
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("Protected spool is append-only and sealed.");
    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true; CryptographicOperations.ZeroMemory(_key); CryptographicOperations.ZeroMemory(_plaintext);
            if (disposing) _file.Dispose();
        }
        base.Dispose(disposing);
    }
}
