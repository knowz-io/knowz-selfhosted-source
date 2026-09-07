using Knowz.Core.Entities;
using Knowz.SelfHosted.Application.Models;

namespace Knowz.SelfHosted.Application.Extensions;

public static class UserMappingExtensions
{
    public static UserDto ToDto(this User user, string? timeZonePreference = null)
    {
        return new UserDto
        {
            Id = user.Id,
            Username = user.Username,
            Email = user.Email,
            DisplayName = user.DisplayName,
            Role = user.Role,
            TenantId = user.TenantId,
            TenantName = user.Tenant?.Name,
            IsActive = user.IsActive,
            MustChangePassword = user.MustChangePassword,
            ApiKey = user.ApiKey,
            CreatedAt = user.CreatedAt,
            LastLoginAt = user.LastLoginAt,
            TimeZonePreference = timeZonePreference,
        };
    }
}
