namespace PiuLink.Host.Core.Models;

public sealed record ComponentHealth(
    string Name,
    ComponentState State,
    string Summary,
    string? Detail = null)
{
    public bool IsReady => State == ComponentState.Ready;
}

