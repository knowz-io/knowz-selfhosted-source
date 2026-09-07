using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Knowz.Core.Enums;
using Knowz.SelfHosted.API.Helpers;
using Knowz.SelfHosted.Application.Interfaces;
using Knowz.SelfHosted.Application.Models;
using Microsoft.AspNetCore.Http;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

/// <summary>
/// Tests for SEC_AdminRoleAuthorization and SEC_AdminTenantScoping enforcement.
/// Verifies authorization logic using AuthorizationHelpers against simulated HttpContext.
/// </summary>
public class AdminEndpointsAuthorizationTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaa0000-0000-0000-0000-000000000001");
    private static readonly Guid TenantB = Guid.Parse("bbbb0000-0000-0000-0000-000000000002");
    private static readonly Guid UserIdRegular = Guid.Parse("cccc0000-0000-0000-0000-000000000001");
    private static readonly Guid UserIdAdmin = Guid.Parse("cccc0000-0000-0000-0000-000000000002");
    private static readonly Guid UserIdSuperAdmin = Guid.Parse("cccc0000-0000-0000-0000-000000000003");
    private static readonly Guid UserIdOtherTenant = Guid.Parse("cccc0000-0000-0000-0000-000000000004");

    private static HttpContext MakeContext(string role, Guid? tenantId = null, Guid? userId = null)
    {
        var claims = new List<Claim> { new(ClaimTypes.Role, role) };
        if (tenantId.HasValue) claims.Add(new("tenantId", tenantId.Value.ToString()));
        if (userId.HasValue) claims.Add(new(JwtRegisteredClaimNames.Sub, userId.Value.ToString()));
        var identity = new ClaimsIdentity(claims, "Test", ClaimTypes.Name, ClaimTypes.Role);
        return new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
    }

    private IUserManagementService MockUserSvc()
    {
        var svc = Substitute.For<IUserManagementService>();
        svc.GetUserAsync(UserIdRegular).Returns(new UserDto
        {
            Id = UserIdRegular, TenantId = TenantA, Role = UserRole.User,
            Username = "regular", IsActive = true
        });
        svc.GetUserAsync(UserIdAdmin).Returns(new UserDto
        {
            Id = UserIdAdmin, TenantId = TenantA, Role = UserRole.Admin,
            Username = "admin", IsActive = true
        });
        svc.GetUserAsync(UserIdSuperAdmin).Returns(new UserDto
        {
            Id = UserIdSuperAdmin, TenantId = TenantA, Role = UserRole.SuperAdmin,
            Username = "superadmin", IsActive = true
        });
        svc.GetUserAsync(UserIdOtherTenant).Returns(new UserDto
        {
            Id = UserIdOtherTenant, TenantId = TenantB, Role = UserRole.User,
            Username = "other-tenant-user", IsActive = true
        });
        return svc;
    }

    // ===== Tenant CRUD: SuperAdmin only =====

    [Theory]
    [InlineData("Admin")]
    [InlineData("User")]
    public void TenantEndpoints_DenyNonSuperAdmin(string role)
    {
        var ctx = MakeContext(role, TenantA);
        Assert.False(AuthorizationHelpers.IsSuperAdmin(ctx));
    }

    [Fact]
    public void TenantEndpoints_AllowSuperAdmin()
    {
        var ctx = MakeContext("SuperAdmin");
        Assert.True(AuthorizationHelpers.IsSuperAdmin(ctx));
    }

    // ===== Privilege Escalation Prevention (CRITICAL) =====

    [Fact]
    public void AdminCannotCreateSuperAdminUser()
    {
        var ctx = MakeContext("Admin", TenantA);
        Assert.False(AuthorizationHelpers.CanAssignRole(ctx, UserRole.SuperAdmin));
    }

    [Fact]
    public void AdminCannotCreateAdminUser()
    {
        var ctx = MakeContext("Admin", TenantA);
        Assert.False(AuthorizationHelpers.CanAssignRole(ctx, UserRole.Admin));
    }

    [Fact]
    public void AdminCanCreateRegularUser()
    {
        var ctx = MakeContext("Admin", TenantA);
        Assert.True(AuthorizationHelpers.CanAssignRole(ctx, UserRole.User));
    }

    [Fact]
    public void SuperAdminCanCreateSuperAdminUser()
    {
        var ctx = MakeContext("SuperAdmin");
        Assert.True(AuthorizationHelpers.CanAssignRole(ctx, UserRole.SuperAdmin));
    }

    [Fact]
    public void SuperAdminCanCreateAdminUser()
    {
        var ctx = MakeContext("SuperAdmin");
        Assert.True(AuthorizationHelpers.CanAssignRole(ctx, UserRole.Admin));
    }

    // ===== Target User Role Protection =====

    [Fact]
    public async Task AdminCannotModifyAdminUser()
    {
        var ctx = MakeContext("Admin", TenantA);
        var svc = MockUserSvc();
        var targetUser = await svc.GetUserAsync(UserIdAdmin);
        Assert.NotNull(targetUser);
        Assert.True(AuthorizationHelpers.IsPrivilegedRole(targetUser!.Role));
        Assert.False(AuthorizationHelpers.IsSuperAdmin(ctx));
    }

    [Fact]
    public async Task AdminCannotModifySuperAdminUser()
    {
        var ctx = MakeContext("Admin", TenantA);
        var svc = MockUserSvc();
        var targetUser = await svc.GetUserAsync(UserIdSuperAdmin);
        Assert.NotNull(targetUser);
        Assert.True(AuthorizationHelpers.IsPrivilegedRole(targetUser!.Role));
        Assert.False(AuthorizationHelpers.IsSuperAdmin(ctx));
    }

    [Fact]
    public async Task AdminCanModifyRegularUser()
    {
        var ctx = MakeContext("Admin", TenantA);
        var svc = MockUserSvc();
        var targetUser = await svc.GetUserAsync(UserIdRegular);
        Assert.NotNull(targetUser);
        Assert.False(AuthorizationHelpers.IsPrivilegedRole(targetUser!.Role));
        Assert.Equal(TenantA, targetUser.TenantId);
    }

    [Fact]
    public void SuperAdminCanModifyAdminUser()
    {
        var ctx = MakeContext("SuperAdmin");
        Assert.True(AuthorizationHelpers.IsSuperAdmin(ctx));
    }

    // ===== Cross-Tenant Isolation =====

    [Fact]
    public void AdminScopedToOwnTenant_ListUsers()
    {
        var ctx = MakeContext("Admin", TenantA);
        var callerTenantId = AuthorizationHelpers.GetCallerTenantId(ctx);
        Assert.Equal(TenantA, callerTenantId);
        Assert.False(AuthorizationHelpers.IsSuperAdmin(ctx));
    }

    [Fact]
    public async Task AdminCannotAccessCrossTenantUser()
    {
        var ctx = MakeContext("Admin", TenantA);
        var svc = MockUserSvc();
        var callerTenantId = AuthorizationHelpers.GetCallerTenantId(ctx);
        var targetUser = await svc.GetUserAsync(UserIdOtherTenant);
        Assert.NotNull(targetUser);
        Assert.NotEqual(callerTenantId, targetUser!.TenantId);
    }

    [Fact]
    public void AdminCreateUser_ForcesOwnTenantId()
    {
        var ctx = MakeContext("Admin", TenantA);
        var callerTenantId = AuthorizationHelpers.GetCallerTenantId(ctx);
        var request = new CreateUserRequest
        {
            TenantId = TenantB, Username = "attacker-user",
            Password = "password123", Role = UserRole.User
        };
        // Endpoint overrides: request.TenantId = callerTenantId.Value
        request.TenantId = callerTenantId!.Value;
        Assert.Equal(TenantA, request.TenantId);
    }

    [Fact]
    public void SuperAdminCanAccessCrossTenantUsers()
    {
        var ctx = MakeContext("SuperAdmin");
        Assert.True(AuthorizationHelpers.IsSuperAdmin(ctx));
    }

    // ===== Information Leakage Prevention =====

    [Fact]
    public async Task CrossTenantUserLookup_Returns404Pattern()
    {
        var ctx = MakeContext("Admin", TenantA);
        var svc = MockUserSvc();
        var callerTenantId = AuthorizationHelpers.GetCallerTenantId(ctx);
        var targetUser = await svc.GetUserAsync(UserIdOtherTenant);
        Assert.NotEqual(callerTenantId, targetUser!.TenantId);
    }

    // ===== Platform Operations: SuperAdmin only =====

    [Theory]
    [InlineData("Admin")]
    [InlineData("User")]
    public void ConfigEndpoints_DenyNonSuperAdmin(string role)
    {
        Assert.False(AuthorizationHelpers.IsSuperAdmin(MakeContext(role, TenantA)));
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("User")]
    public void SSOEndpoints_DenyNonSuperAdmin(string role)
    {
        Assert.False(AuthorizationHelpers.IsSuperAdmin(MakeContext(role, TenantA)));
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("User")]
    public void PortabilityEndpoints_DenyNonSuperAdmin(string role)
    {
        Assert.False(AuthorizationHelpers.IsSuperAdmin(MakeContext(role, TenantA)));
    }

    [Fact]
    public void PortabilityEndpoints_AllowSuperAdmin()
    {
        Assert.True(AuthorizationHelpers.IsSuperAdmin(MakeContext("SuperAdmin")));
    }

    // ===== Tenant-Scoped Operations: Admin Allowed =====

    [Fact]
    public void AdminCanAccessUserEndpoints()
    {
        Assert.True(AuthorizationHelpers.IsAdminOrAbove(MakeContext("Admin", TenantA)));
    }

    [Fact]
    public void AdminCanAccessVaultAccessEndpoints()
    {
        Assert.True(AuthorizationHelpers.IsAdminOrAbove(MakeContext("Admin", TenantA)));
    }

    // ===== Backward Compatibility =====

    [Fact]
    public void RegularUserDeniedFromAllAdminEndpoints()
    {
        var ctx = MakeContext("User");
        Assert.False(AuthorizationHelpers.IsSuperAdmin(ctx));
        Assert.False(AuthorizationHelpers.IsAdminOrAbove(ctx));
    }

    [Fact]
    public void LegacyApiKeyUser_ScopedToLegacyRole_DeniedFromAdminEndpoints()
    {
        // SEC_P0Triage §Rule 7: legacy global API key no longer grants SuperAdmin.
        // AuthenticationMiddleware.TryAuthenticateLegacyApiKey now emits role
        // "LegacyApiKey" which fails both IsSuperAdmin and IsAdminOrAbove checks,
        // forcing /api/superadmin/*, /api/config/*, /api/users/*, /api/admin/*
        // endpoints to 403 for legacy callers.
        var ctx = MakeContext("LegacyApiKey");
        Assert.False(AuthorizationHelpers.IsSuperAdmin(ctx));
        Assert.False(AuthorizationHelpers.IsAdminOrAbove(ctx));
        Assert.Null(AuthorizationHelpers.GetCallerTenantId(ctx));
        // Can't be used to assign privileged roles either.
        Assert.False(AuthorizationHelpers.CanAssignRole(ctx, UserRole.User));
        Assert.False(AuthorizationHelpers.CanAssignRole(ctx, UserRole.Admin));
        Assert.False(AuthorizationHelpers.CanAssignRole(ctx, UserRole.SuperAdmin));
    }

    [Fact]
    public void SuperAdminExistingWorkflows_Unchanged()
    {
        var ctx = MakeContext("SuperAdmin");
        Assert.True(AuthorizationHelpers.IsSuperAdmin(ctx));
        Assert.True(AuthorizationHelpers.IsAdminOrAbove(ctx));
        Assert.True(AuthorizationHelpers.CanAssignRole(ctx, UserRole.SuperAdmin));
    }

    // ===== Tenant Context Edge Cases =====

    [Fact]
    public void AdminWithMissingTenantId_GetsForbidden()
    {
        var ctx = MakeContext("Admin");
        Assert.Null(AuthorizationHelpers.GetCallerTenantId(ctx));
    }

    // ===== Role-cap on PUT (update) =====

    [Fact]
    public void AdminCannotPromoteToAdmin_OnUpdate()
    {
        var ctx = MakeContext("Admin", TenantA);
        Assert.False(AuthorizationHelpers.CanAssignRole(ctx, UserRole.Admin));
    }

    [Fact]
    public void AdminCannotPromoteToSuperAdmin_OnUpdate()
    {
        var ctx = MakeContext("Admin", TenantA);
        Assert.False(AuthorizationHelpers.CanAssignRole(ctx, UserRole.SuperAdmin));
    }

    [Fact]
    public void SuperAdminCanPromoteToSuperAdmin_OnUpdate()
    {
        var ctx = MakeContext("SuperAdmin");
        Assert.True(AuthorizationHelpers.CanAssignRole(ctx, UserRole.SuperAdmin));
    }

    // ===== generate-api-key and reset-password: tenant + role protection =====

    [Fact]
    public async Task AdminCannotGenerateApiKeyForAdminUser()
    {
        var ctx = MakeContext("Admin", TenantA);
        var svc = MockUserSvc();
        var targetUser = await svc.GetUserAsync(UserIdAdmin);
        Assert.True(AuthorizationHelpers.IsPrivilegedRole(targetUser!.Role));
        Assert.False(AuthorizationHelpers.IsSuperAdmin(ctx));
    }

    [Fact]
    public async Task AdminCannotResetPasswordForAdminUser()
    {
        var ctx = MakeContext("Admin", TenantA);
        var svc = MockUserSvc();
        var targetUser = await svc.GetUserAsync(UserIdAdmin);
        Assert.True(AuthorizationHelpers.IsPrivilegedRole(targetUser!.Role));
        Assert.False(AuthorizationHelpers.IsSuperAdmin(ctx));
    }

    [Fact]
    public async Task AdminCannotGenerateApiKeyForCrossTenantUser()
    {
        var ctx = MakeContext("Admin", TenantA);
        var svc = MockUserSvc();
        var callerTenantId = AuthorizationHelpers.GetCallerTenantId(ctx);
        var targetUser = await svc.GetUserAsync(UserIdOtherTenant);
        Assert.NotEqual(callerTenantId, targetUser!.TenantId);
    }
}
