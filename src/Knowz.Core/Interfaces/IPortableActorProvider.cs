namespace Knowz.Core.Interfaces;

/// <summary>Authenticated local actor for owner-only imported recovery records. Background scopes return null.</summary>
public interface IPortableActorProvider
{
    Guid? ActorId { get; }
}
