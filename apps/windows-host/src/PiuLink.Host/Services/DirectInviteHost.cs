using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using PiuLink.Rendezvous;

namespace PiuLink.Host.Services;

internal static class DirectInviteHost
{
    private sealed record Offer(string Id, long Time, string Cert, string Wrapped, string Signature);
    private sealed record Pending(Offer Offer, string Ticket);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task RunAsync(string owner, HostInvite invite, Func<string, CancellationToken, Task<bool>> approve,
        Func<string,Task> log, CancellationToken token)
    {
        var running = new List<Task>();
        var seen = new Dictionary<string,long>();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var identity = PairedDisplaySecurity.LoadServerCertificate();
        var cert = Convert.ToBase64String(identity.RawData);
        try
        {
            while (!token.IsCancellationRequested)
            {
                running.RemoveAll(task => task.IsCompleted);
                if (running.Count < 4)
                {
                    try
                    {
                        using var request = HostInvite.Post("host", owner, 2);
                        request.Content = JsonContent.Create(new {hostId = invite.HostId, cert});
                        using var response = await HostInvite.DirectoryClient.SendAsync(request,token);
                        if (response.StatusCode == HttpStatusCode.OK)
                        {
                            var job = await response.Content.ReadFromJsonAsync<Pending>(Json,token);
                            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                            foreach(var id in seen.Where(p=>p.Value < now-120).Select(p=>p.Key).ToArray()) seen.Remove(id);
                            if (job != null && job.Offer.Id.Length == 32 && job.Offer.Id.All(char.IsAsciiHexDigit) &&
                                job.Offer.Time >= now-65 && job.Offer.Time <= now+45 && seen.TryAdd(job.Offer.Id,now))
                                running.Add(ServeAsync(job,invite.Room,identity,approve,log,lifetime.Token));
                        }
                        else if (response.StatusCode != HttpStatusCode.NoContent) await Task.Delay(2500,token);
                    }
                    catch (Exception) when (!token.IsCancellationRequested) { await Task.Delay(2500,token); }
                }
                await Task.Delay(1400,token);
            }
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested) { }
        finally { lifetime.Cancel(); await Task.WhenAll(running); }
    }

    private static async Task ServeAsync(Pending pending,string room,X509Certificate2 identity,
        Func<string,CancellationToken,Task<bool>> approve,Func<string,Task> log,CancellationToken token)
    {
        try
        {
            var offer=pending.Offer;
            using var device = new X509Certificate2(Convert.FromBase64String(offer.Cert));
            using var publicKey = device.GetRSAPublicKey();
            var signed = Encoding.ASCII.GetBytes($"piulink-punch-v2\n{room}\n{offer.Id}\n{offer.Time}\n{offer.Wrapped}");
            if(publicKey == null || !publicKey.VerifyData(signed,Convert.FromBase64String(offer.Signature),
                HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1)) return;
            using var privateKey=identity.GetRSAPrivateKey();
            var secret=privateKey!.Decrypt(Convert.FromBase64String(offer.Wrapped),RSAEncryptionPadding.OaepSHA256);
            if(secret.Length!=32) return;
            if(!PairedDisplaySecurity.IsPaired(device) && !await approve(device.GetCertHashString(HashAlgorithmName.SHA256), token)) return;
            var lease = new {leaseId=offer.Id,ticket=pending.Ticket,transportKey=Convert.ToBase64String(secret).TrimEnd('=').Replace('+','-').Replace('/','_'),
                sourceClientId="source",hostClientId="host"};
            CryptographicOperations.ZeroMemory(secret);
            using var process = new Process {StartInfo = new(Path.Combine(AppContext.BaseDirectory,"piulink-direct.exe")) {
                RedirectStandardInput=true,RedirectStandardOutput=true, UseShellExecute=false,CreateNoWindow=true}};
            process.Start();
            try
            {
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new {role="host",clientId="host",lease}));
                await process.StandardInput.FlushAsync();
                while (await process.StandardOutput.ReadLineAsync(token) is { } line)
                {
                    // Sidecar events expose only route status, traffic counters and sanitized errors.
                    await log("P2P " + line);
                }
            }
            finally
            {
                process.StandardInput.Close();
                if(!process.HasExited) process.Kill();
                await process.WaitForExitAsync();
            }
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested) { }
        catch(Exception ex) {await log("P2P session failed: "+ex.GetType().Name);}
    }
}
