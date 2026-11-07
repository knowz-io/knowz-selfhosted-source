using System.Text;
using Knowz.SelfHosted.API.Endpoints;
using Knowz.Core.Portability;
using Microsoft.AspNetCore.Http;

namespace Knowz.SelfHosted.Tests;

public sealed class ProtectedFileMultipartReaderTests
{
    [Fact]
    public void PublicParserRemainsAnExactNamespaceOnlyVendorOfThePinnedMainParser()
    {
        var tests = Path.GetDirectoryName(SourcePath())!;
        var selfHostedSource = Path.GetFullPath(Path.Combine(tests, "../Knowz.SelfHosted.API/Endpoints/ProtectedFileMultipartReader.cs"));
        var canonical = File.ReadAllText(selfHostedSource).Replace("\r\n", "\n")
            .Replace("namespace Knowz.SelfHosted.API.Endpoints;", "namespace Knowz.API.Endpoints;", StringComparison.Ordinal);
        Assert.Equal("5767bf83d2d19a2557c3d4e8f3fa89d68451c68158dc7b293a1aed0d13e92a84", Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(canonical))));
        var main = Path.GetFullPath(Path.Combine(tests, "../../../src/Knowz.API/Endpoints/ProtectedFileMultipartReader.cs"));
        if (File.Exists(main)) Assert.Equal(File.ReadAllText(main).Replace("\r\n", "\n"), canonical);
        Assert.DoesNotContain(typeof(EncryptedGraphProfile).Assembly.GetReferencedAssemblies(), assembly => assembly.Name!.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
    }

    private static string SourcePath([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public async Task OrdinaryBytesAreOnlySpooledEncryptedInEverySupportedFieldOrder(int binaryIndex)
    {
        using var temp = new TempDirectory();
        var bytes = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("ORDINARY-BYTES-MUST-NOT-APPEAR-IN-TEMP|", 9000)));
        using var form = Form(bytes, binaryIndex);
        var request = await Request(form);
        await using (var parsed = await ProtectedFileMultipartReader.ReadAsync(request, spoolDirectory: temp.Path))
        {
            var paths = Directory.GetFiles(temp.Path);
            Assert.Equal(parsed.Binary.BackingPath, Assert.Single(paths));
            var onDisk = await File.ReadAllBytesAsync(paths.Single());
            Assert.True(onDisk.AsSpan(0, 12).SequenceEqual("KNZ-SPOOL-V1"u8));
            Assert.DoesNotContain("ORDINARY-BYTES-MUST-NOT-APPEAR-IN-TEMP", Encoding.UTF8.GetString(onDisk));
            using var copy = new MemoryStream(); await parsed.Binary.CopyToAsync(copy);
            Assert.Equal(bytes, copy.ToArray());
            parsed.Binary.Position = 0;
            Assert.Equal(bytes[0], parsed.Binary.ReadByte());
        }
        Assert.Empty(Directory.GetFiles(temp.Path));
    }

    [Theory]
    [InlineData("vaultId")]
    [InlineData("record")]
    [InlineData("payloadKeyReferences")]
    [InlineData("binary")]
    [InlineData("privateKeyBase64")]
    public async Task DuplicateOrUnknownFieldDisposesTheAlreadyCapturedBinary(string name)
    {
        using var temp = new TempDirectory();
        using var form = Form(new byte[128 * 1024], 0);
        form.Add(new StringContent("unaccepted"), name);
        var request = await Request(form);
        var action = async () => await ProtectedFileMultipartReader.ReadAsync(request, spoolDirectory: temp.Path);
        await Assert.ThrowsAsync<InvalidDataException>(async () => { await action(); });
        Assert.Empty(Directory.GetFiles(temp.Path));
    }

    [Fact]
    public async Task AggregateMetadataLimitDisposesBinaryEvenWhenEachFieldIsWithinIndividualLimit()
    {
        using var temp = new TempDirectory();
        using var form = Form(new byte[128 * 1024], 0, new string('a', 4 * 1024 * 1024), new string('b', 4 * 1024 * 1024));
        var request = await Request(form);
        var action = async () => await ProtectedFileMultipartReader.ReadAsync(request, spoolDirectory: temp.Path);
        Assert.Equal(413, (await Assert.ThrowsAsync<ArchiveOperationException>(async () => { await action(); })).StatusCode);
        Assert.Empty(Directory.GetFiles(temp.Path));
    }

    [Fact]
    public async Task ChunkedBinaryLimitIsEnforcedWithoutAContentLengthDeclaration()
    {
        using var temp = new TempDirectory();
        using var form = Form(new byte[checked((int)ProtectedFileMultipartReader.MaximumBinaryBytes + 1)], 0);
        var request = await Request(form);
        Assert.Null(request.ContentLength);
        var action = async () => await ProtectedFileMultipartReader.ReadAsync(request, spoolDirectory: temp.Path);
        Assert.Equal(413, (await Assert.ThrowsAsync<ArchiveOperationException>(async () => { await action(); })).StatusCode);
        Assert.Empty(Directory.GetFiles(temp.Path));
    }

    [Fact]
    public async Task DeclaredOversizeRequestFailsBeforeReadingOrCreatingTemporaryFiles()
    {
        using var temp = new TempDirectory();
        var request = new DefaultHttpContext().Request;
        request.ContentType = "multipart/form-data; boundary=bounded";
        request.ContentLength = ProtectedFileMultipartReader.MaximumRequestBytes + 1;
        request.Body = new ReadForbiddenStream();
        var action = async () => await ProtectedFileMultipartReader.ReadAsync(request, spoolDirectory: temp.Path);
        Assert.Equal(413, (await Assert.ThrowsAsync<ArchiveOperationException>(async () => { await action(); })).StatusCode);
        Assert.Empty(Directory.GetFiles(temp.Path));
    }

    [Fact]
    public async Task CancellationDuringBinaryCaptureRemovesItsProtectedTemporaryFile()
    {
        using var temp = new TempDirectory(); using var cancelled = new CancellationTokenSource();
        using var form = Form(new byte[256 * 1024], 0);
        var request = await Request(form);
        request.Body = new CancellingStream(request.Body, cancelled, 80 * 1024);
        var action = async () => await ProtectedFileMultipartReader.ReadAsync(request, cancelled.Token, temp.Path);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await action(); });
        Assert.Empty(Directory.GetFiles(temp.Path));
    }

    [Fact]
    public async Task MissingOrMalformedBoundaryCannotProduceACompletedUpload()
    {
        using var temp = new TempDirectory(); using var form = Form(new byte[128 * 1024], 0);
        var request = await Request(form);
        request.ContentType = "multipart/form-data; boundary=wrong";
        var action = async () => await ProtectedFileMultipartReader.ReadAsync(request, spoolDirectory: temp.Path);
        await Assert.ThrowsAsync<InvalidDataException>(async () => { await action(); });
        Assert.Empty(Directory.GetFiles(temp.Path));
    }

    [Fact]
    public async Task TruncatedBinaryIsAMalformedRequestAndRemovesItsProtectedTemporaryFile()
    {
        using var temp = new TempDirectory(); using var form = Form(new byte[128 * 1024], 0);
        var request = await Request(form);
        var bytes = ((MemoryStream)request.Body).ToArray();
        request.Body = new MemoryStream(bytes, 0, 100 * 1024, writable: false);
        var action = async () => await ProtectedFileMultipartReader.ReadAsync(request, spoolDirectory: temp.Path);
        await Assert.ThrowsAsync<InvalidDataException>(async () => { await action(); });
        Assert.Empty(Directory.GetFiles(temp.Path));
    }

    private static MultipartFormDataContent Form(byte[] binary, int binaryIndex, string record = "{}", string refs = "[]")
    {
        var form = new MultipartFormDataContent("bounded-portable-file");
        var values = new List<(HttpContent Content, string Name)>
        {
            (new StringContent(Guid.NewGuid().ToString("D")), "vaultId"),
            (new StringContent(record), "record"), (new StringContent(refs), "payloadKeyReferences"),
        };
        values.Insert(binaryIndex, (new ByteArrayContent(binary), "binary"));
        foreach (var value in values)
        {
            if (value.Name == "binary") form.Add(value.Content, value.Name, "ordinary.bin");
            else form.Add(value.Content, value.Name);
        }
        return form;
    }
    private static async Task<HttpRequest> Request(MultipartFormDataContent form)
    {
        var request = new DefaultHttpContext().Request;
        request.ContentType = form.Headers.ContentType!.ToString();
        request.Body = new MemoryStream(await form.ReadAsByteArrayAsync(), writable: false);
        return request;
    }
    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "knowz-stream-test-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
    private sealed class ReadForbiddenStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => throw new InvalidOperationException("Input must not be read.");
    }
    private sealed class CancellingStream(Stream inner, CancellationTokenSource cancelled, long threshold) : Stream
    {
        private long _read;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); var count = await inner.ReadAsync(buffer, ct);
            _read += count; if (_read > threshold) cancelled.Cancel(); return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
