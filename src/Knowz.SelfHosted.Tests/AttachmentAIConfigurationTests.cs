using Knowz.SelfHosted.Application.Services;

namespace Knowz.SelfHosted.Tests;

public class AttachmentAIConfigurationTests
{
    [Fact]
    public void CategorySchemas_ContainsAzureAIVisionCategory()
    {
        Assert.True(ConfigurationManagementService.CategorySchemas.ContainsKey("AzureAIVision"));
    }

    [Fact]
    public void AzureAIVisionCategory_HasExpectedKeys()
    {
        var schema = ConfigurationManagementService.CategorySchemas["AzureAIVision"];

        Assert.Equal("Azure AI Vision", schema.DisplayName);
        Assert.True(schema.Keys.ContainsKey("Endpoint"));
        Assert.True(schema.Keys.ContainsKey("ApiKey"));
        Assert.False(schema.Keys["Endpoint"].IsSecret);
        Assert.True(schema.Keys["ApiKey"].IsSecret);
    }

    [Fact]
    public void CategorySchemas_ContainsAzureDocumentIntelligenceCategory()
    {
        Assert.True(ConfigurationManagementService.CategorySchemas.ContainsKey("AzureDocumentIntelligence"));
    }

    [Fact]
    public void AzureDocumentIntelligenceCategory_HasExpectedKeys()
    {
        var schema = ConfigurationManagementService.CategorySchemas["AzureDocumentIntelligence"];

        Assert.Equal("Azure Document Intelligence", schema.DisplayName);
        Assert.True(schema.Keys.ContainsKey("Endpoint"));
        Assert.True(schema.Keys.ContainsKey("ApiKey"));
        Assert.False(schema.Keys["Endpoint"].IsSecret);
        Assert.True(schema.Keys["ApiKey"].IsSecret);
    }
}
