using Highbyte.Wrighty.Processes;

namespace Highbyte.Wrighty.UnitTests.Processes;

public sealed class PathExecutableResolverTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"wrighty-executable-resolver-tests-{Guid.NewGuid():N}");

    public PathExecutableResolverTests()
    {
        Directory.CreateDirectory(root);
    }

    [Fact]
    public void Resolve_returns_an_absolute_executable_path()
    {
        var executable = CreateExecutable(root, "tool");
        var resolver = CreateResolver(root);

        var result = resolver.Resolve("tool");

        Assert.Equal(executable, result);
        Assert.True(Path.IsPathFullyQualified(result));
    }

    [Fact]
    public void Resolve_ignores_relative_search_directories()
    {
        var trustedDirectory = Path.Combine(root, "trusted");
        Directory.CreateDirectory(trustedDirectory);
        var executable = CreateExecutable(trustedDirectory, "tool");
        var searchPath = string.Join(Path.PathSeparator, "relative", trustedDirectory);
        var resolver = CreateResolver(searchPath);

        var result = resolver.Resolve("tool");

        Assert.Equal(executable, result);
    }

    [Fact]
    public void Resolve_supports_an_explicit_executable_extension()
    {
        var executable = CreateExecutable(root, "tool.test");
        var resolver = new PathExecutableResolver(root, [".test"]);

        var result = resolver.Resolve("tool");

        Assert.Equal(executable, result);
    }

    [Fact]
    public void Resolve_rejects_names_containing_a_path()
    {
        var resolver = CreateResolver(root);

        Assert.Throws<ArgumentException>(() => resolver.Resolve(Path.Combine("somewhere", "tool")));
    }

    [Fact]
    public void Resolve_throws_when_the_executable_is_missing()
    {
        var resolver = CreateResolver(root);

        var exception = Assert.Throws<FileNotFoundException>(() => resolver.Resolve("missing"));

        Assert.Equal("missing", exception.FileName);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }
    }

    private static PathExecutableResolver CreateResolver(string searchPath) =>
        new(searchPath, OperatingSystem.IsWindows() ? [".exe"] : [""]);

    private static string CreateExecutable(string directory, string name)
    {
        var fileName = OperatingSystem.IsWindows() && !Path.HasExtension(name)
            ? name + ".exe"
            : name;
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, string.Empty);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return Path.GetFullPath(path);
    }
}
