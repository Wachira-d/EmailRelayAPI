using System.Text.Json;
using System.Text.Json.Serialization;
using EmailRelayAPI.Data;
using EmailRelayAPI.Middleware;
using EmailRelayAPI.Models;
using EmailRelayAPI.Options;
using EmailRelayAPI.Security;
using EmailRelayAPI.Services;
using EmailRelayAPI.Services.Transports;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;

// ---------------------------------------------------------------------------
// CLI helper:  dotnet run -- hash-password "your-password"
// ---------------------------------------------------------------------------
if (args.Length >= 2 && args[0].Equals("hash-password", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine(PasswordHasher.HashToString(args[1]));
    return;
}

var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;

// ---------------------------------------------------------------------------
// Options
// ---------------------------------------------------------------------------
builder.Services.Configure<DatabaseOptions>(configuration.GetSection(DatabaseOptions.SectionName));
builder.Services.Configure<SecurityOptions>(configuration.GetSection(SecurityOptions.SectionName));
builder.Services.Configure<AnomalyOptions>(configuration.GetSection(AnomalyOptions.SectionName));
builder.Services.Configure<DeliveryOptions>(configuration.GetSection(DeliveryOptions.SectionName));
builder.Services.Configure<RetentionOptions>(configuration.GetSection(RetentionOptions.SectionName));
builder.Services.Configure<AuthenApiOptions>(configuration.GetSection(AuthenApiOptions.SectionName));
builder.Services.Configure<AuditOptions>(configuration.GetSection(AuditOptions.SectionName));
builder.Services.Configure<AlertOptions>(configuration.GetSection(AlertOptions.SectionName));
builder.Services.Configure<ApiKeyOptions>(configuration.GetSection(ApiKeyOptions.SectionName));

var securityOptions = configuration.GetSection(SecurityOptions.SectionName).Get<SecurityOptions>() ?? new SecurityOptions();
var authenOptions = configuration.GetSection(AuthenApiOptions.SectionName).Get<AuthenApiOptions>() ?? new AuthenApiOptions();

// ---------------------------------------------------------------------------
// Infrastructure
// ---------------------------------------------------------------------------
builder.Services.AddRelayDatabase(configuration);
builder.Services.AddMemoryCache();

var keyRingPath = Path.GetFullPath(configuration["DataProtection:KeyPath"] ?? "data/keys");
Directory.CreateDirectory(keyRingPath);
builder.Services.AddDataProtection()
    .SetApplicationName("EmailRelayAPI")
    .PersistKeysToFileSystem(new DirectoryInfo(keyRingPath));

builder.Services.AddHttpClient(HttpApiEmailTransport.HttpClientName);
builder.Services.AddHttpClient(WebhookNotifier.HttpClientName);
builder.Services.AddHttpClient(AlertService.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddHttpClient(AuthenApiClient.HttpClientName);

builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = securityOptions.MaxRequestBodyBytes);
builder.Services.Configure<IISServerOptions>(o => o.MaxRequestBodySize = securityOptions.MaxRequestBodyBytes);
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = securityOptions.MaxRequestBodyBytes);

builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownNetworks.Clear();
    o.KnownProxies.Clear();
    foreach (var proxy in securityOptions.KnownProxies)
    {
        if (!IpUtils.TryParseCidr(proxy, out var net, out var prefix)) continue;
        var max = net.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
        if (prefix == max) o.KnownProxies.Add(net);
        else o.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(net, prefix));
    }
});

// ---------------------------------------------------------------------------
// Application services
// ---------------------------------------------------------------------------
builder.Services.AddSingleton<AuditService>();
builder.Services.AddSingleton<IAuditService>(sp => sp.GetRequiredService<AuditService>());
builder.Services.AddHostedService<LogWriterService>();

builder.Services.AddSingleton<ISecretProtector, SecretProtector>();
builder.Services.AddSingleton<IApiKeyService, ApiKeyService>();
builder.Services.AddSingleton<IRateLimitService, RateLimitService>();
builder.Services.AddSingleton<IDailyQuotaService, DailyQuotaService>();
builder.Services.AddSingleton<BlockListService>();
builder.Services.AddSingleton<IBlockListService>(sp => sp.GetRequiredService<BlockListService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<BlockListService>());
builder.Services.AddSingleton<IAnomalyDetector, AnomalyDetector>();
builder.Services.AddSingleton<IRequestSignatureVerifier, RequestSignatureVerifier>();
builder.Services.AddSingleton<IAlertService, AlertService>();
builder.Services.AddSingleton<ITemplateRenderer, TemplateRenderer>();

builder.Services.AddSingleton<IDeliveryQueue, DeliveryQueue>();
builder.Services.AddSingleton<IEmailQueueService, EmailQueueService>();
builder.Services.AddSingleton<IWebhookNotifier, WebhookNotifier>();
builder.Services.AddSingleton<IEmailTransport, SmtpEmailTransport>();
builder.Services.AddSingleton<IEmailTransport, HttpApiEmailTransport>();
builder.Services.AddSingleton<IEmailTransportResolver, EmailTransportResolver>();
builder.Services.AddHostedService<EmailDispatcherService>();

builder.Services.AddSingleton<RetentionService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RetentionService>());
builder.Services.AddSingleton<KeyRotationService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<KeyRotationService>());

builder.Services.AddSingleton<IAuthenApiClient, AuthenApiClient>();
builder.Services.AddSingleton<IAdminAuthService, AdminAuthService>();
builder.Services.AddScoped<IStatsService, StatsService>();

// ---------------------------------------------------------------------------
// Authentication / authorization
// ---------------------------------------------------------------------------
builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = RelayClaims.ApiKeyScheme;
        options.DefaultChallengeScheme = RelayClaims.ApiKeyScheme;
    })
    .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(RelayClaims.ApiKeyScheme, _ => { })
    .AddCookie(RelayClaims.AdminCookieScheme, o =>
    {
        o.Cookie.Name = "EmailRelay.Admin";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.Cookie.Path = "/";
        o.ExpireTimeSpan = TimeSpan.FromMinutes(Math.Max(5, authenOptions.SessionTimeoutMinutes));
        o.SlidingExpiration = true;
        o.Events = new CookieAuthenticationEvents
        {
            OnRedirectToLogin = ctx =>
            {
                if (ctx.Request.Path.StartsWithSegments("/api"))
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    ctx.Response.ContentType = "application/json";
                    return ctx.Response.WriteAsync(JsonSerializer.Serialize(ApiResponse.Fail("Sign in required"), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                }
                ctx.Response.Redirect("/admin/");
                return Task.CompletedTask;
            },
            OnRedirectToAccessDenied = ctx =>
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                ctx.Response.ContentType = "application/json";
                return ctx.Response.WriteAsync(JsonSerializer.Serialize(ApiResponse.Fail("Insufficient permissions"), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            }
        };
    });

builder.Services.AddAuthorization(o =>
{
    o.AddPolicy(RelayClaims.PolicySend, p => p.RequireAuthenticatedUser()
        .RequireAssertion(ctx => ctx.User.HasClaim(RelayClaims.Scope, RelayClaims.ScopeSend) || ctx.User.HasClaim(RelayClaims.Scope, RelayClaims.ScopeAdmin)));
    o.AddPolicy(RelayClaims.PolicyRead, p => p.RequireAuthenticatedUser()
        .RequireAssertion(ctx => ctx.User.HasClaim(RelayClaims.Scope, RelayClaims.ScopeRead) || ctx.User.HasClaim(RelayClaims.Scope, RelayClaims.ScopeSend) || ctx.User.HasClaim(RelayClaims.Scope, RelayClaims.ScopeAdmin)));
    o.AddPolicy(RelayClaims.PolicyAdmin, p => p.RequireAuthenticatedUser().RequireRole(RelayClaims.RoleAdmin));
    o.AddPolicy(RelayClaims.PolicyViewer, p => p.RequireAuthenticatedUser().RequireRole(RelayClaims.RoleAdmin, RelayClaims.RoleViewer));
});

// ---------------------------------------------------------------------------
// MVC + Swagger
// ---------------------------------------------------------------------------
builder.Services.AddControllers()
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        o.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
    });

builder.Services.Configure<ApiBehaviorOptions>(o =>
{
    o.InvalidModelStateResponseFactory = ctx =>
    {
        var errors = ctx.ModelState
            .Where(kv => kv.Value?.Errors.Count > 0)
            .SelectMany(kv => kv.Value!.Errors.Select(e => string.IsNullOrEmpty(kv.Key) ? e.ErrorMessage : $"{kv.Key}: {(string.IsNullOrEmpty(e.ErrorMessage) ? "invalid value" : e.ErrorMessage)}"))
            .ToList();
        return new BadRequestObjectResult(ApiResponse.Fail("Invalid request", string.Join("; ", errors)));
    };
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Email Relay API",
        Version = "v1",
        Description = "Submit email through the relay. Authenticate with your client API key in the X-API-Key header (or Authorization: Bearer)."
    });
    c.SwaggerDoc("admin", new OpenApiInfo
    {
        Title = "Email Relay API - Administration",
        Version = "admin",
        Description = "Console API. Authenticate with the admin cookie (sign in at /admin/) or an API key with the 'admin' scope."
    });
    c.DocInclusionPredicate((doc, api) =>
    {
        var isAdmin = api.GroupName == "admin";
        return doc == "admin" ? isAdmin : !isAdmin;
    });
    c.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.ApiKey,
        In = ParameterLocation.Header,
        Name = securityOptions.ApiKeyHeaderName,
        Description = "Client API key"
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "ApiKey" } },
            Array.Empty<string>()
        }
    });
    var xml = Path.Combine(AppContext.BaseDirectory, "EmailRelayAPI.xml");
    if (File.Exists(xml)) c.IncludeXmlComments(xml, includeControllerXmlComments: true);
});

var app = builder.Build();

// ---------------------------------------------------------------------------
// Startup
// ---------------------------------------------------------------------------
await DbInitializer.InitializeAsync(app.Services);

var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
if (string.IsNullOrWhiteSpace(authenOptions.BaseUrl) && !authenOptions.LocalAdmin.Enabled)
{
    startupLogger.LogWarning("Neither AuthenApi:BaseUrl nor AuthenApi:LocalAdmin is configured - nobody can sign in to the admin console.");
}
if (!string.IsNullOrWhiteSpace(authenOptions.BaseUrl) && authenOptions.AdminGroups.Count == 0)
{
    startupLogger.LogWarning("AuthenApi:AdminGroups is empty - every user that authenticates through AuthenAPI gets the Admin role.");
}
startupLogger.LogInformation("EmailRelayAPI {Version} starting ({Env})", AppInfo.Version, app.Environment.EnvironmentName);

// ---------------------------------------------------------------------------
// Pipeline
// ---------------------------------------------------------------------------
if (securityOptions.KnownProxies.Count > 0)
{
    app.UseForwardedHeaders();
}

app.UseSecurityHeaders();

app.UseExceptionHandler(errorApp => errorApp.Run(async ctx =>
{
    var feature = ctx.Features.Get<IExceptionHandlerFeature>();
    var logger = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("UnhandledException");
    logger.LogError(feature?.Error, "Unhandled exception for {Method} {Path}", ctx.Request.Method, ctx.Request.Path);
    ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
    ctx.Response.ContentType = "application/json";
    await ctx.Response.WriteAsync(JsonSerializer.Serialize(
        ApiResponse.Fail("An unexpected error occurred", $"traceId={ctx.GetTraceId()}"),
        new JsonSerializerOptions(JsonSerializerDefaults.Web)));
}));

if (securityOptions.RequireHttps && !app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "Email Relay API v1");
    c.SwaggerEndpoint("/swagger/admin/swagger.json", "Administration API");
    c.RoutePrefix = "swagger";
    c.DocumentTitle = "Email Relay API";
});

app.UseDefaultFiles();
app.UseStaticFiles();

// Some IIS/proxy setups reject PUT and DELETE (405); the console sends POST + X-HTTP-Method-Override instead.
app.UseHttpMethodOverride(new HttpMethodOverrideOptions { FormFieldName = "_method" });

app.UseRouting();

app.UseIpFilter();
app.UseRequestLogging();
app.UseAuthentication();
app.UseClientRateLimit();
app.UseAuthorization();

app.MapControllers();

app.MapGet("/health", () => Results.Ok(new { status = "healthy", version = AppInfo.Version, timestamp = DateTime.UtcNow }))
    .AllowAnonymous()
    .ExcludeFromDescription();

app.MapGet("/health/ready", async (AppDbContext db, IDeliveryQueue queue) =>
{
    try
    {
        await db.Database.ExecuteSqlRawAsync("SELECT 1");
        return Results.Ok(new { status = "ready", queueInFlight = queue.InFlightCount, queuePending = queue.PendingCount, timestamp = DateTime.UtcNow });
    }
    catch (Exception ex)
    {
        return Results.Json(new { status = "unavailable", error = ex.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}).AllowAnonymous().ExcludeFromDescription();

app.MapGet("/", () => Results.Redirect("/admin/")).ExcludeFromDescription();

app.Run();
