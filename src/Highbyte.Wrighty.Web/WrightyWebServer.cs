using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Web.Markdown;
using Highbyte.Wrighty.Workers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Highbyte.Wrighty.Web;

public sealed class WrightyWebServer(
    ITrackerConfigLoader configLoader,
    TrackerService tracker,
    IBrowserLauncher browserLauncher,
    string workingDirectory,
    IWorkspaceInventory workspaceInventory) : IWrightyWebServer
{
    public const string TokenHeader = "X-Wrighty-Token";
    private const long MaximumRequestBodySize = 1_100_000;

    public async Task RunAsync(WebServerOptions options, TextWriter output, CancellationToken cancellationToken)
    {
        var config = await configLoader.LoadAsync(workingDirectory, cancellationToken);
        EnsureSupportedBackend(config);
        await tracker.InitializeAsync(config, checkOnly: true, cancellationToken);
        var state = new WebApplicationState(config, LaunchToken());
        var diagnostics = new WebDiagnostics(output);
        var builder = CreateBuilder(options, state, diagnostics);
        await using var application = builder.Build();
        ConfigureApplication(application, state, config, diagnostics);

        await application.StartAsync(cancellationToken);
        var origin = ListeningUrl(application);
        state.Port = new Uri(origin).Port;
        var launchUrl = $"{origin}/#token={Uri.EscapeDataString(state.Token)}";
        await ReportStartup(output, origin, launchUrl, options.OpenBrowser);
        await application.WaitForShutdownAsync(cancellationToken);
    }

    private static void EnsureSupportedBackend(TrackerConfig config)
    {
        if (!string.Equals(config.Backend, "local-markdown", StringComparison.OrdinalIgnoreCase))
        {
            throw new TrackerException(
                "WEB_BACKEND_UNSUPPORTED",
                "The web dashboard currently supports only the local-markdown backend.",
                2);
        }
    }

    private WebApplicationBuilder CreateBuilder(
        WebServerOptions options,
        WebApplicationState state,
        WebDiagnostics diagnostics)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, options.Port));
        builder.Services.AddSingleton(state);
        builder.Services.AddSingleton(tracker);
        builder.Services.AddSingleton(workspaceInventory);
        builder.Services.AddSingleton<MarkdownRenderer>();
        builder.Services.AddRazorPages().AddApplicationPart(typeof(WrightyWebServer).Assembly);
        return builder;
    }

    private static void ConfigureApplication(
        WebApplication application,
        WebApplicationState state,
        TrackerConfig config,
        WebDiagnostics diagnostics)
    {
        application.Use((context, next) =>
            HandleRequest(context, next, state, config, diagnostics));
        application.MapGet("/assets/{name}", AssetResponse);
        application.MapGet("/web/health", () => Results.Json(new { status = "ok" }));
        application.MapRazorPages();
    }

    private static async Task HandleRequest(
        HttpContext context,
        Func<Task> next,
        WebApplicationState state,
        TrackerConfig config,
        WebDiagnostics diagnostics)
    {
        ApplySecurityHeaders(context.Response);
        if (!ValidHost(context.Request, state.Port))
        {
            await WriteProblem(context, 400, "HOST_INVALID", "The request Host is not the Wrighty loopback endpoint.");
            return;
        }

        if (IsProtectedRequest(context.Request) && !ValidToken(context.Request, state.Token))
        {
            await WriteProblem(context, 401, "AUTH_REQUIRED", "The launch token is missing or invalid.");
            return;
        }

        if (IsMutation(context.Request) && !await ValidateMutation(context, state.Origin))
        {
            return;
        }

        try
        {
            await next();
        }
        catch (TrackerException exception) when (!context.Response.HasStarted)
        {
            WebDiagnostics.RetainFailure(context, exception.Code, exception);
            await WriteProblem(context, exception.ExitCode == 2 ? 400 : 500, exception.Code, SafeMessage(exception.Message, config));
        }
        catch (Exception exception) when (!context.Response.HasStarted)
        {
            var correlationId = Guid.NewGuid().ToString("N");
            WebDiagnostics.RetainFailure(context, $"WEB_UNEXPECTED:{correlationId}", exception);
            await WriteProblem(context, 500, "WEB_UNEXPECTED", $"An unexpected error occurred. Correlation ID: {correlationId}");
        }

        await diagnostics.LogFailureAsync(context);
    }

    private static async Task<bool> ValidateMutation(HttpContext context, string origin)
    {
        if (!string.Equals(context.Request.Headers.Origin, origin, StringComparison.Ordinal))
        {
            await WriteProblem(context, 403, "ORIGIN_INVALID", "Mutation requests require the exact Wrighty origin.");
            return false;
        }

        if (!context.Request.HasFormContentType)
        {
            await WriteProblem(context, 415, "CONTENT_TYPE_INVALID", "Mutation requests require form-encoded content.");
            return false;
        }

        context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize = MaximumRequestBodySize;
        return true;
    }

    private async Task ReportStartup(
        TextWriter output,
        string origin,
        string launchUrl,
        bool openBrowser)
    {
        await output.WriteLineAsync($"Wrighty web server listening on {origin}");
        await output.WriteLineAsync($"Open {launchUrl}");
        await output.WriteLineAsync("Press Ctrl+C to stop.");

        if (!openBrowser)
        {
            return;
        }

        try { browserLauncher.Open(launchUrl); }
        catch (Exception exception)
        {
            await output.WriteLineAsync($"warning: Could not open the default browser: {exception.Message}");
        }
    }

    private static bool IsProtectedRequest(HttpRequest request) =>
        request.Query.ContainsKey("handler") || request.Path.StartsWithSegments("/web/fragments");

    private static bool IsMutation(HttpRequest request) =>
        !HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method) && !HttpMethods.IsOptions(request.Method);

    private static bool ValidToken(HttpRequest request, string expected)
    {
        var supplied = request.Headers[TokenHeader].ToString();
        if (supplied.Length != expected.Length) return false;
        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(supplied),
            System.Text.Encoding.UTF8.GetBytes(expected));
    }

    private static bool ValidHost(HttpRequest request, int port)
    {
        if (port == 0) return true;
        return string.Equals(request.Host.Host, "127.0.0.1", StringComparison.Ordinal) && request.Host.Port == port;
    }

    private static void ApplySecurityHeaders(HttpResponse response)
    {
        response.Headers.ContentSecurityPolicy = "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers.CacheControl = "no-store";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers.XFrameOptions = "DENY";
    }

    private static async Task WriteProblem(HttpContext context, int status, string code, string detail)
    {
        context.Response.StatusCode = status;
        if (string.Equals(context.Request.Headers["HX-Request"], "true", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync($"<div class=\"error\" role=\"alert\"><strong>{WebUtility.HtmlEncode(code)}</strong> {WebUtility.HtmlEncode(detail)}</div>");
            return;
        }

        await Results.Problem(statusCode: status, title: code, detail: detail).ExecuteAsync(context);
    }

    private static IResult AssetResponse(string name)
    {
        var asset = name switch
        {
            "wrighty.css" => ("Highbyte.Wrighty.Web.Assets.wrighty.css", "text/css; charset=utf-8"),
            "app.js" => ("Highbyte.Wrighty.Web.Assets.app.js", "text/javascript; charset=utf-8"),
            "htmx.js" => ("Highbyte.Wrighty.Web.Assets.vendor.htmx-2.0.9.min.js", "text/javascript; charset=utf-8"),
            "highlight-yaml.js" => ("Highbyte.Wrighty.Web.Assets.vendor.highlight-yaml-11.11.1.min.js", "text/javascript; charset=utf-8"),
            _ => default
        };
        if (asset.Item1 is null) return Results.NotFound();
        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(asset.Item1);
        return stream is null ? Results.NotFound() : Results.Stream(stream, asset.Item2);
    }

    private static string LaunchToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string SafeMessage(string message, TrackerConfig config)
    {
        if (config.SourcePath is not { } sourcePath) return message;
        var root = Path.GetDirectoryName(Path.GetFullPath(sourcePath));
        return string.IsNullOrEmpty(root)
            ? message
            : message.Replace(root, "<tracker>", StringComparison.Ordinal);
    }

    private static string ListeningUrl(WebApplication application)
    {
        var addresses = application.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses;
        return addresses?.SingleOrDefault()?.Replace("localhost", "127.0.0.1", StringComparison.OrdinalIgnoreCase)
            ?? throw new InvalidOperationException("The web server did not report a listening address.");
    }
}
