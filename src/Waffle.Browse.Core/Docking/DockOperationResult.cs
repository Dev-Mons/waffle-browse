namespace Waffle.Browse.Core.Docking;

public sealed record DockOperationResult(bool Accepted, DockLayoutState State, string? Reason = null);
