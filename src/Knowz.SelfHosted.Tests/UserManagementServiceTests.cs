using Knowz.Core.Configuration;
using Knowz.Core.Entities;
using Knowz.Core.Enums;
using Knowz.SelfHosted.Application.Interfaces;
using Knowz.SelfHosted.Application.Models;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

public class UserManagementServiceTests : IDisposable
{
    private readonly SelfHostedDbContext _db;
    private readonly IUserManagementService _service;
    private readonly IAuthService _authService;
    private readonly Tenant _tenant;
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public UserManagementServiceTests()
    {
        var dbOptions = new DbContextOptionsBuilder<SelfHostedDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var options = new SelfHostedOptions
        {
            TenantId = TenantId,
            JwtSecret = "this-is-a-test-secret-key-at-least-32-characters",
            JwtExpirationMinutes = 60,
            JwtIssuer = "test-issuer"
        };
        var selfHostedOptions = Options.Create(options);
        var tenantProvider = Substitute.For<Knowz.Core.Interfaces.ITenantProvider>();
        tenantProvider.TenantId.Returns(TenantId);
        _db = new SelfHostedDbContext(dbOptions, tenantProvider);

        _authService = new AuthService(_db, selfHostedOptions, Substitute.For<ILogger<AuthService>>());
        var logger = Substitute.For<ILogger<UserManagementService>>();
        _service = new UserManagementService(_db, _authService, logger);

        // Seed a tenant
        _tenant = new Tenant { Name = "Test Tenant", Slug = "test-tenant" };
        _db.Tenants.Add(_tenant);
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
    }

    // --- ListUsersAsync ---

    [Fact]
    public async Task Should_ReturnEmptyList_WhenNoUsers()
    {
        var result = await _service.ListUsersAsync();

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public async Task Should_ReturnAllUsers_WhenNoTenantFilter()
    {
        _db.Users.Add(new User { TenantId = _tenant.Id, Username = "user1", PasswordHash = "hash" });
        _db.Users.Add(new User { TenantId = _tenant.Id, Username = "user2", PasswordHash = "hash" });
        await _db.SaveChangesAsync();

        var result = await _service.ListUsersAsync();

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task Should_FilterByTenantId_WhenProvided()
    {
        var otherTenant = new Tenant { Name = "Other", Slug = "other" };
        _db.Tenants.Add(otherTenant);
        await _db.SaveChangesAsync();

        _db.Users.Add(new User { TenantId = _tenant.Id, Username = "user1", PasswordHash = "hash" });
        _db.Users.Add(new User { TenantId = otherTenant.Id, Username = "user2", PasswordHash = "hash" });
        await _db.SaveChangesAsync();

        var result = await _service.ListUsersAsync(_tenant.Id);

        Assert.Single(result);
        Assert.Equal("user1", result[0].Username);
    }

    // --- GetUserAsync ---

    [Fact]
    public async Task Should_ReturnUser_WhenExists()
    {
        var user = new User { TenantId = _tenant.Id, Username = "testuser", PasswordHash = "hash" };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        var result = await _service.GetUserAsync(user.Id);

        Assert.NotNull(result);
        Assert.Equal("testuser", result!.Username);
    }

    [Fact]
    public async Task Should_ReturnNull_WhenUserNotFound()
    {
        var result = await _service.GetUserAsync(Guid.NewGuid());

        Assert.Null(result);
    }

    [Fact]
    public async Task Should_IncludeTenantName_InUserDto()
    {
        var user = new User { TenantId = _tenant.Id, Username = "testuser", PasswordHash = "hash" };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        var result = await _service.GetUserAsync(user.Id);

        Assert.Equal("Test Tenant", result!.TenantName);
    }

    // --- CreateUserAsync ---

    [Fact]
    public async Task Should_CreateUser_WithValidRequest()
    {
        var request = new CreateUserRequest
        {
            TenantId = _tenant.Id,
            Username = "newuser",
            Password = "password123",
            Email = "new@test.com",
            DisplayName = "New User"
        };

        var result = await _service.CreateUserAsync(request);

        Assert.NotNull(result);
        Assert.Equal("newuser", result.Username);
        Assert.Equal("new@test.com", result.Email);
        Assert.Equal("New User", result.DisplayName);
        Assert.Equal(UserRole.User, result.Role);
        Assert.True(result.IsActive);
    }

    [Fact]
    public async Task Should_HashPassword_WhenCreatingUser()
    {
        var request = new CreateUserRequest
        {
            TenantId = _tenant.Id,
            Username = "newuser",
            Password = "password123"
        };

        await _service.CreateUserAsync(request);

        var user = await _db.Users.FirstAsync(u => u.Username == "newuser");
        Assert.NotEqual("password123", user.PasswordHash);
        Assert.True(_authService.VerifyPassword("password123", user.PasswordHash));
    }

    [Fact]
    public async Task Should_ThrowException_WhenUsernameDuplicate()
    {
        _db.Users.Add(new User { TenantId = _tenant.Id, Username = "existing", PasswordHash = "hash" });
        await _db.SaveChangesAsync();

        var request = new CreateUserRequest
        {
            TenantId = _tenant.Id,
            Username = "existing",
            Password = "password123"
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.CreateUserAsync(request));
    }

    [Fact]
    public async Task Should_ThrowException_WhenTenantNotFound()
    {
        var request = new CreateUserRequest
        {
            TenantId = Guid.NewGuid(),
            Username = "orphan",
            Password = "password123"
        };

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => _service.CreateUserAsync(request));
    }

    [Fact]
    public async Task Should_AssignAdminRole_WhenSpecified()
    {
        var request = new CreateUserRequest
        {
            TenantId = _tenant.Id,
            Username = "admin",
            Password = "password123",
            Role = UserRole.Admin
        };

        var result = await _service.CreateUserAsync(request);

        Assert.Equal(UserRole.Admin, result.Role);
    }

    // --- UpdateUserAsync ---

    [Fact]
    public async Task Should_UpdateEmail_WhenProvided()
    {
        var user = new User { TenantId = _tenant.Id, Username = "user", PasswordHash = "hash" };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        var result = await _service.UpdateUserAsync(user.Id,
            new UpdateUserRequest { Email = "updated@test.com" });

        Assert.Equal("updated@test.com", result.Email);
    }

    [Fact]
    public async Task Should_UpdateDisplayName_WhenProvided()
    {
        var user = new User { TenantId = _tenant.Id, Username = "user", PasswordHash = "hash" };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        var result = await _service.UpdateUserAsync(user.Id,
            new UpdateUserRequest { DisplayName = "New Name" });

        Assert.Equal("New Name", result.DisplayName);
    }

    [Fact]
    public async Task Should_UpdateRole_WhenProvided()
    {
        var user = new User { TenantId = _tenant.Id, Username = "user", PasswordHash = "hash", Role = UserRole.User };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        var result = await _service.UpdateUserAsync(user.Id,
            new UpdateUserRequest { Role = UserRole.Admin });

        Assert.Equal(UserRole.Admin, result.Role);
    }

    [Fact]
    public async Task Should_DeactivateUser_WhenIsActiveFalse()
    {
        var user = new User { TenantId = _tenant.Id, Username = "user", PasswordHash = "hash" };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        var result = await _service.UpdateUserAsync(user.Id,
            new UpdateUserRequest { IsActive = false });

        Assert.False(result.IsActive);
    }

    [Fact]
    public async Task Should_ThrowKeyNotFound_WhenUpdateNonExistentUser()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => _service.UpdateUserAsync(Guid.NewGuid(),
                new UpdateUserRequest { Email = "x@x.com" }));
    }

    // --- DeleteUserAsync ---

    [Fact]
    public async Task Should_DeleteUser_WhenExists()
    {
        var user = new User { TenantId = _tenant.Id, Username = "todelete", PasswordHash = "hash" };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        await _service.DeleteUserAsync(user.Id);

        var found = await _db.Users.FindAsync(user.Id);
        Assert.Null(found);
    }

    [Fact]
    public async Task Should_ThrowKeyNotFound_WhenDeleteNonExistentUser()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => _service.DeleteUserAsync(Guid.NewGuid()));
    }

    // --- GenerateApiKeyAsync ---

    [Fact]
    public async Task Should_GenerateApiKey_WithCorrectPrefix()
    {
        var user = new User { TenantId = _tenant.Id, Username = "user", PasswordHash = "hash" };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        var apiKey = await _service.GenerateApiKeyAsync(user.Id);

        Assert.StartsWith("ksh_", apiKey);
    }

    [Fact]
    public async Task Should_GenerateApiKey_WithCorrectLength()
    {
        var user = new User { TenantId = _tenant.Id, Username = "user", PasswordHash = "hash" };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        var apiKey = await _service.GenerateApiKeyAsync(user.Id);

        // ksh_ (4) + 32 chars = 36 total
        Assert.Equal(36, apiKey.Length);
    }

    [Fact]
    public async Task Should_PersistApiKey_ToDatabase()
    {
        var user = new User { TenantId = _tenant.Id, Username = "user", PasswordHash = "hash" };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        var apiKey = await _service.GenerateApiKeyAsync(user.Id);

        var updated = await _db.Users.FindAsync(user.Id);
        Assert.Equal(apiKey, updated!.ApiKey);
    }

    [Fact]
    public async Task Should_ReplaceExistingApiKey_WhenGeneratedAgain()
    {
        var user = new User { TenantId = _tenant.Id, Username = "user", PasswordHash = "hash", ApiKey = "ksh_old" };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        var newKey = await _service.GenerateApiKeyAsync(user.Id);

        Assert.NotEqual("ksh_old", newKey);
        var updated = await _db.Users.FindAsync(user.Id);
        Assert.Equal(newKey, updated!.ApiKey);
    }

    [Fact]
    public async Task Should_ThrowKeyNotFound_WhenGenerateApiKeyForNonExistentUser()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => _service.GenerateApiKeyAsync(Guid.NewGuid()));
    }

    // --- ResetPasswordAsync ---

    [Fact]
    public async Task Should_ResetPassword_AndHashNewPassword()
    {
        var user = new User
        {
            TenantId = _tenant.Id,
            Username = "user",
            PasswordHash = _authService.HashPassword("oldpassword")
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        var result = await _service.ResetPasswordAsync(user.Id, "newpassword");

        Assert.Equal("Password reset successfully", result);

        var updated = await _db.Users.FindAsync(user.Id);
        Assert.True(_authService.VerifyPassword("newpassword", updated!.PasswordHash));
        Assert.False(_authService.VerifyPassword("oldpassword", updated.PasswordHash));
    }

    [Fact]
    public async Task Should_ClearFirstLoginRequirement_WhenPasswordIsReset()
    {
        var user = new User
        {
            TenantId = _tenant.Id,
            Username = "first-login-user",
            PasswordHash = _authService.HashPassword("oldpassword"),
            MustChangePassword = true
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        await _service.ResetPasswordAsync(user.Id, "N3w!River-Stone-84");

        Assert.False((await _db.Users.FindAsync(user.Id))!.MustChangePassword);
    }

    [Fact]
    public async Task Should_ThrowKeyNotFound_WhenResetPasswordForNonExistentUser()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => _service.ResetPasswordAsync(Guid.NewGuid(), "newpassword"));
    }
}
