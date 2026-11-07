using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Knowz.Core.Entities;
using Knowz.Core.Interfaces;
using Knowz.Core.Portability;
using Knowz.SelfHosted.API.Endpoints;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

public sealed partial class SelfHostedGraphStoreTests
{
    [Fact]
    public Task NativeMultipartReplacementNeverBuffersOrdinaryBytesAsPlaintext() => RunMultipartOwnedProbeAsync(false);

    [Fact]
    public Task NativeMultipartReplacementRejectsPrivateFieldsBeforeActivation() => RunMultipartOwnedProbeAsync(true);

    private async Task RunMultipartOwnedProbeAsync(bool includePrivateField)
    {
        // ASP.NET caches its temporary directory per process. Run the actual HTTP probe in a
        // fresh test process with an owned directory; never mutate shared process temp settings.
        const string probeVariable = "KNOWZ_OWNED_MULTIPART_HTTP_PROBE";
        if (Environment.GetEnvironmentVariable(probeVariable) != "1")
        {
            var directory = Path.Combine(Path.GetTempPath(), "knowz-owned-multipart-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                start.ArgumentList.Add("vstest"); start.ArgumentList.Add(typeof(SelfHostedGraphStoreTests).Assembly.Location);
                start.ArgumentList.Add("--TestCaseFilter:FullyQualifiedName~" + (includePrivateField ? nameof(NativeMultipartReplacementRejectsPrivateFieldsBeforeActivation) : nameof(NativeMultipartReplacementNeverBuffersOrdinaryBytesAsPlaintext)));
                start.Environment[probeVariable] = "1"; start.Environment["ASPNETCORE_TEMP"] = directory;
                using var child = Process.Start(start)!;
                var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                try { await child.WaitForExitAsync(timeout.Token); }
                catch { if (!child.HasExited) child.Kill(entireProcessTree: true); throw; }
                Assert.True(child.ExitCode == 0, await output + await error);
                Assert.Empty(Directory.GetFiles(directory));
            }
            finally { Directory.Delete(directory, recursive: true); }
            return;
        }

        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        json.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        var source = JsonSerializer.Deserialize<PortableExportPackage>(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "portable-graph-v1.json")), json)!;
        var sealedFile = source.SealedFiles!.Single(); var original = Encoding.UTF8.GetBytes("Original public binary with protected metadata");
        source.Data.FileRecords.Add(new() { Id = sealedFile.Id, OriginalFileId = sealedFile.OriginalFileId,
            FileName = "encrypted-file", ContentType = "application/octet-stream", SizeBytes = original.Length,
            BinaryContentBase64 = Convert.ToBase64String(original), EncryptedFieldsJson = sealedFile.EncryptedFieldsJson,
            Attachments = [new() { KnowledgeId = source.SealedItems!.Single().Id }] });
        source.SealedFiles.Clear(); source.PayloadKeyReferences!.RemoveAll(r => r.OwnerKind == "file" && r.Role == "binary");
        var imported = await _graph.ImportAsync(source, null, "trusted:multipart-temp-probe", ImportConflictStrategy.Skip, null, default);
        Assert.True(imported.Success, imported.Error);
        var vault = (await _db.Vaults.SingleAsync()).Id; var file = await _db.FileRecords.SingleAsync();
        var projection = (await SelfHostedSealedFiles.GetProtectedFileAsync(_db, file.Id, default))!;
        var revision = SelfHostedSealedStore.PayloadRevision(projection);
        var exported = await _graph.ExportAsync(new() { Type = ExportScopeType.Vault, VaultIds = [vault] }, false, default);
        var refs = exported.PayloadKeyReferences!.Where(r => r.OwnerKind == "file" && r.OwnerId == file.Id).ToList();
        var catalog = (await _lifecycle.GetCatalogAsync(vault, default))!; var key = catalog.Keys.Single();
        Assert.True((await _lifecycle.SelectPrimaryAsync(vault, key.ContextId, key.Fingerprint, catalog.Revision, default, key.Generation)).Ok);
        var actor = new User { TenantId = _tenant, Username = "owned-multipart-probe", IsActive = true, Role = Knowz.Core.Enums.UserRole.Admin };
        _db.Users.Add(actor); await _db.SaveChangesAsync();
        var tenant = Substitute.For<ITenantProvider>(); tenant.TenantId.Returns(_tenant);
        var service = new SelfHostedSealedFiles(_db, tenant, _lifecycle, _storage);
        var marker = "ONLY-THIS-OWNED-REQUEST-" + Guid.NewGuid().ToString("N") + "|";
        var replacement = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(marker, 6000)));
        Assert.True(replacement.Length >= 256 * 1024);
        projection.SizeBytes = replacement.Length; projection.BinarySha256 = Convert.ToHexStringLower(SHA256.HashData(replacement));
        var observedPlaintext = new List<string>();
        var ownedTemp = Environment.GetEnvironmentVariable("ASPNETCORE_TEMP")!;
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(_db); builder.Services.AddSingleton(_lifecycle); builder.Services.AddSingleton(_graph); builder.Services.AddSingleton(service);
        builder.Services.AddSingleton<Knowz.SelfHosted.Application.Interfaces.IVaultAccessService>(new VaultAccessService(_db));
        await using var app = builder.Build();
        app.Use(async (http, next) =>
        {
            http.User = new(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, actor.Id.ToString()), new("tenantId", _tenant.ToString()), new(ClaimTypes.Role, "Admin")], "owned-fixture"));
            await next();
            // This is before response completion disposes framework form buffers.
            foreach (var path in Directory.GetFiles(ownedTemp))
                if (Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path)).Contains(marker, StringComparison.Ordinal)) observedPlaintext.Add(Path.GetFileName(path));
        });
        EncryptedVaultEndpoints.MapEncryptedVaultEndpoints(app); await app.StartAsync(); using var client = app.GetTestClient();
        using var form = new MultipartFormDataContent("owned-multipart-boundary");
        form.Add(new ByteArrayContent(replacement), "binary", "encrypted-file");
        form.Add(new StringContent(JsonSerializer.Serialize(projection, json)), "record");
        form.Add(new StringContent(JsonSerializer.Serialize(refs, json)), "payloadKeyReferences");
        form.Add(new StringContent(vault.ToString()), "vaultId");
        if (includePrivateField) form.Add(new StringContent("must-remain-client-local"), "privateKeyBase64");
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/encrypted/files/{file.Id}") { Content = form };
        request.Headers.TryAddWithoutValidation("If-Match", revision);
        using var response = await client.SendAsync(request);
        Assert.Empty(observedPlaintext);
        if (includePrivateField)
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Single(await _db.FileRecords.ToListAsync());
        }
        else
        {
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            var result = await response.Content.ReadFromJsonAsync<JsonElement>(); var newId = result.GetProperty("ownerId").GetGuid(); Assert.NotEqual(file.Id, newId);
            await using var stream = await _graph.OpenFileAsync(newId, default); using var actual = new MemoryStream(); await stream.CopyToAsync(actual); Assert.Equal(replacement, actual.ToArray());
        }
        await using var oldStream = await _graph.OpenFileAsync(file.Id, default); using var retained = new MemoryStream(); await oldStream.CopyToAsync(retained); Assert.Equal(original, retained.ToArray());
    }
}
