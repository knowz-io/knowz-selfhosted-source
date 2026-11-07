using System.Net;
using System.Security.Claims;
using Knowz.Core.Interfaces;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Knowz.Core.Portability;
using Knowz.SelfHosted.API.Endpoints;
using Knowz.SelfHosted.Application.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

public sealed partial class SelfHostedGraphStoreTests
{
    [Theory]
    [InlineData("chunked")]
    [InlineData("declared")]
    [InlineData("denied")]
    [InlineData("foreign")]
    [InlineData("deleted")]
    public async Task ExistingFileAuthorityAndBinarySchemeBoundMultipartBeforeLargeCapture(string scenario)
    {
        var (source, _, _) = Fixture();
        var imported = await _graph.ImportAsync(source, null, "trusted:early-cap", Knowz.SelfHosted.Application.DTOs.ImportConflictStrategy.Skip, null, default);
        Assert.True(imported.Success, imported.Error);
        var vault = (await _db.Vaults.SingleAsync()).Id;
        var file = await _db.ShSealedFiles.SingleAsync();
        if (scenario == "foreign") file.TenantId = Guid.NewGuid();
        if (scenario == "deleted") file.IsDeleted = true;
        var actor = new Knowz.Core.Entities.User { TenantId = _tenant, Username = "early-cap-writer", IsActive = scenario != "denied", Role = Knowz.Core.Enums.UserRole.Admin };
        _db.Users.Add(actor); await _db.SaveChangesAsync();
        var tenant = Substitute.For<ITenantProvider>(); tenant.TenantId.Returns(_tenant);
        var storage = Substitute.For<IFileStorageProvider>();
        var service = new SelfHostedSealedFiles(_db, tenant, _lifecycle, storage);
        using var probe = new EarlyCapProbe(vault);
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(_db); builder.Services.AddSingleton(_lifecycle); builder.Services.AddSingleton(_graph); builder.Services.AddSingleton(service);
        builder.Services.AddSingleton<Knowz.SelfHosted.Application.Interfaces.IVaultAccessService>(new VaultAccessService(_db));
        await using var app = builder.Build();
        app.Use(async (http, next) => { http.User = new(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, actor.Id.ToString()), new("tenantId", _tenant.ToString()), new(ClaimTypes.Role, "Admin")], "owned-cap-fixture"));
            http.Request.Body = probe; http.Request.ContentType = "multipart/form-data; boundary=early-cap";
            http.Request.ContentLength = scenario == "declared" ? ProtectedFileMultipartReader.MaximumRequestBytes + 1 : null; await next(); });
        app.MapEncryptedVaultEndpoints(); await app.StartAsync(); using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/encrypted/files/{file.Id}"); request.Headers.TryAddWithoutValidation("If-Match", "1");
        using var response = await client.SendAsync(request);
        Assert.True(response.StatusCode == (scenario is "chunked" or "declared" ? HttpStatusCode.RequestEntityTooLarge : HttpStatusCode.Forbidden), $"Actual HTTP {(int)response.StatusCode}; request read {probe.BytesRead} bytes");
        Assert.InRange(probe.BytesRead, 0, scenario == "chunked" ? EncryptedGraphProfile.MaxFileBytes + 128 * 1024 : 0);
        Assert.Empty(storage.ReceivedCalls()); Assert.Empty(await _db.ShSealedFileStages.ToListAsync());
    }

    private sealed class EarlyCapProbe(Guid vault) : Stream
    {
        private readonly byte[] prefix = System.Text.Encoding.UTF8.GetBytes("--early-cap\r\nContent-Disposition: form-data; name=\"binary\"; filename=\"bytes\"\r\n\r\n");
        private readonly byte[] suffix = System.Text.Encoding.UTF8.GetBytes($"\r\n--early-cap\r\nContent-Disposition: form-data; name=\"record\"\r\n\r\n{{}}\r\n--early-cap\r\nContent-Disposition: form-data; name=\"payloadKeyReferences\"\r\n\r\n[]\r\n--early-cap\r\nContent-Disposition: form-data; name=\"vaultId\"\r\n\r\n{vault}\r\n--early-cap--\r\n");
        private const long BinaryLength = 65L * 1024 * 1024;
        public long BytesRead { get; private set; }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => prefix.Length + BinaryLength + suffix.Length;
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            var count = (int)Math.Min(buffer.Length, Length - BytesRead); var start = BytesRead; buffer[..count].Clear();
            for (var i = 0; i < count; i++)
            {
                var pos = start + i;
                if (pos < prefix.Length) buffer[i] = prefix[(int)pos];
                else if (pos >= prefix.Length + BinaryLength) buffer[i] = suffix[(int)(pos - prefix.Length - BinaryLength)];
            }
            BytesRead += count; return count;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); return ValueTask.FromResult(Read(buffer.Span)); }
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
