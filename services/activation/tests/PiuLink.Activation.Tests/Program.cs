using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PiuLink.Activation;

var directory = Path.Combine(Path.GetTempPath(), $"piulink-activation-test-{Guid.NewGuid():N}");
Directory.CreateDirectory(directory);
try
{
    var store = new ActivationStore(
        Path.Combine(directory, "activation.db"),
        RandomNumberGenerator.GetBytes(32));
    await store.InitializeAsync();

    var generated = await store.CreateCodesAsync(1, "test batch");
    AssertEqual(1, generated.Count);
    AssertEqual(generated[0].Code, ActivationCode.Normalize(generated[0].Code.ToLowerInvariant().Replace("-", " ")));

    var deviceA = new string('A', 64);
    var deviceB = new string('B', 64);
    var deviceC = new string('C', 64);
    var first = await store.RedeemAsync(generated[0].Code, deviceA);
    var retry = await store.RedeemAsync(generated[0].Code, deviceA);
    var secondDevice = await store.RedeemAsync(generated[0].Code, deviceB);
    var invalid = await store.RedeemAsync("PLH1-AAAA-AAAA-AAAA-AAAA-AAAA-AAAA-AAAA", deviceA);

    AssertEqual(RedeemState.Activated, first.State);
    AssertEqual(RedeemState.AlreadyActivatedOnThisDevice, retry.State);
    AssertEqual(RedeemState.Activated, secondDevice.State);
    AssertEqual(1, first.ActivationCount);
    AssertEqual(2, secondDevice.ActivationCount);
    AssertEqual(RedeemState.UsedByAnotherDevice, (await store.RedeemAsync(generated[0].Code, deviceC)).State);
    var fullRetry = await store.RedeemAsync(generated[0].Code, deviceA.ToLowerInvariant());
    AssertEqual(RedeemState.AlreadyActivatedOnThisDevice, fullRetry.State);
    AssertEqual(2, fullRetry.ActivationCount);
    AssertEqual(RedeemState.InvalidCode, invalid.State);
    AssertEqual(first.ActivationId, retry.ActivationId);

    var importedCode = ActivationCode.Generate();
    var imported = await store.ImportCodeAsync(importedCode, "store-order:123", "paid order");
    var importedRetry = await store.ImportCodeAsync(importedCode, "store-order:123", "paid order");
    var importedConflict = await store.ImportCodeAsync(ActivationCode.Generate(), "store-order:123", "paid order");
    AssertEqual(ImportCodeState.Imported, imported.State);
    AssertEqual(ImportCodeState.AlreadyImported, importedRetry.State);
    AssertEqual(imported.ActivationCode?.Id, importedRetry.ActivationCode?.Id);
    AssertEqual(ImportCodeState.ExternalIdConflict, importedConflict.State);

    using var signerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    using var signer = new ActivationReceiptSigner(signerKey, "test-key");
    var receipt = signer.Sign(first.ActivationId!, deviceA, first.ActivationCount, first.MaxActivations);
    AssertEqual(3, receipt.Split('.').Length);
    AssertEqual(true, signer.ExportPublicKeyPem().Contains("BEGIN PUBLIC KEY", StringComparison.Ordinal));

    var verified = PiuLink.Host.Core.Activation.ActivationReceiptVerifier.Verify(receipt, deviceA, signer.ExportPublicKeyPem());
    AssertEqual(true, verified.CanRun);
    AssertEqual(1, verified.ActivationCount);
    AssertEqual(2, verified.MaxActivations);
    AssertEqual(false, PiuLink.Host.Core.Activation.ActivationReceiptVerifier.Verify(receipt, deviceB, signer.ExportPublicKeyPem()).CanRun);
    var parts = receipt.Split('.');
    parts[1] = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"activation_count\":0}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    AssertEqual(false, PiuLink.Host.Core.Activation.ActivationReceiptVerifier.Verify(string.Join('.', parts), deviceA, signer.ExportPublicKeyPem()).CanRun);
    var legacyReceipt = signer.Sign(first.ActivationId!, deviceA);
    AssertEqual(true, PiuLink.Host.Core.Activation.ActivationReceiptVerifier.Verify(legacyReceipt, deviceA, signer.ExportPublicKeyPem()).CanRun);

    var concurrentCode = (await store.CreateCodesAsync(1, "concurrent"))[0].Code;
    var contenders = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(() => store.RedeemAsync(
        concurrentCode, Convert.ToHexString(SHA256.HashData(BitConverter.GetBytes(i)))))));
    AssertEqual(2, contenders.Count(result => result.State == RedeemState.Activated));
    AssertEqual(10, contenders.Count(result => result.State == RedeemState.UsedByAnotherDevice));
    var sameDeviceCode = (await store.CreateCodesAsync(1, "same-device race"))[0].Code;
    var retries = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => store.RedeemAsync(sameDeviceCode, deviceA))));
    AssertEqual(1, retries.Count(result => result.State == RedeemState.Activated));
    AssertEqual(7, retries.Count(result => result.State == RedeemState.AlreadyActivatedOnThisDevice));
    AssertEqual(true, retries.All(result => result.ActivationCount == 1));
    await store.InitializeAsync();
    AssertEqual(RedeemState.UsedByAnotherDevice, (await store.RedeemAsync(generated[0].Code, deviceC)).State);
    await store.RevokeAsync(generated[0].Id);
    AssertEqual(RedeemState.Revoked, (await store.RedeemAsync(generated[0].Code, deviceA)).State);

    // Migrate the exact legacy schema and keep its original device in slot one.
    var legacyPath = Path.Combine(directory, "legacy.db");
    var hmac = RandomNumberGenerator.GetBytes(32);
    var legacyCode = ActivationCode.Generate();
    await using (var database = new SqliteConnection($"Data Source={legacyPath}"))
    {
        await database.OpenAsync();
        await using var command = database.CreateCommand();
        command.CommandText = """
            CREATE TABLE activation_codes (id TEXT PRIMARY KEY, code_hash TEXT NOT NULL UNIQUE,
                prefix TEXT NOT NULL, label TEXT, created_at INTEGER NOT NULL,
                redeemed_at INTEGER, device_hash TEXT, revoked_at INTEGER);
            INSERT INTO activation_codes VALUES ('legacy-id', $hash, 'PLH1-TEST', 'legacy', 100, 101, $device, NULL);
            """;
        command.Parameters.AddWithValue("$hash", Convert.ToHexString(HMACSHA256.HashData(hmac, Encoding.UTF8.GetBytes(legacyCode))));
        command.Parameters.AddWithValue("$device", deviceA.ToLowerInvariant());
        await command.ExecuteNonQueryAsync();
    }
    var migrated = new ActivationStore(legacyPath, hmac);
    await migrated.InitializeAsync();
    await migrated.InitializeAsync();
    var existingDevice = await migrated.RedeemAsync(legacyCode, deviceA);
    AssertEqual(RedeemState.AlreadyActivatedOnThisDevice, existingDevice.State);
    AssertEqual("legacy-id", existingDevice.ActivationId);
    AssertEqual(1, existingDevice.ActivationCount);
    AssertEqual(RedeemState.Activated, (await migrated.RedeemAsync(legacyCode, deviceB)).State);
    AssertEqual(RedeemState.UsedByAnotherDevice, (await migrated.RedeemAsync(legacyCode, deviceC)).State);

    Console.WriteLine("PASS two permanent devices, third rejected, no self-service reset");
    Console.WriteLine("PASS same-device redemption is idempotent");
    Console.WriteLine("PASS activation receipt is ES256 signed");
    Console.WriteLine("PASS storefront activation import is idempotent per order");
    Console.WriteLine("PASS concurrent redemptions cannot exceed two devices or double-charge retries");
    Console.WriteLine("PASS legacy database migration is idempotent and preserves original bindings");
    Console.WriteLine("PASS offline verifier accepts old/new receipts and rejects tampering/wrong device");
    Console.WriteLine("PASS revocation rejects further redemption; no network check is added to offline receipts");
    return 0;
}
finally
{
    SqliteConnection.ClearAllPools();
    Directory.Delete(directory, recursive: true);
}

static void AssertEqual<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }
}
