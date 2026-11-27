using System.Diagnostics;
using System.IO;
using System.Text.Json;
using PiuLink.Host.Core.Sunshine;

namespace PiuLink.Host.Services;

// Runs in the interactive user's session. Never delegates display APIs to Session 0.
internal static class DisplayRecoveryJournal
{
    internal sealed record Entry(string Owner, string RequestId, DateTimeOffset ExpiresAt,
        DisplayTopologyService.Snapshot Desktop, string Configuration, string Phase = "Preparing", string? Fault = null,
        string? CaptureKey = null, string? PreparedDeviceId = null, bool Exclusive = false, bool TransientRoute = false,
        SessionMicrophoneRoute? Microphone = null);
    private static readonly JsonSerializerOptions Json = new() { IncludeFields = true, WriteIndented = true };
    private static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PiuLink", "DisplayRecovery");
    private static string JournalPath => Path.Combine(DirectoryPath, "transaction.json");
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly object StateGate = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> Aborted = new();
    public static bool IsAborted(string owner, string requestId) => Aborted.ContainsKey(owner + "/" + requestId);

    public static async Task<bool> AbortAsync(string owner, string requestId, CancellationToken token)
    {
        if (!Guid.TryParseExact(requestId, "D", out _)) return false;
        lock (StateGate)
        {
            foreach (var expired in Aborted.Where(item => item.Value < DateTimeOffset.UtcNow).ToArray())
                Aborted.TryRemove(expired.Key, out _);
            Aborted[owner + "/" + requestId] = DateTimeOffset.UtcNow.AddMinutes(2);
        }
        while (Pending)
        {
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(DirectoryPath);
            FileStream writer;
            try { writer = new FileStream(Path.Combine(DirectoryPath, "recovery.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
            {
                await Task.Delay(75, token);
                continue;
            }
            using (writer)
            {
                var current = Read();
                if (current is null) return true;
                if (current.Owner != owner || current.RequestId != requestId) return false;
                if (current.Phase is "Ready" or "Preparing" or "Streaming")
                    Save(current with { Phase = "Aborted", ExpiresAt = DateTimeOffset.UtcNow });
                return true;
            }
        }
        return true;
    }

    internal static bool BeginSwitch(string owner, string requestId)
    {
        if (!Pending || !Guid.TryParseExact(requestId, "D", out _)) return false;
        try {
            using var writer = AcquireSessionWriter();
            var entry = Read();
            if (entry == null || !OwnsStream(owner, requestId)) return false;
            Save(entry with { Phase = "Switching", ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(6) });
            return true;
        } catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33) { return false; }
    }

    internal static bool CanSwitch(Entry? entry, string owner) =>
        entry is { Phase: "Switching" } && entry.Owner == owner && entry.ExpiresAt > DateTimeOffset.UtcNow;

    public static void MarkReady(string owner, string requestId, string? captureKey, string? deviceId, bool exclusive, bool transientRoute)
    {
        lock (StateGate) {
            var current = Read();
            if (current is null || current.Owner != owner || current.RequestId != requestId ||
                current.Phase == "Aborted" || IsAborted(owner, requestId) || current.ExpiresAt <= DateTimeOffset.UtcNow)
                throw new OperationCanceledException("Display preparation was cancelled or expired.");
            Save(current with { Phase = "Ready", CaptureKey = captureKey, PreparedDeviceId = deviceId, Exclusive = exclusive, TransientRoute = transientRoute });
        }
    }
    public static Entry? Read()
    {
        for (int attempt = 0; ; attempt++) {
            try {
                using var stream = new FileStream(JournalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return JsonSerializer.Deserialize<Entry>(stream, Json);
            } catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException && attempt < 6) {
                Thread.Sleep(25);
            }
        }
    }
    internal static bool OwnsStream(string owner, string requestId) =>
        Read() is { Phase: "Ready" or "Streaming" } entry && entry.Owner == owner &&
        entry.RequestId == requestId && !IsAborted(owner, requestId) &&
        (entry.Phase == "Streaming" || entry.ExpiresAt > DateTimeOffset.UtcNow);

    internal static void EnsureSessionMicrophone(string owner, string id)
    {
        for (int attempt = 0; attempt < 10; attempt++) {
            try {
                using var writer = AcquireSessionWriter();
                var entry = Read();
                if (entry != null && OwnsStream(owner, id)) EnsureMicrophone(entry);
                return;
            } catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33 && attempt < 9) {
                Thread.Sleep(50);
            }
        }
    }

    internal static bool ProbeVirtualMicrophone(string owner, string id)
    {
        string endpoint;
        using (AcquireSessionWriter()) {
            var entry = Read();
            if (entry == null || !OwnsStream(owner, id)) throw new UnauthorizedAccessException();
            var route = EnsureMicrophone(entry) ?? throw new InvalidOperationException("virtual_microphone_unavailable");
            endpoint = route.Endpoint;
        }
        return SessionInputStatus.MicrophoneInUse(endpoint);
    }

    // Caller owns recovery.lock. Save before changing any default role so disconnect or
    // a Host process restart can always recover the pre-stream devices.
    private static SessionMicrophoneRoute? EnsureMicrophone(Entry entry)
    {
        if (entry.Microphone != null) return entry.Microphone;
        var route = SessionMicrophoneRoute.Capture();
        if (route == null) return null;
        Save(entry with { Microphone = route });
        try { route.Switch(restore: false); }
        catch {
            try { route.Switch(restore: true); } catch { }
            Save(entry);
            throw;
        }
        return route;
    }

    private static Entry RestoreMicrophone(Entry entry)
    {
        if (entry.Microphone == null) return entry;
        try { entry.Microphone.Switch(restore: true); }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidOperationException or IOException) {
            // Audio endpoint recovery failures must not block physical display restoration or wedge the guardian.
        }
        var restored = entry with { Microphone = null };
        Save(restored);
        return restored;
    }

    internal static FileStream AcquireSessionWriter()
    {
        Directory.CreateDirectory(DirectoryPath);
        return new(Path.Combine(DirectoryPath, "recovery.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public static bool Pending => File.Exists(JournalPath);

    private static void RetryFileIo(Action action)
    {
        for (int attempt = 0; ; attempt++) {
            try { action(); return; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException && attempt < 8) {
                Thread.Sleep(30);
            }
        }
    }

    public static void Save(Entry entry)
    {
        Directory.CreateDirectory(DirectoryPath);
        var temporary = JournalPath + "." + Environment.ProcessId + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) {
                JsonSerializer.Serialize(stream, entry, Json); stream.Flush(true);
            }
            RetryFileIo(() => File.Move(temporary, JournalPath, true));
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static bool CanResume(Entry entry, string owner) =>
        entry.Owner == owner && entry.Phase is "Streaming" or "Ready" or "Aborted" or "AwaitingApplication";

    public static FileStream Begin(string owner, string requestId, DisplayTopologyService.Snapshot desktop,
        string configuration, Entry? resumed = null)
    {
        if (IsAborted(owner, requestId)) throw new OperationCanceledException("This display request was cancelled.");
        Directory.CreateDirectory(DirectoryPath);
        FileStream preparationLock;
        try { preparationLock = new FileStream(Path.Combine(DirectoryPath, "recovery.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33) { throw new DisplayRecoveryInProgressException(); }
        try {
            // Another transaction may have committed between the initial test and this lock acquisition.
            var existing = Read();
            if (resumed is null ? existing is not null : existing is null || existing.RequestId != resumed.RequestId ||
                !(resumed.Phase == "Switching" ? CanSwitch(existing, owner) : CanResume(existing, owner))) throw new DisplayRecoveryInProgressException();
            if (IsAborted(owner, requestId)) throw new OperationCanceledException("This display request was cancelled.");
            Save(new(owner, requestId, DateTimeOffset.UtcNow.AddSeconds(45),
                existing?.Desktop ?? desktop,
                existing?.Configuration ?? configuration,
                CaptureKey: resumed is not null ? existing?.CaptureKey : null,
                PreparedDeviceId: resumed is not null ? existing?.PreparedDeviceId : null,
                Exclusive: resumed is not null && existing is not null && existing.Exclusive,
                TransientRoute: resumed is not null && existing is not null && existing.TransientRoute,
                Microphone: existing?.Microphone));
            EnsureGuardian(); return preparationLock;
        } catch { preparationLock.Dispose(); throw; }
    }

    public static void EnsureGuardian()
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
        };
        start.ArgumentList.Add("--display-recovery-agent");
        using var process = Process.Start(start) ?? throw new IOException("Display recovery agent could not start.");
    }

    internal static bool IsObsoleteSuperScreenState(JsonElement state, string selectedId, SunshineDisplay[] displays)
    {
        if (!displays.Any(d => !d.IsVirtual && d.Bounds != null) ||
            !displays.Any(d => d.IsVirtual && d.DeviceId.Equals(selectedId, StringComparison.OrdinalIgnoreCase)))
            return false;

        static Guid[] ReadTargets(JsonElement root, string name)
        {
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var section) ||
                section.ValueKind != JsonValueKind.Object || !section.TryGetProperty("topology", out var groups) ||
                groups.ValueKind != JsonValueKind.Array || groups.GetArrayLength() == 0) return [];
            var ids = new HashSet<Guid>();
            foreach (var group in groups.EnumerateArray()) {
                if (group.ValueKind != JsonValueKind.Array || group.GetArrayLength() == 0) return [];
                foreach (var id in group.EnumerateArray()) {
                    if (id.ValueKind != JsonValueKind.String || !Guid.TryParse(id.GetString(), out var parsed)) return [];
                    ids.Add(parsed);
                }
            }
            return ids.ToArray();
        }
        var initial = ReadTargets(state, "initial");
        var modified = ReadTargets(state, "modified");
        if (!Guid.TryParse(selectedId, out var target) || modified.Length != 1 || modified[0] != target ||
            initial.Length == 0 || displays.Any(d => !Guid.TryParse(d.DeviceId, out _))) return false;
        var available = displays.Select(d => Guid.Parse(d.DeviceId)).ToHashSet();
        return !initial.Any(available.Contains);
    }

    // Called with an authenticated engine reservation and the recovery writer lock. An
    // obsolete Sunshine restore job otherwise turns every monitor on after our restore.
    internal static async Task RetireObsoleteSuperScreenStateAsync(SunshineApiClient api, SunshineDeviceCredential credential,
        string selectedId, SunshineDisplay[] displays, CancellationToken token)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "engine", "config", "display_device.state");
        if (!File.Exists(path)) return;
        var contents = await File.ReadAllBytesAsync(path, token);
        try {
            using var state = JsonDocument.Parse(contents);
            if (!IsObsoleteSuperScreenState(state.RootElement, selectedId, displays)) return;
        } catch (JsonException) { return; }
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(contents));
        var archive = Path.Combine(DirectoryPath, $"superseded-engine-{digest}.json");
        if (!File.Exists(archive)) {
            var temporary = archive + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                    file.Write(contents); file.Flush(true);
                }
                File.Move(temporary, archive);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        if (!contents.SequenceEqual(await File.ReadAllBytesAsync(archive, token)))
            throw new IOException("The archived engine recovery state could not be verified.");
        if (File.Exists(path) && contents.SequenceEqual(await File.ReadAllBytesAsync(path, token)))
            await api.ResetDisplayPersistenceAsync(credential.Username, credential.Secret, token);
    }

    public static async Task<bool> RestoreAsync(bool engineDead = false, Entry? observed = null, bool requireIdleApplication = false)
    {
        if (!await Gate.WaitAsync(0)) return false;
        Directory.CreateDirectory(DirectoryPath);
        FileStream? recoveryLock = null;
        try {
            try { recoveryLock = new FileStream(Path.Combine(DirectoryPath, "recovery.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { return false; }
            var entry = Read(); if (entry is null) return true;
            if (observed is not null && (entry.RequestId != observed.RequestId || entry.Owner != observed.Owner || entry.Phase != observed.Phase)) return false;
            var desktopRecovered = entry.Phase == "AwaitingEngine";
            if (desktopRecovered && engineDead) return false;
            var topologyRestored = desktopRecovered;
            try {
                var credential = new SunshineManagedIdentity().TryRead();
                using var api = SunshineApiClient.CreateForLoopback();
                if (!engineDead) {
                    if (credential is null) throw new IOException("Local engine credential is unavailable.");
                    var overview = await api.GetOverviewAsync(credential.Username, credential.Secret);
                    if (overview.HasActiveStreams || (requireIdleApplication && overview.HasCurrentApplication)) return false;
                    try
                    {
                        await api.RegisterPreparedDisplayLeaseAsync(entry.RequestId, entry.Owner,
                            credential.Username, credential.Secret, CancellationToken.None, ready: false);
                    }
                    catch (System.Net.Http.HttpRequestException error) when (error.StatusCode == System.Net.HttpStatusCode.BadRequest)
                    {
                        if ((await api.GetOverviewAsync(credential.Username, credential.Secret)).HasActiveStreams) return false;
                        throw;
                    }
                }
                entry = RestoreMicrophone(entry);
                if (!desktopRecovered) {
                    Save(entry with { Phase = "Restoring" });
                    topologyRestored = DisplayTopologyService.RestorePhysicalDesktop(entry.Desktop);
                    if (!topologyRestored) throw new IOException("Windows did not verify the recovered desktop.");
                    desktopRecovered = true;
                }
                if (engineDead) {
                    Save(entry with { Phase = "AwaitingEngine", Fault = "Desktop recovered; configuration reconciliation awaits the engine." });
                    return false;
                }
                var state = await api.GetOverviewAsync(credential!.Username, credential.Secret);
                if (state.HasActiveStreams) throw new IOException("A stream became active during recovery.");
                if (!await api.RestoreDisplayConfigurationAsync(entry.Configuration, credential.Username, credential.Secret,
                        allowRestart: !state.HasRetainedApplication) || ((entry.TransientRoute || entry.Exclusive) && state.HasRetainedApplication))
                {
                    // The configured engine route may differ from the prepared physical target.
                    // Retain its owner and target while the game is suspended so it can resume.
                    Save(entry with { Phase = "AwaitingApplication", Fault = null });
                    return false;
                }
                for (var attempt = 0; ; attempt++) {
                    try {
                        await api.RegisterPreparedDisplayLeaseAsync(entry.RequestId, entry.Owner,
                            credential.Username, credential.Secret, CancellationToken.None, action: "abort"); break;
                    } catch (Exception) when (attempt < 10) { await Task.Delay(500); }
                }
                if (!DisplayTopologyService.AllSnapshotTargetsActive(entry.Desktop)) {
                    // Finish recovery of connected outputs, retaining the complete original
                    // snapshot for a later explicit recovery without blocking new sessions.
                    Save(entry with { Phase = "PartialRecovered", Fault = "Some original displays are disconnected; their saved layout is retained." });
                    RetryFileIo(() => File.Move(JournalPath, Path.Combine(DirectoryPath, "partial-" + entry.RequestId + ".json"), true));
                } else RetryFileIo(() => File.Delete(JournalPath));
                return true;
            } catch (Exception error) {
                if (desktopRecovered && error is System.Net.Http.HttpRequestException { StatusCode: null } or TaskCanceledException) {
                    Save(entry with { Phase = "AwaitingEngine", Fault = "Desktop recovered; configuration reconciliation awaits the engine." });
                    return false;
                }
                Save(entry with { Phase = "Fault", Fault = error.Message });
                return false;
            }
        } finally { recoveryLock?.Dispose(); Gate.Release(); }
    }

    public static async Task<string> RepairDesktopAsync()
    {
        Directory.CreateDirectory(DirectoryPath);
        var credential = new SunshineManagedIdentity().TryRead() ?? throw new IOException("请先连接本机串流引擎，再修复桌面。");
        using var api = SunshineApiClient.CreateForLoopback();
        async Task CheckIdle() {
            var state = await api.GetOverviewAsync(credential.Username, credential.Secret);
            if (state.HasActiveStreams || state.HasRetainedApplication) throw new IOException("请先结束串流及保留的应用，再修复桌面。");
        }
        await CheckIdle();
        var current = Read();
        if (current != null) {
            if (current.Phase is "Preparing" or "Switching" or "Ready" && current.ExpiresAt > DateTimeOffset.UtcNow)
                throw new IOException("连接正在准备，请稍后重试。");
            if (!await RestoreAsync(observed: current, requireIdleApplication: true))
                throw new IOException(Read()?.Fault ?? "屏幕事务仍在处理，请稍后重试。");
            if (!File.Exists(Path.Combine(DirectoryPath, $"partial-{current.RequestId}.json"))) return "已恢复当前会话保存的桌面布局。";
        }
        using var writer = new FileStream(Path.Combine(DirectoryPath, "recovery.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        await CheckIdle();
        if (Pending) throw new IOException("另一台设备正在准备连接，请稍后重试。");
        var latestRecovered = Directory.EnumerateFiles(DirectoryPath, "recovered-partial-*.json")
            .Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max();
        var archives = Directory.EnumerateFiles(DirectoryPath, "partial-*.json")
            .Where(path => File.GetLastWriteTimeUtc(path) > latestRecovered)
            .OrderByDescending(File.GetLastWriteTimeUtc).ToArray();
        foreach (var path in archives) {
            var entry = JsonSerializer.Deserialize<Entry>(File.ReadAllText(path), Json) ?? throw new IOException("保存的桌面布局无法读取。");
            if (!DisplayTopologyService.AllSnapshotTargetsConnected(entry.Desktop)) continue;
            if (!DisplayTopologyService.RestorePhysicalDesktop(entry.Desktop) || !DisplayTopologyService.AllSnapshotTargetsActive(entry.Desktop))
                throw new IOException("Windows 尚未恢复全部显示器的原始位置。已保留布局，可重试。");
            File.Move(path, Path.Combine(DirectoryPath, "recovered-" + Path.GetFileName(path)), true);
            return "已恢复保存的显示器位置和缩放。";
        }
        if (archives.Length > 0) throw new IOException("请接回原显示器，再点“修复桌面布局”。已保留原始位置。");
        if (current == null) {
            DisplayTopologyService.RestoreLastExtendedDesktop();
            foreach (var target in DisplayTopologyService.GetActiveVirtualTargets()) DisplayTopologyService.DisconnectVirtualDesktop(target);
            if (!DisplayTopologyService.IsPhysicalDesktopOnly()) throw new IOException("Windows 未确认物理桌面恢复。");
        }
        return "桌面恢复完成。";
    }

    public static async Task RunGuardianAsync()
    {
        Mutex? mutex = null;
        var primary = false;
        for (int attempt = 0; attempt < 6; attempt++) {
            mutex = new Mutex(true, @"Local\PiuLink.DisplayRecovery.Agent", out primary);
            if (primary) break;
            mutex.Dispose();
            mutex = null;
            if (!Pending || attempt == 5) return;
            await Task.Delay(100);
        }
        using var ownedMutex = mutex!;
        using var api = SunshineApiClient.CreateForLoopback();
        DateTimeOffset? disconnectedSince = null;
        var wasStreaming = false;
        string? lastRequestId = null;
        int faultAttempts = 0;
        DateTimeOffset nextFaultRetry = DateTimeOffset.MinValue;
        while (Pending) {
            try {
                var entry = Read(); if (entry is null) break;
                if (entry.RequestId != lastRequestId) {
                    lastRequestId = entry.RequestId;
                    wasStreaming = false;
                    disconnectedSince = null;
                    faultAttempts = 0;
                    nextFaultRetry = DateTimeOffset.MinValue;
                }
                if (entry.Phase != "Fault") {
                    faultAttempts = 0;
                    nextFaultRetry = DateTimeOffset.MinValue;
                }
                var credential = new SunshineManagedIdentity().TryRead();
                bool? active = null;
                bool retained = false;
                bool oneStream = false;
                if (credential != null) {
                    try {
                        var state = await api.GetOverviewAsync(credential.Username, credential.Secret);
                        active = state.HasActiveStreams;
                        oneStream = state.ActiveStreamCount == 1;
                        retained = state.HasRetainedApplication;
                    }
                    catch (Exception) { }
                }
                var processes = Process.GetProcessesByName("sunshine");
                var engineDead = processes.Length == 0;
                foreach (var process in processes) process.Dispose();
                if (active == true) {
                    wasStreaming = true;
                    disconnectedSince = null;
                    if (entry.Phase == "Ready" || (oneStream && entry.Phase == "Streaming" && entry.Microphone == null && VirtualMicrophoneDevice.HasNativeDriver)) {
                        try {
                            using var writer = new FileStream(Path.Combine(DirectoryPath, "recovery.lock"), FileMode.OpenOrCreate,
                                FileAccess.ReadWrite, FileShare.None);
                            var current = Read();
                            if (current?.RequestId == entry.RequestId && current.Phase is "Ready" or "Streaming") {
                                if (current.Phase == "Ready") { current = current with { Phase = "Streaming" }; Save(current); }
                                if (oneStream && current.Microphone == null && VirtualMicrophoneDevice.HasNativeDriver)
                                    EnsureMicrophone(current);
                            }
                        } catch (IOException) { /* The preparing or recovering process still owns the journal. */ }
                    }
                } else {
                    if ((active == false || engineDead) && entry.Microphone != null) {
                        try {
                            using var writer = AcquireSessionWriter();
                            var current = Read();
                            if (current?.RequestId == entry.RequestId) entry = RestoreMicrophone(current);
                        } catch (IOException) { /* Another process holds recovery.lock; retry on the next tick. */ }
                    }
                    wasStreaming |= entry.Phase == "Streaming";
                    if (active == false) disconnectedSince ??= DateTimeOffset.UtcNow;
                    else disconnectedSince = null;
                    var released = PhysicalDisplayRecoveryPolicy.CanRestoreReleasedSession(entry.Phase, active, retained);
                    var canAttemptFault = entry.Phase != "Fault" ||
                        (faultAttempts < 3 && DateTimeOffset.UtcNow >= nextFaultRetry);
                    if (canAttemptFault &&
                        !(entry.Phase == "AwaitingEngine" && engineDead) &&
                        !(entry.Phase == "AwaitingApplication" && retained) &&
                        (released || PhysicalDisplayRecoveryPolicy.ShouldRestore(active, !engineDead, true,
                            entry.Phase is "Preparing" or "Ready" or "Switching", DateTimeOffset.UtcNow >= entry.ExpiresAt,
                            wasStreaming, disconnectedSince is null ? null : DateTimeOffset.UtcNow - disconnectedSince.Value)))
                    {
                        var restored = await RestoreAsync(engineDead, entry, requireIdleApplication: released);
                        if (!restored && Read()?.Phase == "Fault") {
                            faultAttempts++;
                            nextFaultRetry = DateTimeOffset.UtcNow.AddSeconds(faultAttempts switch { 1 => 10, 2 => 30, _ => 60 });
                        }
                    }
                }
            } catch (Exception error) {
                Directory.CreateDirectory(DirectoryPath);
                await File.AppendAllTextAsync(Path.Combine(DirectoryPath, "recovery.log"), $"{DateTimeOffset.UtcNow:O} {error.GetType().Name}: {error.Message}\n");
            }
            await Task.Delay(2000);
        }
    }
}
