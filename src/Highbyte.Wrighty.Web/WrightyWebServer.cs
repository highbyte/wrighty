using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Collections.Frozen;
using Highbyte.Wrighty.ApprovedContext;
using Highbyte.Wrighty.Caching;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Storage;
using Highbyte.Wrighty.Web.Markdown;
using Highbyte.Wrighty.Workers;
using Highbyte.Wrighty.Processes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Highbyte.Wrighty.Web;

public sealed record WrightyWebServerDependencies(
    IWorkspaceInventory WorkspaceInventory,
    IProviderCapacityStore? ProviderCapacityStore = null,
    IProviderCapacityProbeService? ProviderCapacityProbeService = null,
    IEnumerable<IAgentAdapter>? AgentAdapters = null,
    IAgentRuntimeCatalog? AgentRuntimeCatalog = null,
    ILocalAgentSessionLauncher? LocalAgentSessionLauncher = null,
    IRepositoryConfigurationService? RepositoryConfiguration = null,
    IWorkerInstanceRegistry? WorkerInstanceRegistry = null,
    IContextApprovalService? ContextApproval = null,
    Highbyte.Wrighty.Settings.IUserConfigurationService? UserConfiguration = null,
    Workers.AgentModelDiscoveries? ModelDiscoveries = null,
    StorageLocationCatalog? StorageLocations = null,
    GitHubProjectUrlResolver? GitHubProjectUrls = null,
    WorkerService? WorkerService = null,
    ILocalHostNameProvider? LocalHostNameProvider = null,
    AgentRegistry? AgentRegistry = null,
    IWebSkillMaintenance? SkillMaintenance = null);

public sealed record WebAgentSessionServices(
    IWorkspaceInventory WorkspaceInventory,
    IReadOnlyDictionary<string, IAgentAdapter> AdaptersByName,
    IReadOnlyList<AgentDescriptor> AgentDescriptors,
    IAgentRuntimeCatalog RuntimeCatalog,
    ILocalAgentSessionLauncher Launcher);

public sealed record WebOperationsServices(
    IRepositoryConfigurationService? RepositoryConfiguration,
    IWorkerInstanceRegistry WorkerInstances,
    WebHostedWorkerSupervisor HostedWorker,
    IContextApprovalService? ContextApproval,
    // Optional like its repository sibling: a build without it renders the console unchanged,
    // minus the machine-local panel.
    Highbyte.Wrighty.Settings.IUserConfigurationService? UserConfiguration = null,
    Workers.AgentModelDiscoveries? ModelDiscoveries = null,
    StorageLocationCatalog? StorageLocations = null,
    GitHubProjectUrlResolver? GitHubProjectUrls = null,
    IWebSkillMaintenance? SkillMaintenance = null);

public sealed class WrightyWebServer(
    ITrackerConfigLoader configLoader,
    TrackerService tracker,
    IBrowserLauncher browserLauncher,
    string workingDirectory,
    WrightyWebServerDependencies dependencies) : IWrightyWebServer
{
    public const string TokenHeader = "X-Wrighty-Token";
    private const string JavaScriptContentType = "text/javascript; charset=utf-8";
    private const long MaximumRequestBodySize = 1_100_000;

    public async Task RunAsync(
        WebServerOptions options,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var authenticationOptions = WebAuthenticationOptionsResolver.Resolve(options);
        var endpoint = WebEndpointOptionsResolver.Resolve(options);
        var config = await configLoader.LoadAsync(workingDirectory, cancellationToken);
        if (WebSurfaceCapabilities.Resolve(config).LocalBoard)
            await tracker.InitializeAsync(config, checkOnly: true, cancellationToken);
        var activeConfigurationRevision = config.SourceRevision ??
            (config.SourcePath is { } sourcePath && File.Exists(sourcePath)
                ? await RepositoryConfigurationService.RevisionAsync(
                    sourcePath,
                    cancellationToken)
                : null);
        var authentication = new WebTokenProvider().Resolve(
            authenticationOptions,
            config,
            workingDirectory);
        var state = new WebApplicationState(
            config,
            authentication.Token,
            workingDirectory,
            authentication.TokenRequired,
            activeConfigurationRevision,
            (dependencies.LocalHostNameProvider ?? SystemLocalHostNameProvider.Instance)
                .GetHostName());
        var hostedWorker = new WebHostedWorkerSupervisor(
            dependencies.WorkerService,
            dependencies.WorkerInstanceRegistry ?? NoOpWorkerInstanceRegistry.Instance,
            state);
        var diagnostics = new WebDiagnostics(output);
        var builder = CreateBuilder(endpoint, state, hostedWorker, diagnostics);
        await using var application = builder.Build();
        ConfigureApplication(application, state, diagnostics);

        await application.StartAsync(cancellationToken);
        var origin = ListeningUrl(application, endpoint);
        state.ConfigureEndpoint(
            endpoint.BindAddress,
            new Uri(origin).Port,
            endpoint.AllowedHosts,
            endpoint.PublicUrl);
        var launchOrigin = endpoint.PublicUrl?.GetLeftPart(UriPartial.Authority) ?? origin;
        var launchUrl = authentication.Token is { } token
            ? $"{launchOrigin}/#token={Uri.EscapeDataString(token)}"
            : $"{launchOrigin}/";
        if (AccessWarning(endpoint, authentication) is { } warning)
        {
            await error.WriteLineAsync(warning);
        }
        await ReportStartup(output, origin, launchUrl, options.OpenBrowser);
        try
        {
            await application.WaitForShutdownAsync(cancellationToken);
        }
        finally
        {
            await hostedWorker.StopForHostShutdownAsync(TimeSpan.FromSeconds(15));
        }
    }

    private WebApplicationBuilder CreateBuilder(
        WebEndpointOptions endpoint,
        WebApplicationState state,
        WebHostedWorkerSupervisor hostedWorker,
        WebDiagnostics diagnostics)
    {
        // Wrighty loads its own tracker configuration and has no appsettings.json to watch.
        // Disabling the default reload watchers also prevents WebApplication.CreateBuilder from
        // blocking indefinitely when a macOS sandbox denies the file-watcher registration.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = ["--hostBuilder:reloadConfigOnChange=false"]
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            if (!endpoint.IsLoopback)
            {
                kestrel.Listen(endpoint.BindAddress, endpoint.Port);
                return;
            }

            kestrel.Listen(IPAddress.Loopback, endpoint.Port);
            if (Socket.OSSupportsIPv6)
            {
                kestrel.Listen(IPAddress.IPv6Loopback, endpoint.Port);
            }
        });
        builder.Services.AddSingleton(state);
        builder.Services.AddSingleton(hostedWorker);
        builder.Services.AddSingleton(tracker);
        builder.Services.AddSingleton(dependencies.WorkspaceInventory);
        builder.Services.AddSingleton(
            dependencies.ProviderCapacityStore ?? NoOpProviderCapacityStore.Instance);
        builder.Services.AddSingleton(
            dependencies.ProviderCapacityProbeService ??
            UnavailableProviderCapacityProbeService.Instance);
        var executableResolver = new PathExecutableResolver();
        var registry = dependencies.AgentRegistry ??
            (dependencies.AgentAdapters is null
                ? BuiltInAgentRegistry.Create(executableResolver)
                : null);
        var registeredAdapters = (registry?.ExecutionAdapters ?? dependencies.AgentAdapters!)
            .ToArray();
        foreach (var adapter in registeredAdapters)
            builder.Services.AddSingleton<IAgentAdapter>(adapter);
        var runtimeCatalog = dependencies.AgentRuntimeCatalog;
        if (runtimeCatalog is null)
        {
            IAgentRuntimeCatalog physical =
                registry is null
                    ? new AgentRuntimeCatalog(registeredAdapters, executableResolver)
                    : new AgentRuntimeCatalog(registry, executableResolver);
            runtimeCatalog = configLoader is ITrackerConfigStore store
                ? new TestingAgentRuntimeCatalog(physical, store, workingDirectory)
                : physical;
        }
        var localLauncher =
            dependencies.LocalAgentSessionLauncher ??
            (registry is null
                ? new LocalAgentSessionLauncher(executableResolver)
                : new LocalAgentSessionLauncher(executableResolver, registry));
        builder.Services.AddSingleton(runtimeCatalog);
        builder.Services.AddSingleton(localLauncher);
        builder.Services.AddSingleton(new WebAgentSessionServices(
            dependencies.WorkspaceInventory,
            registeredAdapters.ToDictionary(
                adapter => adapter.Agent,
                StringComparer.OrdinalIgnoreCase),
            registry?.WorkerDescriptors ?? registeredAdapters
                .Select(adapter => new AgentDescriptor(
                    adapter.Agent,
                    char.ToUpperInvariant(adapter.Agent[0]) + adapter.Agent[1..],
                    adapter.Agent,
                    adapter.ExecutableName,
                    AgentCapabilities.WorkerExecution))
                .ToArray(),
            runtimeCatalog,
            localLauncher));
        builder.Services.AddSingleton(new WebOperationsServices(
            dependencies.RepositoryConfiguration,
            dependencies.WorkerInstanceRegistry ?? NoOpWorkerInstanceRegistry.Instance,
            hostedWorker,
            dependencies.ContextApproval,
            dependencies.UserConfiguration,
            dependencies.ModelDiscoveries ?? (registry is null
                ? null
                : new Workers.AgentModelDiscoveries(registry, runtimeCatalog)),
            dependencies.StorageLocations ?? new StorageLocationCatalog(new CachePaths(
                Environment.GetEnvironmentVariable("WRIGHTY_CACHE_DIR"))),
            dependencies.GitHubProjectUrls ?? GitHubProjectUrlResolver.Unavailable,
            dependencies.SkillMaintenance));
        builder.Services.AddSingleton<MarkdownRenderer>();
        builder.Services.AddRazorPages().AddApplicationPart(typeof(WrightyWebServer).Assembly);
        return builder;
    }

    private static void ConfigureApplication(
        WebApplication application,
        WebApplicationState state,
        WebDiagnostics diagnostics)
    {
        application.Use((context, next) =>
            HandleRequest(context, next, state, diagnostics));
        application.MapGet("/assets/{name}", AssetResponse);
        application.MapGet("/web/health", () => Results.Json(new { status = "ok" }));
        application.MapRazorPages();
    }

    private static async Task HandleRequest(
        HttpContext context,
        Func<Task> next,
        WebApplicationState state,
        WebDiagnostics diagnostics)
    {
        using var configurationScope = state.CaptureConfigurationForRequest();
        var config = state.Config;
        ApplySecurityHeaders(context.Response);
        if (!state.AllowsAuthority(context.Request.Host))
        {
            await WriteProblem(
                context,
                400,
                "HOST_INVALID",
                $"The request Host must be one of {Expected(state.AllowedAuthorities)}; " +
                $"received {SafeHeaderValue(context.Request.Host.Value)}.");
            return;
        }

        if (IsProtectedRequest(context.Request) &&
            state.TokenAuthenticationRequired &&
            !ValidToken(context.Request, state.Token!))
        {
            await WriteProblem(context, 401, "AUTH_REQUIRED", "The launch token is missing or invalid.");
            return;
        }

        if (IsMutation(context.Request) && !await ValidateMutation(context, state.AllowedOrigins))
        {
            return;
        }
        if (!IsMutation(context.Request) &&
            !state.Capabilities.LocalBoard &&
            IsLocalSurfaceHandler(context.Request.Query["handler"]))
        {
            await WriteProblem(
                context,
                404,
                "WEB_SURFACE_UNAVAILABLE",
                $"The requested Local Markdown surface is not available for backend '{config.Backend}'.");
            return;
        }
        if (IsMutation(context.Request) &&
            !state.Capabilities.LocalItemMutation &&
            !IsSharedMutation(context.Request.Query["handler"]))
        {
            await WriteProblem(
                context,
                405,
                "WEB_MUTATION_UNSUPPORTED",
                $"Work-item mutations are not available for backend '{config.Backend}'.");
            return;
        }

        try
        {
            await next();
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The client disconnected or deliberately abandoned the request. There is no response
            // left to write, and treating an expected request abort as WEB_UNEXPECTED obscures real
            // server failures in the operator log.
            return;
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

    private static async Task<bool> ValidateMutation(
        HttpContext context,
        FrozenSet<string> allowedOrigins)
    {
        var origin = context.Request.Headers.Origin.ToString();
        if (!allowedOrigins.Contains(origin))
        {
            await WriteProblem(
                context,
                403,
                "ORIGIN_INVALID",
                $"Mutation requests require one of {Expected(allowedOrigins)}; " +
                $"received {SafeHeaderValue(origin)}.");
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

    internal static string? AccessWarning(
        WebEndpointOptions endpoint,
        WebAuthenticationSession authentication)
    {
        if (authentication.Mode == WebAuthenticationMode.None)
        {
            var transport = endpoint.IsLoopback
                ? string.Empty
                : " Wrighty is serving plaintext HTTP on a non-loopback interface.";
            return "warning: Wrighty web token authentication is disabled. Every client able to " +
                   $"reach {endpoint.BindAddress} can read and mutate the tracker.{transport} " +
                   "An authenticating reverse proxy is ineffective if clients can bypass it.";
        }

        if (endpoint.IsLoopback)
        {
            return null;
        }

        var tokenMode = authentication.Mode == WebAuthenticationMode.PersistentToken
            ? "Persistent token authentication"
            : "Token authentication";
        return $"warning: Wrighty web is listening on non-loopback address {endpoint.BindAddress} " +
               $"over plaintext HTTP. {tokenMode} is enabled; any client that can reach this " +
               "address can attempt access, and possession of the launch URL grants web console " +
               "access. Use only on an encrypted or trusted transport such as Tailscale.";
    }

    private static bool IsProtectedRequest(HttpRequest request) =>
        request.Query.ContainsKey("handler") || request.Path.StartsWithSegments("/web/fragments");

    private static bool IsMutation(HttpRequest request) =>
        !HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method) && !HttpMethods.IsOptions(request.Method);

    /// <summary>
    /// Handlers that post something other than a work-item edit, and so must keep working on a
    /// backend that owns its own items.
    ///
    /// **Every new POST handler must be classified here or it is treated as a work-item mutation
    /// and refused on GitHub.** That is how machine-local settings were rejected with
    /// "Work-item mutations are not available for backend 'github'" — a message about work items,
    /// for a setting that has nothing to do with them.
    /// </summary>
    internal static bool IsSharedMutation(string? handler) =>
        string.Equals(handler, "Configuration", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(handler, "StartHostedWorker", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(handler, "StopWorker", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(handler, "UserConfiguration", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(handler, "ProfileMapping", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(handler, "ValidateTarget", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(handler, "ApproveContext", StringComparison.OrdinalIgnoreCase) ||
        // Skill maintenance updates machine-local agent configuration, not backend-owned item
        // content. Keep it available regardless of which project backend the console displays.
        string.Equals(handler, "UpdateSkill", StringComparison.OrdinalIgnoreCase) ||
        // Opening a retained vendor session operates on Wrighty's local claim/session control
        // plane, not backend-owned item content. Operations offers these on both Local Markdown
        // and GitHub, so they are shared even though their target is one item.
        string.Equals(handler, "OpenSessionCli", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(handler, "OpenSessionDesktop", StringComparison.OrdinalIgnoreCase) ||
        // Provider capacity probes the locally installed agent CLIs. Nothing about it is
        // backend-specific, and the console renders its buttons on GitHub — where, until this was
        // measured, every one of them returned 405. Pre-existing, and the same omission as above.
        string.Equals(handler, "ProbeProvider", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(handler, "ProbeAllProviders", StringComparison.OrdinalIgnoreCase);

    private static bool IsLocalSurfaceHandler(string? handler) =>
        string.Equals(handler, "Board", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(handler, "Item", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(handler, "Create", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(handler, "Edit", StringComparison.OrdinalIgnoreCase);

    private static bool ValidToken(HttpRequest request, string expected)
    {
        var supplied = request.Headers[TokenHeader].ToString();
        if (supplied.Length != expected.Length) return false;
        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(supplied),
            System.Text.Encoding.UTF8.GetBytes(expected));
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
            "app.js" => ("Highbyte.Wrighty.Web.Assets.app.js", JavaScriptContentType),
            "board-controls.mjs" => ("Highbyte.Wrighty.Web.Assets.board-controls.mjs", JavaScriptContentType),
            "confirmation-dialog.mjs" => ("Highbyte.Wrighty.Web.Assets.confirmation-dialog.mjs", JavaScriptContentType),
            "context-panel.mjs" => ("Highbyte.Wrighty.Web.Assets.context-panel.mjs", JavaScriptContentType),
            "hosted-log.mjs" => ("Highbyte.Wrighty.Web.Assets.hosted-log.mjs", JavaScriptContentType),
            "launch-token.mjs" => ("Highbyte.Wrighty.Web.Assets.launch-token.mjs", JavaScriptContentType),
            "page-regions.mjs" => ("Highbyte.Wrighty.Web.Assets.page-regions.mjs", JavaScriptContentType),
            "relative-time.mjs" => ("Highbyte.Wrighty.Web.Assets.relative-time.mjs", JavaScriptContentType),
            "settings-dirty.mjs" => ("Highbyte.Wrighty.Web.Assets.settings-dirty.mjs", JavaScriptContentType),
            "settings-scroll.mjs" => ("Highbyte.Wrighty.Web.Assets.settings-scroll.mjs", JavaScriptContentType),
            "token-picker.mjs" => ("Highbyte.Wrighty.Web.Assets.token-picker.mjs", JavaScriptContentType),
            "htmx.js" => ("Highbyte.Wrighty.Web.Assets.vendor.htmx-2.0.9.min.js", JavaScriptContentType),
            "highlight-yaml.js" => ("Highbyte.Wrighty.Web.Assets.vendor.highlight-yaml-11.11.1.min.js", JavaScriptContentType),
            _ => default
        };
        if (asset.Item1 is null) return Results.NotFound();
        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(asset.Item1);
        return stream is null ? Results.NotFound() : Results.Stream(stream, asset.Item2);
    }

    private static string SafeMessage(string message, TrackerConfig config)
    {
        if (config.SourcePath is not { } sourcePath) return message;
        var root = Path.GetDirectoryName(Path.GetFullPath(sourcePath));
        return string.IsNullOrEmpty(root)
            ? message
            : message.Replace(root, "<tracker>", StringComparison.Ordinal);
    }

    private static string ListeningUrl(
        WebApplication application,
        WebEndpointOptions endpoint)
    {
        var addresses = application.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses
            ?? throw new InvalidOperationException(
                "The web server did not report a listening address.");
        var expectedAddress = endpoint.IsLoopback
            ? IPAddress.Loopback
            : endpoint.BindAddress;
        var address = addresses.FirstOrDefault(value =>
            Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            IPAddress.TryParse(uri.Host, out var candidate) &&
            candidate.Equals(expectedAddress));
        return address is not null
            ? NormalizeListeningUrl(address)
            : throw new InvalidOperationException("The web server did not report a listening address.");
    }

    internal static string NormalizeListeningUrl(string address)
    {
        var uri = new Uri(address, UriKind.Absolute);
        if (!string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return address;
        }

        var normalized = new UriBuilder(uri) { Host = "127.0.0.1" }.Uri;
        return normalized.AbsolutePath == "/" &&
               string.IsNullOrEmpty(normalized.Query) &&
               string.IsNullOrEmpty(normalized.Fragment)
            ? normalized.GetLeftPart(UriPartial.Authority)
            : normalized.AbsoluteUri;
    }

    private static string Expected(IEnumerable<string> values) =>
        string.Join(", ", values.Order(StringComparer.Ordinal));

    private static string SafeHeaderValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "(empty)";
        }

        return string.Concat(value.Select(character =>
            character is >= '!' and <= '~' &&
            character is not '<' and not '>' and not '&' and not '"' and not '\''
                ? character
                : '?'));
    }
}
