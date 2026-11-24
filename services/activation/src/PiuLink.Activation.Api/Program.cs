using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using PiuLink.Activation;

var builder = WebApplication.CreateBuilder(args);
builder.Services
    .AddOptions<ActivationOptions>()
    .Bind(builder.Configuration.GetSection(ActivationOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

var configured = builder.Configuration.GetSection(ActivationOptions.SectionName).Get<ActivationOptions>()
    ?? throw new InvalidOperationException("PiuLinkActivation configuration is required.");
var codeHmacKey = DecodeSecret(configured.CodeHmacKey, nameof(configured.CodeHmacKey));
if (Encoding.UTF8.GetByteCount(configured.AdminKey) < 32)
{
    throw new InvalidOperationException("PiuLinkActivation:AdminKey must contain at least 32 UTF-8 bytes.");
}

builder.Services.AddSingleton(new ActivationStore(configured.DatabasePath, codeHmacKey));
builder.Services.AddSingleton(_ => ActivationReceiptSigner.Load(configured.SigningKeyPemPath, configured.KeyId));
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("redeem", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 20,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
});

var app = builder.Build();
app.UseForwardedHeaders();
app.UseRateLimiter();
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
    await next();
});

var store = app.Services.GetRequiredService<ActivationStore>();
await store.InitializeAsync();

app.MapGet("/healthz", () => Results.Ok(new { status = "healthy", service = "piulink-activation", version = "0.2.0", max_host_activations = 2 }));
app.MapGet("/.well-known/piulink-activation-key.pem", (ActivationReceiptSigner signer) =>
    Results.Text(signer.ExportPublicKeyPem(), "application/x-pem-file"));

app.MapPost("/v1/activation/redeem", async (
        RedeemRequest request,
        ActivationStore activationStore,
        ActivationReceiptSigner signer,
        CancellationToken cancellationToken) =>
    {
        if (!string.Equals(request.Product, "piulink-host", StringComparison.Ordinal) ||
            request.DeviceHash is null)
        {
            return Results.BadRequest(new { code = "invalid_request", message = "Product or device hash is invalid." });
        }

        var result = await activationStore.RedeemAsync(request.Code, request.DeviceHash, cancellationToken);
        return result.State switch
        {
            RedeemState.Activated or RedeemState.AlreadyActivatedOnThisDevice => Results.Ok(new
            {
                status = result.State == RedeemState.Activated ? "activated" : "already_activated",
                activation_id = result.ActivationId,
                activation_count = result.ActivationCount,
                max_activations = result.MaxActivations,
                receipt = signer.Sign(result.ActivationId!, request.DeviceHash.ToUpperInvariant(), result.ActivationCount, result.MaxActivations)
            }),
            RedeemState.InvalidCode => Results.NotFound(new { code = "invalid_code", message = "Activation code was not found." }),
            RedeemState.UsedByAnotherDevice => Results.Conflict(new { code = "activation_limit_reached", message = "This license has reached its 2-device activation limit. Activations are permanent. Purchase another PiuLink license to activate another host.", activation_count = result.ActivationCount, max_activations = result.MaxActivations }),
            RedeemState.Revoked => Results.Json(new { code = "code_revoked", message = "Activation code was revoked." }, statusCode: StatusCodes.Status410Gone),
            _ => Results.StatusCode(StatusCodes.Status500InternalServerError)
        };
    })
    .RequireRateLimiting("redeem");

app.MapPost("/v1/admin/codes", async (
    CreateCodesRequest request,
    HttpContext context,
    ActivationStore activationStore,
    IOptions<ActivationOptions> options,
    CancellationToken cancellationToken) =>
{
    if (!IsAdmin(context.Request, options.Value.AdminKey))
    {
        return Results.Unauthorized();
    }

    if (request.Count is < 1 or > 100)
    {
        return Results.BadRequest(new { code = "invalid_count", message = "Count must be between 1 and 100." });
    }

    var codes = await activationStore.CreateCodesAsync(request.Count, request.Label, cancellationToken);
    return Results.Ok(new
    {
        warning = "Plaintext activation codes are returned only once. Store them securely.",
        codes
    });
});

app.MapPost("/v1/admin/codes/import", async (
    ImportCodeRequest request,
    HttpContext context,
    ActivationStore activationStore,
    IOptions<ActivationOptions> options,
    CancellationToken cancellationToken) =>
{
    if (!IsAdmin(context.Request, options.Value.AdminKey))
    {
        return Results.Unauthorized();
    }

    var result = await activationStore.ImportCodeAsync(
        request.Code,
        request.ExternalId,
        request.Label,
        cancellationToken);
    return result.State switch
    {
        ImportCodeState.Imported => Results.Created($"/v1/admin/codes/{result.ActivationCode!.Id}", new
        {
            status = "imported",
            activation_id = result.ActivationCode.Id,
            prefix = result.ActivationCode.Prefix
        }),
        ImportCodeState.AlreadyImported => Results.Ok(new
        {
            status = "already_imported",
            activation_id = result.ActivationCode!.Id,
            prefix = result.ActivationCode.Prefix
        }),
        ImportCodeState.InvalidCode => Results.BadRequest(new { code = "invalid_request", message = "Code or external_id is invalid." }),
        ImportCodeState.ExternalIdConflict => Results.Conflict(new { code = "external_id_conflict", message = "external_id already maps to a different code." }),
        _ => Results.StatusCode(StatusCodes.Status500InternalServerError)
    };
});

app.MapPost("/v1/admin/codes/{id}/revoke", async (
    string id,
    HttpContext context,
    ActivationStore activationStore,
    IOptions<ActivationOptions> options,
    CancellationToken cancellationToken) =>
{
    if (!IsAdmin(context.Request, options.Value.AdminKey))
    {
        return Results.Unauthorized();
    }

    return await activationStore.RevokeAsync(id, cancellationToken)
        ? Results.Ok(new { status = "revoked", activation_id = id })
        : Results.NotFound(new { code = "not_found", message = "Activation record was not found or was already revoked." });
});

app.Run();

static byte[] DecodeSecret(string value, string name)
{
    try
    {
        var bytes = Convert.FromBase64String(value);
        if (bytes.Length < 32)
        {
            throw new InvalidOperationException($"PiuLinkActivation:{name} must decode to at least 32 bytes.");
        }

        return bytes;
    }
    catch (FormatException exception)
    {
        throw new InvalidOperationException($"PiuLinkActivation:{name} must be Base64.", exception);
    }
}

static bool IsAdmin(HttpRequest request, string expected)
{
    var submitted = request.Headers["X-PiuLink-Admin-Key"].ToString();
    return CryptographicOperations.FixedTimeEquals(
        SHA256.HashData(Encoding.UTF8.GetBytes(submitted)),
        SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
}

public sealed record RedeemRequest(string? Product, string? Code, string? DeviceHash);

public sealed record CreateCodesRequest(int Count, string? Label);

public sealed record ImportCodeRequest(
    string? Code,
    [property: System.Text.Json.Serialization.JsonPropertyName("external_id")] string? ExternalId,
    string? Label);
