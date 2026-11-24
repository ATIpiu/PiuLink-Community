namespace PiuLink.Host.Core.Install;

/// <summary>
/// Trust anchors compiled into PiuLink Host. The external manifest is informative and must match
/// these values exactly; editing a staged manifest can never authorize a different elevated payload.
/// </summary>
public static class SunshinePinnedRelease
{
    public static BundledComponentManifest Manifest { get; } = new(
        1,
        "LizardByte.Sunshine",
        "2026.516.143833",
        "x64",
        "Sunshine-Windows-AMD64-installer.msi",
        new Uri("https://github.com/LizardByte/Sunshine/releases/download/v2026.516.143833/Sunshine-Windows-AMD64-installer.msi"),
        "E7208B11A4AB9DD89871133A054BBB8DC55DFBBA408227B0ECCAB22C60B273A2",
        "CN=David Lane, O=David Lane, L=KISSIMMEE, S=Florida, C=US",
        "A7269D36C49502D162A7E6FB88C30819DDB03F01",
        "GPL-3.0-only",
        "Sunshine-GPL-3.0.txt",
        "3972DC9744F6499F0F9B2DBF76696F2AE7AD8AF9B23DDE66D6AF86C9DFB36986",
        new Uri("https://github.com/LizardByte/Sunshine/blob/v2026.516.143833/LICENSE"),
        "v2026.516.143833",
        "14ffa6fdaa53f7b51512be2b3d24f3939695403c",
        new Uri("https://github.com/LizardByte/Sunshine/tree/v2026.516.143833"));
}
