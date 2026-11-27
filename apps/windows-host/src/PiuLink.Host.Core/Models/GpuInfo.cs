namespace PiuLink.Host.Core.Models;

public enum GpuVendor
{
    Unknown,
    Nvidia,
    Amd,
    Intel
}

public sealed record GpuInfo(string Name, GpuVendor Vendor);

