namespace PiuLink.Host.Services;

public sealed record FileTransferPairRequest(string DeviceId, string DeviceName, string? Token, string RemoteAddress, bool RefreshOnly, string CertificateFingerprint);
public sealed record FileTransferPairResponse(string Status, string Token, int Port, string Transport = "tls-v2");
