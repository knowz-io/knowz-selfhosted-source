using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Knowz.Core.Enums;
using Knowz.SelfHosted.API.Helpers;
using Microsoft.AspNetCore.Http;

namespace Knowz.SelfHosted.Tests;

public class AuthorizationHelpersTests
{
    private static readonly Guid TestTenantId = Guid.Parse("aaaa0000-0000-0000-0000-000000000001");
    private static readonly Guid TestUserId = Guid.Parse("bbbb0000-0000-0000-0000-000000000001");

    private static HttpContext CreateHttpContext(string role, Guid? tenantId = null, Guid? userId = null)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Role, role)
        };
        if (tenantId.HasValue)
            claims.Add(new Claim("tenantId", tenantId.Value.ToString()));
        if (userId.HasValue)
            claims.Add(new Claim(JwtRegisteredClaimNames.Sub, userId.Value.ToString()));

        var identity = new ClaimsIdentity(claims, "TestAuth", ClaimTypes.Name, ClaimTypes.Role);
        var principal = new ClaimsPrincipal(identity);

        var context = new DefaultHttpContext();
        context.User = principal;
        return context;
    }

    // --- IsSuperAdmin ---

    [Fact]
    public void IsSuperAdmin_ReturnsTrue_ForSuperAdmin()
    {
        var ctx = CreateHttpContext("SuperAdmin");
        Assert.True(AuthorizationHelpers.IsSuperAdmin(ctx));
    }

    [Fact]
    public void IsSuperAdmin_ReturnsFalse_ForAdmin()
    {
        var ctx = CreateHttpContext("Admin");
        Assert.False(AuthorizationHelpers.IsSuperAdmin(ctx));
    }

    [Fact]
    public void IsSuperAdmin_ReturnsFalse_ForUser()
    {
        var ctx = CreateHttpContext("User");
        Assert.False(AuthorizationHelpers.IsSuperAdmin(ctx));
    }

    // --- IsAdminOrAbove ---

    [Fact]
    public void IsAdminOrAbove_ReturnsTrue_ForSuperAdmin()
    {
        var ctx = CreateHttpContext("SuperAdmin");
        Assert.True(AuthorizationHelpers.IsAdminOrAbove(ctx));
    }

    [Fact]
    public void IsAdminOrAbove_ReturnsTrue_ForAdmin()
    {
        var ctx = CreateHttpContext("Admin");
        Assert.True(AuthorizationHelpers.IsAdminOrAbove(ctx));
    }

    [Fact]
    public void IsAdminOrAbove_ReturnsFalse_ForUser()
    {
        var ctx = CreateHttpContext("User");
        Assert.False(AuthorizationHelpers.IsAdminOrAbove(ctx));
    }

    // --- IsAuthenticatedKnowzUser (VERIFY-M1) ---

    [Theory]
    [InlineData("User")]
    [InlineData("Admin")]
    [InlineData("SuperAdmin")]
    public void IsAuthenticatedKnowzUser_ReturnsTrue_ForKnowzRoles(string role)
    {
        var ctx = CreateHttpContext(role);
        Assert.True(AuthorizationHelpers.IsAuthenticatedKnowzUser(ctx));
    }

    [Fact]
    public void IsAuthenticatedKnowzUser_ReturnsFalse_ForLegacyApiKey()
    {
        var ctx = CreateHttpContext("LegacyApiKey");
        Assert.False(AuthorizationHelpers.IsAuthenticatedKnowzUser(ctx));
    }

    [Fact]
    public void IsAuthenticatedKnowzUser_ReturnsFalse_WhenAnonymous()
    {
        var context = new DefaultHttpContext();
        Assert.False(AuthorizationHelpers.IsAuthenticatedKnowzUser(context));
    }

    // --- GetCallerTenantId ---

    [Fact]
    public void GetCallerTenantId_ExtractsTenantId_FromJwtClaims()
    {
        var ctx = CreateHttpContext("Admin", tenantId: TestTenantId);
        var result = AuthorizationHelpers.GetCallerTenantId(ctx);
        Assert.Equal(TestTenantId, result);
    }

    [Fact]
    public void GetCallerTenantId_ReturnsNull_WhenNoTenantIdClaim()
    {
        var ctx = CreateHttpContext("SuperAdmin");
        var result = AuthorizationHelpers.GetCallerTenantId(ctx);
        Assert.Null(result);
    }

    [Fact]
    public void GetCallerTenantId_ReturnsNull_ForInvalidGuid()
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Role, "Admin"),
            new("tenantId", "not-a-guid")
        };
        var identity = new ClaimsIdentity(claims, "TestAuth", ClaimTypes.Name, ClaimTypes.Role);
        var context = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };

        var result = AuthorizationHelpers.GetCallerTenantId(context);
        Assert.Null(result);
    }

    // --- GetCallerId ---

    [Fact]
    public void GetCallerId_ExtractsUserId_FromJwtSubClaim()
    {
        var ctx = CreateHttpContext("Admin", userId: TestUserId);
        var result = AuthorizationHelpers.GetCallerId(ctx);
        Assert.Equal(TestUserId, result);
    }

    [Fact]
    public void GetCallerId_ReturnsNull_WhenNoSubClaim()
    {
        var ctx = CreateHttpContext("SuperAdmin");
        var result = AuthorizationHelpers.GetCallerId(ctx);
        Assert.Null(result);
    }

    // --- CanAssignRole ---

    [Fact]
    public void CanAssignRole_SuperAdmin_CanAssignSuperAdmin()
    {
        var ctx = CreateHttpContext("SuperAdmin");
        Assert.True(AuthorizationHelpers.CanAssignRole(ctx, UserRole.SuperAdmin));
    }

    [Fact]
    public void CanAssignRole_SuperAdmin_CanAssignAdmin()
    {
        var ctx = CreateHttpContext("SuperAdmin");
        Assert.True(AuthorizationHelpers.CanAssignRole(ctx, UserRole.Admin));
    }

    [Fact]
    public void CanAssignRole_SuperAdmin_CanAssignUser()
    {
        var ctx = CreateHttpContext("SuperAdmin");
        Assert.True(AuthorizationHelpers.CanAssignRole(ctx, UserRole.User));
    }

    [Fact]
    public void CanAssignRole_Admin_CanAssignUser()
    {
        var ctx = CreateHttpContext("Admin");
        Assert.True(AuthorizationHelpers.CanAssignRole(ctx, UserRole.User));
    }

    [Fact]
    public void CanAssignRole_Admin_CannotAssignAdmin()
    {
        var ctx = CreateHttpContext("Admin");
        Assert.False(AuthorizationHelpers.CanAssignRole(ctx, UserRole.Admin));
    }

    [Fact]
    public void CanAssignRole_Admin_CannotAssignSuperAdmin()
    {
        var ctx = CreateHttpContext("Admin");
        Assert.False(AuthorizationHelpers.CanAssignRole(ctx, UserRole.SuperAdmin));
    }

    [Fact]
    public void CanAssignRole_User_CannotAssignAnyRole()
    {
        var ctx = CreateHttpContext("User");
        Assert.False(AuthorizationHelpers.CanAssignRole(ctx, UserRole.User));
        Assert.False(AuthorizationHelpers.CanAssignRole(ctx, UserRole.Admin));
        Assert.False(AuthorizationHelpers.CanAssignRole(ctx, UserRole.SuperAdmin));
    }

    // --- IsPrivilegedRole ---

    [Fact]
    public void IsPrivilegedRole_ReturnsTrue_ForAdmin()
    {
        Assert.True(AuthorizationHelpers.IsPrivilegedRole(UserRole.Admin));
    }

    [Fact]
    public void IsPrivilegedRole_ReturnsTrue_ForSuperAdmin()
    {
        Assert.True(AuthorizationHelpers.IsPrivilegedRole(UserRole.SuperAdmin));
    }

    [Fact]
    public void IsPrivilegedRole_ReturnsFalse_ForUser()
    {
        Assert.False(AuthorizationHelpers.IsPrivilegedRole(UserRole.User));
    }

    // --- Forbidden ---

    [Fact]
    public void Forbidden_ReturnsJsonResult_WithDefaultMessage()
    {
        var result = AuthorizationHelpers.Forbidden();
        Assert.NotNull(result);
    }

    [Fact]
    public void Forbidden_ReturnsJsonResult_WithCustomMessage()
    {
        var result = AuthorizationHelpers.Forbidden("Cannot assign role 'SuperAdmin'. Insufficient privileges.");
        Assert.NotNull(result);
    }
}
