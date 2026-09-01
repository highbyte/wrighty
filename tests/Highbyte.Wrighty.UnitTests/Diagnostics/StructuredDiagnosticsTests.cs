using Highbyte.Wrighty.Cli;
using Highbyte.Wrighty.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Highbyte.Wrighty.UnitTests.Diagnostics;

public sealed class StructuredDiagnosticsTests
{
    [Fact]
    public async Task Web_failure_fans_out_with_structured_safe_properties()
    {
        var first = new RecordingLoggerProvider();
        var second = new RecordingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddProvider(first);
            builder.AddProvider(second);
        });
        var diagnostics = new WebDiagnostics(
            loggerFactory.CreateLogger<WebDiagnostics>());
        var context = Context(
            StatusCodes.Status500InternalServerError,
            "/items",
            "?handler=Edit&id=local%3A7&scope=user&token=must-not-appear");
        var exception = new InvalidOperationException("Synthetic failure");
        WebDiagnostics.RetainFailure(context, "WEB_UNEXPECTED:correlation-id", exception);

        await diagnostics.LogFailureAsync(context);

        var firstEntry = Assert.Single(first.Entries);
        var secondEntry = Assert.Single(second.Entries);
        Assert.Equal(2002, firstEntry.EventId.Id);
        Assert.Equal(LogLevel.Error, firstEntry.Level);
        Assert.Equal(firstEntry.Message, secondEntry.Message);
        Assert.Same(exception, firstEntry.Exception);
        Assert.Equal("POST", firstEntry.Properties["RequestMethod"]);
        Assert.Equal(
            "/items?handler=Edit&id=local%3A7&scope=user",
            firstEntry.Properties["RequestTarget"]);
        Assert.Equal(500, firstEntry.Properties["StatusCode"]);
        Assert.Equal(
            "WEB_UNEXPECTED:correlation-id",
            firstEntry.Properties["ErrorCode"]);
        Assert.DoesNotContain("token", firstEntry.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("must-not-appear", firstEntry.Message);
    }

    [Fact]
    public async Task Category_filter_suppresses_warning_but_keeps_error()
    {
        var logs = new RecordingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddFilter(typeof(WebDiagnostics).FullName!, LogLevel.Error);
            builder.AddProvider(logs);
        });
        var diagnostics = new WebDiagnostics(
            loggerFactory.CreateLogger<WebDiagnostics>());
        var context = Context(StatusCodes.Status400BadRequest, "/bad-request", string.Empty);

        await diagnostics.LogFailureAsync(context);
        Assert.Empty(logs.Entries);

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await diagnostics.LogFailureAsync(context);
        var entry = Assert.Single(logs.Entries);
        Assert.Equal(2002, entry.EventId.Id);
        Assert.Equal("HTTP_500", entry.Properties["ErrorCode"]);
    }

    private static DefaultHttpContext Context(
        int statusCode,
        string path,
        string queryString)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(queryString);
        context.Response.StatusCode = statusCode;
        return context;
    }
}

[Collection("Process environment")]
public sealed class ConsoleDiagnosticRoutingTests
{
    [Fact]
    public void Process_logger_writes_diagnostics_only_to_standard_error()
    {
        var originalOutput = Console.Out;
        var originalError = Console.Error;
        var output = new StringWriter();
        var error = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            using (var loggerFactory = DiagnosticLogging.CreateFactory())
            {
                CliDiagnostics.AgentContextWarning(
                    loggerFactory.CreateLogger<CliApplication>(),
                    "Synthetic diagnostic warning.");
            }
        }
        finally
        {
            Console.SetOut(originalOutput);
            Console.SetError(originalError);
        }

        Assert.Equal(string.Empty, output.ToString());
        Assert.Contains("Synthetic diagnostic warning.", error.ToString());
        Assert.Contains("Highbyte.Wrighty.Cli.CliApplication[1004]", error.ToString());
    }
}
