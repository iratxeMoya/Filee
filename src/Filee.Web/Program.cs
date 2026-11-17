// Entry point of filee-web: Filee's conversions behind an HTTP API (/api, see Api/FileeApi.cs) and the page in
// wwwroot that uses it. Configured by environment variables (WebOptions); listens where ASPNETCORE_URLS / _HTTP_PORTS say.

using System.Security.Cryptography;
using System.Text;
using Filee.Web;
using Filee.Web.Api;
using Filee.Web.Hosting;
using Filee.Web.Jobs;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

// The options come from the services (tests put their own there), everything else reads them from there too.
builder.Services.AddSingleton(_ => WebOptions.FromEnvironment());
builder.Services.AddOptions<KestrelServerOptions>().Configure<WebOptions>((kestrel, options) =>
{
    kestrel.AddServerHeader = false;
    kestrel.Limits.MaxRequestBodySize = options.MaxUploadBytes;
});
builder.Services.AddOptions<FormOptions>().Configure<WebOptions>((form, options) =>
{
    form.MultipartBodyLengthLimit = options.MaxUploadBytes;
    form.ValueCountLimit = options.MaxFilesPerJob + 16;
});
builder.Services.AddSingleton(services =>
    WebConversionHost.Create(services.GetRequiredService<WebOptions>().DataDirectory, services.GetRequiredService<ILoggerFactory>()));
builder.Services.AddSingleton<JobStore>();
builder.Services.AddHostedService<JobCleanupService>();

var app = builder.Build();
var options = app.Services.GetRequiredService<WebOptions>();
app.Services.GetRequiredService<ILogger<Program>>().LogInformation(
    "Filee data folder: {Data}; jobs: {Jobs}; upload limit: {Limit} MB; access token: {Token}",
    options.DataDirectory, options.JobsDirectory, options.MaxUploadBytes / (1024 * 1024), options.Token is null ? "off" : "on");

// Check every engine once at start (some run a program to read its version), not on the first request.
_ = app.Services.GetRequiredService<WebConversionHost>().Catalog.CheckInBackgroundAsync();

app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers.XContentTypeOptions = "nosniff";
    headers.XFrameOptions = "DENY";
    headers["Referrer-Policy"] = "no-referrer";
    headers.ContentSecurityPolicy = "default-src 'self'; img-src 'self' data: blob:; style-src 'self'; script-src 'self'; "
        + "connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
    await next();
});

if (options.Token is { } token)
{
    var expected = Encoding.UTF8.GetBytes(token);
    app.Use(async (context, next) =>
    {
        // The health check stays open for Docker; everything else under /api needs the token.
        if (context.Request.Path.StartsWithSegments("/api") && !context.Request.Path.StartsWithSegments("/api/health"))
        {
            var given = context.Request.Headers.Authorization.ToString() is { } header && header.StartsWith("Bearer ", StringComparison.Ordinal)
                ? header["Bearer ".Length..]
                : context.Request.Cookies["filee_web_token"];
            if (given is null || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), expected))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new ErrorDto("This server needs an access token."));
                return;
            }
        }
        await next();
    });
}

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFileeApi();

app.Run();

/// <summary>The program, named for the tests (WebApplicationFactory).</summary>
public partial class Program;
