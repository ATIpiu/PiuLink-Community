using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace PiuLink.Activation;

public sealed record ActivationReceiptPayload(
    [property: JsonPropertyName("ver")] int Version,
    [property: JsonPropertyName("jti")] string ActivationId,
    [property: JsonPropertyName("product")] string Product,
    [property: JsonPropertyName("device")] string DeviceHash,
    [property: JsonPropertyName("iat")] long IssuedAt,
    [property: JsonPropertyName("license")] string License,
    [property: JsonPropertyName("activation_count")] int? ActivationCount = null,
    [property: JsonPropertyName("max_activations")] int? MaxActivations = null);

public enum RedeemState
{
    Activated,
    AlreadyActivatedOnThisDevice,
    InvalidCode,
    UsedByAnotherDevice,
    Revoked
}

public sealed record RedeemResult(RedeemState State, string? ActivationId = null,
    int ActivationCount = 0, int MaxActivations = 2);

public sealed record GeneratedActivationCode(string Id, string Code, string Prefix);

public enum ImportCodeState
{
    Imported,
    AlreadyImported,
    InvalidCode,
    ExternalIdConflict
}

public sealed record ImportCodeResult(ImportCodeState State, GeneratedActivationCode? ActivationCode = null);

public static class ActivationCode
{
    private const string Alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";

    public static string Generate()
    {
        Span<byte> random = stackalloc byte[28];
        RandomNumberGenerator.Fill(random);
        Span<char> symbols = stackalloc char[28];
        for (var index = 0; index < symbols.Length; index++)
        {
            symbols[index] = Alphabet[random[index] % Alphabet.Length];
        }

        return $"PLH1-{new string(symbols[..4])}-{new string(symbols[4..8])}-{new string(symbols[8..12])}-{new string(symbols[12..16])}-{new string(symbols[16..20])}-{new string(symbols[20..24])}-{new string(symbols[24..28])}";
    }

    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var compact = new string(value
            .Trim()
            .ToUpperInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());
        if (compact.Length != 32 || !compact.StartsWith("PLH1", StringComparison.Ordinal))
        {
            return null;
        }

        if (compact.Skip(4).Any(character => !Alphabet.Contains(character, StringComparison.Ordinal)))
        {
            return null;
        }

        return string.Join('-', compact[..4], compact[4..8], compact[8..12], compact[12..16],
            compact[16..20], compact[20..24], compact[24..28], compact[28..32]);
    }
}

public sealed class ActivationStore(string databasePath, byte[] codeHmacKey)
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = Path.GetFullPath(databasePath),
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared
    }.ToString();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var path = new SqliteConnectionStringBuilder(_connectionString).DataSource;
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA busy_timeout=5000;
            CREATE TABLE IF NOT EXISTS activation_codes (
                id TEXT PRIMARY KEY,
                code_hash TEXT NOT NULL UNIQUE,
                prefix TEXT NOT NULL,
                label TEXT,
                external_id TEXT,
                created_at INTEGER NOT NULL,
                redeemed_at INTEGER,
                device_hash TEXT,
                revoked_at INTEGER,
                max_activations INTEGER NOT NULL DEFAULT 2 CHECK(max_activations = 2)
            );
            CREATE INDEX IF NOT EXISTS ix_activation_codes_prefix ON activation_codes(prefix);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);

        var hasExternalId = false;
        var hasMaxActivations = false;
        await using (var columns = connection.CreateCommand())
        {
            columns.CommandText = "PRAGMA table_info(activation_codes);";
            await using var reader = await columns.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                hasExternalId |= string.Equals(reader.GetString(1), "external_id", StringComparison.Ordinal);
                hasMaxActivations |= string.Equals(reader.GetString(1), "max_activations", StringComparison.Ordinal);
            }
        }

        if (!hasExternalId)
        {
            await using var migrate = connection.CreateCommand();
            migrate.CommandText = "ALTER TABLE activation_codes ADD COLUMN external_id TEXT;";
            await migrate.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var externalIdIndex = connection.CreateCommand();
        externalIdIndex.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS ux_activation_codes_external_id ON activation_codes(external_id);";
        await externalIdIndex.ExecuteNonQueryAsync(cancellationToken);

        // Upgrade legacy one-device codes without changing their IDs, HMACs or signed receipts.
        await using var migration = connection.CreateCommand();
        migration.CommandText = "BEGIN IMMEDIATE;";
        await migration.ExecuteNonQueryAsync(cancellationToken);
        try
        {
            if (!hasMaxActivations)
            {
                migration.CommandText = "ALTER TABLE activation_codes ADD COLUMN max_activations INTEGER NOT NULL DEFAULT 2 CHECK(max_activations = 2);";
                await migration.ExecuteNonQueryAsync(cancellationToken);
            }
            migration.CommandText = """
                CREATE TABLE IF NOT EXISTS activations (
                    license_id TEXT NOT NULL REFERENCES activation_codes(id),
                    machine_id_hash TEXT NOT NULL,
                    activated_at INTEGER NOT NULL,
                    PRIMARY KEY (license_id, machine_id_hash)
                );
                INSERT OR IGNORE INTO activations (license_id, machine_id_hash, activated_at)
                    SELECT id, UPPER(device_hash), COALESCE(redeemed_at, created_at)
                    FROM activation_codes WHERE device_hash IS NOT NULL;
                COMMIT;
                """;
            await migration.ExecuteNonQueryAsync(cancellationToken);
        }
        catch
        {
            migration.CommandText = "ROLLBACK;";
            await migration.ExecuteNonQueryAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<IReadOnlyList<GeneratedActivationCode>> CreateCodesAsync(
        int count,
        string? label,
        CancellationToken cancellationToken = default)
    {
        if (count is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "Generate between 1 and 100 codes per request.");
        }

        var codes = new List<GeneratedActivationCode>(count);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        while (codes.Count < count)
        {
            var code = ActivationCode.Generate();
            var id = Guid.NewGuid().ToString("N");
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT OR IGNORE INTO activation_codes
                    (id, code_hash, prefix, label, created_at)
                VALUES ($id, $code_hash, $prefix, $label, $created_at);
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$code_hash", HashCode(code));
            command.Parameters.AddWithValue("$prefix", code[..9]);
            command.Parameters.AddWithValue("$label", (object?)NormalizeLabel(label) ?? DBNull.Value);
            command.Parameters.AddWithValue("$created_at", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            if (await command.ExecuteNonQueryAsync(cancellationToken) == 1)
            {
                codes.Add(new GeneratedActivationCode(id, code, code[..9]));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return codes;
    }

    public async Task<ImportCodeResult> ImportCodeAsync(
        string? submittedCode,
        string? externalId,
        string? label,
        CancellationToken cancellationToken = default)
    {
        var code = ActivationCode.Normalize(submittedCode);
        var normalizedExternalId = NormalizeExternalId(externalId);
        if (code is null || normalizedExternalId is null)
        {
            return new ImportCodeResult(ImportCodeState.InvalidCode);
        }

        var codeHash = HashCode(code);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using (var begin = connection.CreateCommand())
        {
            begin.CommandText = "BEGIN IMMEDIATE;";
            await begin.ExecuteNonQueryAsync(cancellationToken);
        }

        try
        {
            await using (var existing = connection.CreateCommand())
            {
                existing.CommandText = "SELECT id, code_hash, prefix FROM activation_codes WHERE external_id = $external_id;";
                existing.Parameters.AddWithValue("$external_id", normalizedExternalId);
                await using var reader = await existing.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    var existingHash = reader.GetString(1);
                    var result = CryptographicOperations.FixedTimeEquals(
                        Encoding.ASCII.GetBytes(existingHash),
                        Encoding.ASCII.GetBytes(codeHash))
                        ? new ImportCodeResult(ImportCodeState.AlreadyImported,
                            new GeneratedActivationCode(reader.GetString(0), code, reader.GetString(2)))
                        : new ImportCodeResult(ImportCodeState.ExternalIdConflict);
                    await CommitAsync(connection, cancellationToken);
                    return result;
                }
            }

            var id = Guid.NewGuid().ToString("N");
            await using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT OR IGNORE INTO activation_codes
                    (id, code_hash, prefix, label, external_id, created_at)
                VALUES ($id, $code_hash, $prefix, $label, $external_id, $created_at);
                """;
            insert.Parameters.AddWithValue("$id", id);
            insert.Parameters.AddWithValue("$code_hash", codeHash);
            insert.Parameters.AddWithValue("$prefix", code[..9]);
            insert.Parameters.AddWithValue("$label", (object?)NormalizeLabel(label) ?? DBNull.Value);
            insert.Parameters.AddWithValue("$external_id", normalizedExternalId);
            insert.Parameters.AddWithValue("$created_at", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            var inserted = await insert.ExecuteNonQueryAsync(cancellationToken);
            await CommitAsync(connection, cancellationToken);
            return inserted == 1
                ? new ImportCodeResult(ImportCodeState.Imported, new GeneratedActivationCode(id, code, code[..9]))
                : new ImportCodeResult(ImportCodeState.ExternalIdConflict);
        }
        catch
        {
            await using var rollback = connection.CreateCommand();
            rollback.CommandText = "ROLLBACK;";
            await rollback.ExecuteNonQueryAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<RedeemResult> RedeemAsync(
        string? submittedCode,
        string deviceHash,
        CancellationToken cancellationToken = default)
    {
        var code = ActivationCode.Normalize(submittedCode);
        if (code is null || !IsDeviceHash(deviceHash))
        {
            return new RedeemResult(RedeemState.InvalidCode);
        }
        deviceHash = deviceHash.ToUpperInvariant();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using (var begin = connection.CreateCommand())
        {
            begin.CommandText = "BEGIN IMMEDIATE;";
            await begin.ExecuteNonQueryAsync(cancellationToken);
        }

        try
        {
            string? id = null;
            var maxActivations = 2;
            long? revokedAt = null;
            await using (var select = connection.CreateCommand())
            {
                select.CommandText = """
                    SELECT id, max_activations, revoked_at
                    FROM activation_codes
                    WHERE code_hash = $code_hash;
                    """;
                select.Parameters.AddWithValue("$code_hash", HashCode(code));
                await using var reader = await select.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    id = reader.GetString(0);
                    maxActivations = reader.GetInt32(1);
                    revokedAt = reader.IsDBNull(2) ? null : reader.GetInt64(2);
                }
            }

            RedeemResult result;
            if (id is null)
            {
                result = new RedeemResult(RedeemState.InvalidCode);
            }
            else if (revokedAt.HasValue)
            {
                result = new RedeemResult(RedeemState.Revoked, id);
            }
            else
            {
                int count;
                bool alreadyActivated;
                await using (var existing = connection.CreateCommand())
                {
                    existing.CommandText = """
                        SELECT COUNT(*), COALESCE(MAX(machine_id_hash = $device), 0)
                        FROM activations WHERE license_id = $id;
                        """;
                    existing.Parameters.AddWithValue("$id", id);
                    existing.Parameters.AddWithValue("$device", deviceHash);
                    await using var reader = await existing.ExecuteReaderAsync(cancellationToken);
                    await reader.ReadAsync(cancellationToken);
                    count = reader.GetInt32(0);
                    alreadyActivated = reader.GetInt32(1) != 0;
                }
                if (alreadyActivated)
                    result = new RedeemResult(RedeemState.AlreadyActivatedOnThisDevice, id, count, maxActivations);
                else if (count >= maxActivations)
                    result = new RedeemResult(RedeemState.UsedByAnotherDevice, id, count, maxActivations);
                else
                {
                    // BEGIN IMMEDIATE serializes the count + insert across concurrent requests/processes.
                    await using var insert = connection.CreateCommand();
                    insert.CommandText = """
                        INSERT INTO activations (license_id, machine_id_hash, activated_at)
                        VALUES ($id, $device, $now);
                        UPDATE activation_codes SET device_hash = $device, redeemed_at = $now
                        WHERE id = $id AND device_hash IS NULL;
                        """;
                    insert.Parameters.AddWithValue("$id", id);
                    insert.Parameters.AddWithValue("$device", deviceHash);
                    insert.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    await insert.ExecuteNonQueryAsync(cancellationToken);
                    result = new RedeemResult(RedeemState.Activated, id, count + 1, maxActivations);
                }
            }

            await using var commit = connection.CreateCommand();
            commit.CommandText = "COMMIT;";
            await commit.ExecuteNonQueryAsync(cancellationToken);
            return result;
        }
        catch
        {
            await using var rollback = connection.CreateCommand();
            rollback.CommandText = "ROLLBACK;";
            await rollback.ExecuteNonQueryAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<bool> RevokeAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE activation_codes
            SET revoked_at = $revoked_at
            WHERE id = $id AND revoked_at IS NULL;
            """;
        command.Parameters.AddWithValue("$revoked_at", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private string HashCode(string code) => Convert.ToHexString(
        HMACSHA256.HashData(codeHmacKey, Encoding.UTF8.GetBytes(code)));

    private static bool IsDeviceHash(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);

    private static string? NormalizeLabel(string? label) =>
        string.IsNullOrWhiteSpace(label) ? null : label.Trim()[..Math.Min(label.Trim().Length, 120)];

    private static string? NormalizeExternalId(string? externalId)
    {
        var normalized = externalId?.Trim();
        return string.IsNullOrEmpty(normalized) || normalized.Length > 160 ? null : normalized;
    }

    private static async Task CommitAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var commit = connection.CreateCommand();
        commit.CommandText = "COMMIT;";
        await commit.ExecuteNonQueryAsync(cancellationToken);
    }
}

public sealed class ActivationReceiptSigner : IDisposable
{
    private readonly ECDsa _key;
    private readonly string _keyId;

    public ActivationReceiptSigner(ECDsa key, string keyId)
    {
        _key = key;
        _keyId = keyId;
    }

    public static ActivationReceiptSigner Load(string pemPath, string keyId)
    {
        var key = ECDsa.Create();
        key.ImportFromPem(File.ReadAllText(pemPath));
        return new ActivationReceiptSigner(key, keyId);
    }

    public string Sign(string activationId, string deviceHash, int? activationCount = null, int? maxActivations = null)
    {
        var header = JsonSerializer.SerializeToUtf8Bytes(new { alg = "ES256", typ = "PIULINK-ACTIVATION", kid = _keyId });
        var payload = JsonSerializer.SerializeToUtf8Bytes(new ActivationReceiptPayload(
            1,
            activationId,
            "piulink-host",
            deviceHash,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            "perpetual", activationCount, maxActivations));
        var signingInput = $"{Base64Url(header)}.{Base64Url(payload)}";
        byte[] signature;
        lock (_key)
        {
            signature = _key.SignData(
                Encoding.ASCII.GetBytes(signingInput),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        return $"{signingInput}.{Base64Url(signature)}";
    }

    public string ExportPublicKeyPem() => _key.ExportSubjectPublicKeyInfoPem();

    public void Dispose() => _key.Dispose();

    private static string Base64Url(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
