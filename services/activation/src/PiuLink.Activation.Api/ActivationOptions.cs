using System.ComponentModel.DataAnnotations;

namespace PiuLink.Activation;

public sealed class ActivationOptions
{
    public const string SectionName = "PiuLinkActivation";

    [Required]
    public string DatabasePath { get; init; } = "data/piulink-activation.db";

    [Required]
    public string CodeHmacKey { get; init; } = string.Empty;

    [Required]
    public string AdminKey { get; init; } = string.Empty;

    [Required]
    public string SigningKeyPemPath { get; init; } = string.Empty;

    [Required]
    public string KeyId { get; init; } = "piulink-activation-v1";
}
