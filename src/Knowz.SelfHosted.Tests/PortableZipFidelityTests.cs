using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Knowz.Core.Portability;
using Xunit;

namespace Knowz.Portability.Tests;

public class PortableZipFidelityTests
{
    [Fact]
    public void RoundTrip_PreservesArchivesCommentsAndEveryFileOwner()
    {
        var item = Guid.NewGuid();
        var comment = Guid.NewGuid();
        var outside = Guid.NewGuid();
        var package = new PortableExportPackage { SourceEdition = "platform" };
        package.Data.KnowledgeItems.Add(new() { Id = item, Title = "Entry", Content = "Body" });
        package.Data.Comments.Add(new() { Id = comment, KnowledgeId = item, Body = "Reply" });
        package.Data.Comments.Add(new() { Id = Guid.NewGuid(), KnowledgeId = outside, Body = "External owner" });
        package.Data.Archives["CustomerSite"] = new() { JsonSerializer.SerializeToElement(new { id = Guid.NewGuid(), name = "Archive" }) };
        foreach (var links in new List<PortableFileAttachmentLink>[] {
            new() { new() { CommentId = comment } },
            new() { new() { KnowledgeId = outside }, new() { KnowledgeId = item } },
            new() { new() { KnowledgeId = outside } }, new() })
            package.Data.FileRecords.Add(new() { Id = Guid.NewGuid(), FileName = "file.bin", Attachments = links,
                ExtensionData = new() { ["vaultId"] = JsonSerializer.SerializeToElement(Guid.NewGuid()) } });
        var restored = RoundTrip(package);
        Assert.Equal(package.Data.FileRecords.Select(f => f.Id).Order(), restored.Data.FileRecords.Select(f => f.Id).Order());
        Assert.Equal(package.Data.Comments.Select(c => c.Id).Order(), restored.Data.Comments.Select(c => c.Id).Order());
        Assert.True(JsonElement.DeepEquals(package.Data.Archives["CustomerSite"][0], restored.Data.Archives["CustomerSite"][0]));
        foreach (var file in package.Data.FileRecords)
            Assert.Equal(file.ExtensionData!["vaultId"].GetGuid(), restored.Data.FileRecords.Single(f => f.Id == file.Id).ExtensionData!["vaultId"].GetGuid());
        Assert.Equal(1, restored.Metadata.TotalArchiveTypes);
    }

    [Fact]
    public void RoundTrip_HighlyCompressibleTextProducedByWriter_IsReadable()
    {
        var package = new PortableExportPackage();
        package.Data.KnowledgeItems.Add(new() { Id = Guid.NewGuid(), Title = "Repetition", Content = new string('x', 100_000) });
        Assert.Equal(package.Data.KnowledgeItems[0].Content, RoundTrip(package).Data.KnowledgeItems[0].Content);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{")]
    [InlineData("{\"schemaVersion\":2,\"formatVersion\":99}")]
    [InlineData("{\"schemaVersion\":\"2\",\"formatVersion\":1}")]
    public void Read_InvalidCurrentManifest_Rejects(string? manifest)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
            if (manifest != null) Write(archive, "_manifest.json", manifest);
        stream.Position = 0;
        using var reader = new ZipArchive(stream, ZipArchiveMode.Read);
        Assert.Throws<PortableZipSecurityException>(() => PortableZipReader.ReadFromZip(reader));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"schemaVersion\":2,\"data\":null}")]
    public void Read_InvalidLegacyManifest_Rejects(string manifest)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true)) Write(archive, "manifest.json", manifest);
        stream.Position = 0;
        using var reader = new ZipArchive(stream, ZipArchiveMode.Read);
        Assert.Throws<PortableZipSecurityException>(() => PortableZipReader.ReadFromZip(reader));
    }

    [Fact]
    public void Read_DuplicateEntries_RejectsAmbiguousData()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            Write(archive, "_manifest.json", "{\"schemaVersion\":2,\"formatVersion\":1}");
            Write(archive, "_vaults.json", "[]"); Write(archive, "_vaults.json", "[]");
        }
        stream.Position = 0;
        using var reader = new ZipArchive(stream, ZipArchiveMode.Read);
        Assert.Throws<PortableZipSecurityException>(() => PortableZipReader.ReadFromZip(reader));
    }

    [Fact]
    public void Read_DuplicateEntityIds_RejectsAmbiguousData()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            Write(archive, "_manifest.json", "{\"schemaVersion\":2,\"formatVersion\":1}");
            var id = Guid.NewGuid();
            Write(archive, "_vaults.json", $"[{{\"id\":\"{id}\",\"name\":\"first\"}},{{\"id\":\"{id}\",\"name\":\"second\"}}]");
        }
        stream.Position = 0;
        using var reader = new ZipArchive(stream, ZipArchiveMode.Read);
        Assert.Throws<PortableZipSecurityException>(() => PortableZipReader.ReadFromZip(reader));
    }

    [Theory]
    [InlineData("tags")]
    [InlineData("vaults")]
    [InlineData("persons")]
    [InlineData("locations")]
    [InlineData("events")]
    [InlineData("vaultPersons")]
    [InlineData("personLinks")]
    [InlineData("locationLinks")]
    [InlineData("eventLinks")]
    public void ValidatePackage_RejectsMalformedNestedCollections(string field)
    {
        var package = new PortableExportPackage();
        var item = new PortableKnowledge { Id = Guid.NewGuid() };
        package.Data.KnowledgeItems.Add(item);
        switch (field)
        {
            case "tags": item.TagIds = null!; break;
            case "vaults": item.VaultIds = null!; break;
            case "persons": item.PersonIds = null!; break;
            case "locations": item.LocationIds = null!; break;
            case "events": item.EventIds = null!; break;
            case "vaultPersons": package.Data.Vaults.Add(new() { Id = Guid.NewGuid(), PersonIds = null! }); break;
            case "personLinks": item.PersonLinks = new() { null! }; break;
            case "locationLinks": item.LocationLinks = new() { null! }; break;
            case "eventLinks": item.EventLinks = new() { null! }; break;
        }
        Assert.Throws<PortableZipSecurityException>(() => PortableZipReader.ValidatePackage(package));
    }

    private static PortableExportPackage RoundTrip(PortableExportPackage package)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true)) PortableZipWriter.WriteToZip(archive, package);
        stream.Position = 0;
        using var reader = new ZipArchive(stream, ZipArchiveMode.Read);
        return PortableZipReader.ReadFromZip(reader);
    }

    private static void Write(ZipArchive archive, string name, string text)
    {
        using var stream = archive.CreateEntry(name).Open();
        stream.Write(Encoding.UTF8.GetBytes(text));
    }
}
