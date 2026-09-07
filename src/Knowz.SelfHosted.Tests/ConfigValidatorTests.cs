using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Setup.Services;

namespace Knowz.SelfHosted.Tests;

/// <summary>
/// Tests for the Setup CLI's <see cref="ConfigValidator"/>. Two invariants:
/// 1. <see cref="ConfigValidator.IsStrongAdminPassword"/> enforces the same policy
///    as <see cref="AuthService.IsWeakPassword"/> — so a password captured by the
///    CLI cannot be rejected by the runtime seeder at first boot.
/// 2. <see cref="ConfigValidator.GenerateJwtSecret"/> uses a CSPRNG, not
///    <see cref="System.Random"/> (was a weak-RNG bug pre-SEC_P0Triage).
/// </summary>
public class ConfigValidatorTests
{
    // --- IsStrongAdminPassword ↔ AuthService.IsWeakPassword parity ---

    [Theory]
    [InlineData("changeme")]
    [InlineData("admin")]
    [InlineData("Password123!")]      // contains "password"
    [InlineData("ChangeMe-2026!")]    // contains "changeme"
    [InlineData("KnowzSelfHost1!")]   // contains "knowz"
    public void IsStrongAdminPassword_Rejects_Denylisted(string pw)
    {
        Assert.False(ConfigValidator.IsStrongAdminPassword(pw));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("short1!A")]            // <12 chars
    [InlineData("nouppercase123!")]     // no uppercase
    [InlineData("NOLOWERCASE123!")]     // no lowercase
    [InlineData("NoSymbolOrDigit")]     // no digit, no symbol
    [InlineData("NoSymbol12345678")]    // no non-alnum
    public void IsStrongAdminPassword_Rejects_ComplexityFailures(string? pw)
    {
        Assert.False(ConfigValidator.IsStrongAdminPassword(pw!));
    }

    [Theory]
    [InlineData("R4pid!Vault-Seed")]
    [InlineData("Str0ng#Random-Value-2026")]
    [InlineData("Xy!8zQp9mTvR2wL7")]
    public void IsStrongAdminPassword_Accepts_Strong(string pw)
    {
        Assert.True(ConfigValidator.IsStrongAdminPassword(pw));
    }

    /// <summary>
    /// PARITY: for every password in a cross-product of denylist + strong + weak
    /// cases, the CLI validator and the AuthService runtime validator must AGREE.
    /// A password the CLI accepts MUST be accepted by AuthService; one the CLI
    /// rejects MUST also be rejected. If this test fails, the two lists have
    /// drifted — update both.
    /// </summary>
    [Theory]
    // Denylist (both reject)
    [InlineData("changeme", true)]
    [InlineData("admin", true)]
    [InlineData("Password123!", true)]
    [InlineData("letmein#2026AB", true)]
    // Complexity fail (both reject)
    [InlineData("short1!A", true)]
    [InlineData("nouppercase123!", true)]
    [InlineData("", true)]
    // Strong (both accept)
    [InlineData("R4pid!Vault-Seed", false)]
    [InlineData("Str0ng#Random-Value-2026", false)]
    public void CliValidator_AgreesWithRuntimeValidator(string password, bool expectedWeak)
    {
        var cliRejects = !ConfigValidator.IsStrongAdminPassword(password);
        var runtimeRejects = AuthService.IsWeakPassword(password);

        Assert.Equal(expectedWeak, cliRejects);
        Assert.Equal(cliRejects, runtimeRejects);
    }

    // --- GenerateJwtSecret: crypto RNG, not System.Random ---

    [Fact]
    public void GenerateJwtSecret_ProducesValueOfExpectedLength()
    {
        var secret = ConfigValidator.GenerateJwtSecret();

        // 48 bytes → base64 is ceil(48/3)*4 = 64 chars, minus trimmed padding
        Assert.True(secret.Length >= 32,
            $"Secret length {secret.Length} does not satisfy IsValidJwtSecret (>=32).");
        Assert.True(ConfigValidator.IsValidJwtSecret(secret));
    }

    [Fact]
    public void GenerateJwtSecret_ProducesUrlSafeBase64()
    {
        // Base64url alphabet: A-Z, a-z, 0-9, '-', '_'. No '+', '/', or '='.
        var secret = ConfigValidator.GenerateJwtSecret();

        Assert.DoesNotContain('+', secret);
        Assert.DoesNotContain('/', secret);
        Assert.DoesNotContain('=', secret);
        Assert.Matches("^[A-Za-z0-9_-]+$", secret);
    }

    [Fact]
    public void GenerateJwtSecret_ReturnsDifferentValuesOnSuccessiveCalls()
    {
        // Cryptographic RNG should produce unique values every call. Not a
        // proof of randomness quality, but catches the "accidentally used a
        // fixed seed" / "accidentally deterministic" regression cheaply.
        var set = new HashSet<string>();
        for (var i = 0; i < 10; i++)
        {
            set.Add(ConfigValidator.GenerateJwtSecret());
        }
        Assert.Equal(10, set.Count);
    }

    [Fact]
    public void GenerateJwtSecret_CustomByteCount_Honored()
    {
        // 24 bytes → base64 is ceil(24/3)*4 = 32 chars (no padding needed).
        var secret = ConfigValidator.GenerateJwtSecret(byteCount: 24);

        // Length should be close to 32 chars (possibly minus '=' trimmed).
        Assert.InRange(secret.Length, 30, 32);
    }
}
