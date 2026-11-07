using System.Security.Claims;
using Knowz.Core.Interfaces;
namespace Knowz.SelfHosted.API.Services;
public sealed class HttpPortableActorProvider(IHttpContextAccessor http) : IPortableActorProvider
{
    public Guid? ActorId => http.HttpContext?.User is { Identity.IsAuthenticated: true } user
        && Guid.TryParse((user.FindFirstValue("sub") ?? user.FindFirstValue(ClaimTypes.NameIdentifier)), out var actor) && actor != Guid.Empty ? actor : null;
}
