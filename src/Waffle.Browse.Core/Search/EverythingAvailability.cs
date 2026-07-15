namespace Waffle.Browse.Core.Search;

public enum EverythingAvailabilityKind
{
    Available,
    SdkMissing,
    NotRunning,
    IndexLoading,
    Error
}

public sealed record EverythingAvailability(
    EverythingAvailabilityKind Kind,
    string Message)
{
    public bool IsAvailable => Kind == EverythingAvailabilityKind.Available;
}
