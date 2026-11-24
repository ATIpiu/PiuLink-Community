using System.IO;
using System.Net.Security;

namespace PiuLink.Host.Services;

internal static partial class FileTransferSender
{
    public static async Task SendAsync(FileTransferPairingStore.Peer peer, string path, CancellationToken token,
        IProgress<(long Sent, long Total)>? progress = null)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        var size = input.Length;
        if (size > 2_147_483_648) throw new IOException("文件不能超过 2 GiB。");
        if (peer.CertificateFingerprint is null)
            throw new IOException("请更新手机并打开 PiuLink，以迁移文件传输授权。");
        peer = new FileTransferPairingStore().Peers().SingleOrDefault(saved => saved.CertificateFingerprint == peer.CertificateFingerprint)
            ?? throw new IOException("此设备的文件传输配对已移除，请重新连接手机后再发送。");
        using var identity = PairedDisplaySecurity.LoadServerCertificate();
        await SendToPeerAsync(input, size, Path.GetFileName(path), peer.DeviceId, FileTransferPairingStore.Token(peer),
            peer.CertificateFingerprint!, new[] { peer.Address }.Concat(peer.Addresses ?? []), identity,
            PairedDisplaySecurity.IsPaired, token, progress).ConfigureAwait(false);
    }

    internal static Task SendPayloadAsync(SslStream stream, Stream input, long size, string filename,
        FileTransferPairingStore.Peer peer, CancellationToken token, IProgress<(long Sent, long Total)>? progress = null) =>
        SendPayloadAsync(stream, input, size, filename, peer.DeviceId, FileTransferPairingStore.Token(peer), token, progress);
}
