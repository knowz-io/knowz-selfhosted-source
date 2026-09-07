using Knowz.SelfHosted.Application.Services;

namespace Knowz.SelfHosted.Tests;

/// <summary>
/// Tests for SSO category registration in ConfigurationManagementService.
/// </summary>
public class SSOConfigurationTests
{
    [Fact]
    public void CategorySchemas_ContainsSSOCategory()
    {
        Assert.True(ConfigurationManagementService.CategorySchemas.ContainsKey("SSO"));
    }

    [Fact]
    public void SSOCategory_HasCorrectDisplayName()
    {
        var ssoSchema = ConfigurationManagementService.CategorySchemas["SSO"];
        Assert.Equal("Single Sign-On (SSO)", ssoSchema.DisplayName);
    }

    [Fact]
    public void SSOCategory_Has8Keys()
    {
        var ssoSchema = ConfigurationManagementService.CategorySchemas["SSO"];
        Assert.Equal(8, ssoSchema.Keys.Count);
    }

    [Theory]
    [InlineData("Enabled")]
    [InlineData("AutoProvisionUsers")]
    [InlineData("DefaultRole")]
    [InlineData("Microsoft:ClientId")]
    [InlineData("Microsoft:ClientSecret")]
    [InlineData("Microsoft:DirectoryTenantId")]
    [InlineData("Google:ClientId")]
    [InlineData("Google:ClientSecret")]
    public void SSOCategory_ContainsExpectedKey(string key)
    {
        var ssoSchema = ConfigurationManagementService.CategorySchemas["SSO"];
        Assert.True(ssoSchema.Keys.ContainsKey(key), $"Missing key: {key}");
    }

    [Theory]
    [InlineData("Microsoft:ClientSecret", true)]
    [InlineData("Google:ClientSecret", true)]
    [InlineData("Microsoft:ClientId", false)]
    [InlineData("Google:ClientId", false)]
    [InlineData("Enabled", false)]
    [InlineData("AutoProvisionUsers", false)]
    [InlineData("DefaultRole", false)]
    [InlineData("Microsoft:DirectoryTenantId", false)]
    public void SSOCategory_HasCorrectSecretFlags(string key, bool expectedIsSecret)
    {
        var ssoSchema = ConfigurationManagementService.CategorySchemas["SSO"];
        Assert.Equal(expectedIsSecret, ssoSchema.Keys[key].IsSecret);
    }

    [Fact]
    public void SSOCategory_AllKeysAreHotReloadable()
    {
        var ssoSchema = ConfigurationManagementService.CategorySchemas["SSO"];
        foreach (var kvp in ssoSchema.Keys)
        {
            Assert.False(kvp.Value.RequiresRestart,
                $"Key '{kvp.Key}' should not require restart");
        }
    }

    [Fact]
    public void SSOCategory_AllKeysHaveDescriptions()
    {
        var ssoSchema = ConfigurationManagementService.CategorySchemas["SSO"];
        foreach (var kvp in ssoSchema.Keys)
        {
            Assert.False(string.IsNullOrWhiteSpace(kvp.Value.Description),
                $"Key '{kvp.Key}' should have a description");
        }
    }
}
