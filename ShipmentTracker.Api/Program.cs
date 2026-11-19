using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Options;
using ShipmentTracker.Api.Endpoints;
using ShipmentTracker.Api.Options;
using ShipmentTracker.Api.Services;
using ShipmentTracker.Api.Services.Auth;
using ShipmentTracker.Api.Services.Nas;

// Helper for creating password hashes:   dotnet run -- hash-password "MyPassword"
if (args.Length == 2 && args[0] == "hash-password")
{
    Console.WriteLine(PasswordHasher.Hash(args[1]));
    return;
}

var builder = WebApplication.CreateBuilder(args);

// NAS settings = appsettings.json + the changes saved from the Settings page (data/settings.json)
builder.Services.AddSingleton<NasSettings>();
builder.Services.AddSingleton<IOptionsMonitor<NasOptions>>(sp => sp.GetRequiredService<NasSettings>());
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.SectionName));

builder.Services.AddSingleton<INasConnector, NasConnector>();
builder.Services.AddSingleton<ShipmentRepositories>();
builder.Services.AddHostedService<NasStartupCheck>();
builder.Services.AddHostedService<CacheWarmupService>();

builder.Services.AddSingleton<JwtTokenService>();
builder.Services.AddSingleton<UserStore>();
builder.Services.AddAuthentication(BearerAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, BearerAuthenticationHandler>(BearerAuthenticationHandler.SchemeName, _ => { });
builder.Services.AddAuthorization();

builder.Services.AddResponseCompression(o =>
{
    o.EnableForHttps = true;
    o.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(["application/json"]);
});

var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
{
    if (origins.Contains("*")) p.AllowAnyOrigin();
    else p.WithOrigins(origins);
    p.AllowAnyHeader().AllowAnyMethod().WithExposedHeaders("ETag");
}));

var app = builder.Build();

app.UseResponseCompression();
app.UseCors();

// If you copy the built Angular app into wwwroot, this API serves the website too (one port, no CORS needed).
if (app.Environment.WebRootPath is { } webRoot && File.Exists(Path.Combine(webRoot, "index.html")))
{
    app.UseDefaultFiles();
    app.UseStaticFiles();
}

app.UseAuthentication();
app.UseAuthorization();

var api = app.MapGroup("/api");
api.MapAuthEndpoints();
api.MapShipmentEndpoints();
api.MapSettingsEndpoints();
api.MapFallback(() => Results.Json(new { message = "Not found." }, statusCode: 404));

if (app.Environment.WebRootPath is { } root && File.Exists(Path.Combine(root, "index.html")))
    app.MapFallbackToFile("index.html");   // Angular routes like /dashboard

app.Run();
