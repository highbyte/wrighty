using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;
using System.Text;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Errors;

namespace Highbyte.Wrighty.Web;

internal enum WebAuthenticationMode
{
    EphemeralToken,
    PersistentToken,
    None
}

internal sealed record WebAuthenticationOptions(
    WebAuthenticationMode Mode,
    string? TokenFile,
    bool RotateToken);

internal sealed record WebAuthenticationSession(
    WebAuthenticationMode Mode,
    string? Token)
{
    public bool TokenRequired => Mode != WebAuthenticationMode.None;
}

internal static class WebAuthenticationOptionsResolver
{
    public static WebAuthenticationOptions Resolve(WebServerOptions options)
    {
        var authMode = options.AuthMode.Trim();
        if (!string.Equals(authMode, "token", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(authMode, "none", StringComparison.OrdinalIgnoreCase))
        {
            throw new TrackerException(
                "WEB_AUTH_INVALID",
                "--auth must be either 'token' or 'none'.",
                2);
        }

        var tokenFile = string.IsNullOrWhiteSpace(options.TokenFile)
            ? null
            : options.TokenFile.Trim();
        var persistent = options.PersistToken || tokenFile is not null;
        if (string.Equals(authMode, "none", StringComparison.OrdinalIgnoreCase))
        {
            if (persistent || options.RotateToken)
            {
                throw new TrackerException(
                    "WEB_AUTH_OPTIONS_CONFLICT",
                    "--auth none cannot be combined with --persist-token, --token-file, or --rotate-token.",
                    2);
            }

            return new WebAuthenticationOptions(WebAuthenticationMode.None, null, false);
        }

        if (options.RotateToken && !persistent)
        {
            throw new TrackerException(
                "WEB_AUTH_OPTIONS_CONFLICT",
                "--rotate-token requires --persist-token or --token-file.",
                2);
        }

        return new WebAuthenticationOptions(
            persistent
                ? WebAuthenticationMode.PersistentToken
                : WebAuthenticationMode.EphemeralToken,
            tokenFile,
            options.RotateToken);
    }
}

internal sealed class WebTokenProvider(string? managedRoot = null)
{
    private const UnixFileMode TokenDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode TokenFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public WebAuthenticationSession Resolve(
        WebAuthenticationOptions options,
        TrackerConfig config,
        string workingDirectory)
    {
        if (options.Mode == WebAuthenticationMode.None)
        {
            return new WebAuthenticationSession(options.Mode, null);
        }

        if (options.Mode == WebAuthenticationMode.EphemeralToken)
        {
            return new WebAuthenticationSession(options.Mode, GenerateToken());
        }

        var trackerRoot = TrackerRoot(config, workingDirectory);
        var managed = options.TokenFile is null;
        var path = managed
            ? ManagedTokenPath(trackerRoot)
            : Path.GetFullPath(options.TokenFile!, workingDirectory);
        EnsureOutsideTracker(path, trackerRoot);
        return new WebAuthenticationSession(
            options.Mode,
            LoadOrCreate(path, options.RotateToken, managed));
    }

    internal string ManagedTokenPath(string trackerRoot)
    {
        var fullRoot = Path.GetFullPath(trackerRoot);
        var canonicalRoot = fullRoot.Length == Path.GetPathRoot(fullRoot)?.Length
            ? fullRoot
            : fullRoot.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        var hashInput = OperatingSystem.IsWindows()
            ? canonicalRoot.ToUpperInvariant()
            : canonicalRoot;
        var hash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(hashInput)))[..12];
        var slug = Slug(Path.GetFileName(canonicalRoot));
        return Path.Combine(
            managedRoot ?? DefaultManagedRoot(),
            $"{slug}-{hash}",
            "token");
    }

    internal static string GenerateToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static string TrackerRoot(TrackerConfig config, string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(config.SourcePath))
        {
            return Path.GetFullPath(workingDirectory);
        }

        var sourcePath = Path.GetFullPath(config.SourcePath, workingDirectory);
        return Path.GetDirectoryName(sourcePath)
            ?? Path.GetFullPath(workingDirectory);
    }

    private static void EnsureOutsideTracker(string tokenPath, string trackerRoot)
    {
        var relative = Path.GetRelativePath(
            ResolvePhysicalPath(trackerRoot),
            ResolvePhysicalPath(tokenPath));
        var firstSegment = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries)[0];
        if (!Path.IsPathRooted(relative) &&
            !string.Equals(firstSegment, "..", StringComparison.Ordinal))
        {
            throw new TrackerException(
                "WEB_TOKEN_FILE_IN_REPOSITORY",
                "--token-file must be outside the tracker repository.",
                2);
        }
    }

    private static string ResolvePhysicalPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)
            ?? throw new IOException($"Path '{path}' has no filesystem root.");
        var relative = Path.GetRelativePath(root, fullPath);
        if (relative == ".")
        {
            return root;
        }

        var current = root;
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo? entry = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : File.Exists(current)
                    ? new FileInfo(current)
                    : null;
            if (entry?.ResolveLinkTarget(returnFinalTarget: true) is { } target)
            {
                current = ResolvePhysicalPath(target.FullName);
            }
        }

        return Path.GetFullPath(current);
    }

    private static string LoadOrCreate(string path, bool rotate, bool managed)
    {
        try
        {
            var directory = Path.GetDirectoryName(path)
                ?? throw new IOException("The token path has no parent directory.");
            var directoryExisted = Directory.Exists(directory);
            if (!OperatingSystem.IsWindows() && !directoryExisted)
            {
                Directory.CreateDirectory(directory, TokenDirectoryMode);
            }
            else
            {
                Directory.CreateDirectory(directory);
            }

            if (OperatingSystem.IsWindows())
            {
                if (managed)
                {
                    if (!directoryExisted)
                    {
                        SetWindowsUserOnlyAccess(directory, directory: true);
                    }

                    EnsureWindowsUserOnlyAccess(path, directory, directory: true);
                }
            }
            else
            {
                if (managed)
                {
                    EnsureUnixDirectory(path, directory);
                }
            }

            using var tokenLock = AcquireTokenLock($"{path}.lock");
            if (File.Exists(path))
            {
                EnsureSecureTokenFile(path);
                if (!rotate)
                {
                    return ReadToken(path);
                }
            }

            var token = GenerateToken();
            var temporaryPath = Path.Combine(
                directory,
                $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            try
            {
                WriteToken(temporaryPath, token);
                try
                {
                    File.Move(temporaryPath, path, overwrite: rotate);
                }
                catch (IOException) when (!rotate && File.Exists(path))
                {
                    EnsureSecureTokenFile(path);
                    return ReadToken(path);
                }

                EnsureSecureTokenFile(path);
                return ReadToken(path);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
        catch (TrackerException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw new TrackerException(
                "WEB_TOKEN_FILE_ERROR",
                $"Could not securely load or create web token file '{path}': {exception.Message}",
                3,
                innerException: exception);
        }
    }

    private static FileStream AcquireTokenLock(string path)
    {
        const int attempts = 250;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                var options = new FileStreamOptions
                {
                    Mode = FileMode.OpenOrCreate,
                    Access = FileAccess.ReadWrite,
                    Share = FileShare.None
                };
                if (!OperatingSystem.IsWindows())
                {
                    options.UnixCreateMode = TokenFileMode;
                }

                var stream = new FileStream(path, options);
                try
                {
                    if (OperatingSystem.IsWindows())
                    {
                        SetWindowsUserOnlyAccess(path, directory: false);
                    }

                    EnsureSecureTokenFile(path);
                    return stream;
                }
                catch
                {
                    stream.Dispose();
                    throw;
                }
            }
            catch (IOException) when (attempt < attempts - 1)
            {
                Thread.Sleep(20);
            }
        }

        throw new IOException($"Timed out acquiring token file lock '{path}'.");
    }

    private static void WriteToken(string path, string token)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.WriteThrough
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = TokenFileMode;
            using var unixStream = new FileStream(path, options);
            WriteToken(unixStream, token);
            return;
        }

        using (new FileStream(path, options))
        {
        }

        SetWindowsUserOnlyAccess(path, directory: false);
        options.Mode = FileMode.Truncate;
        using var windowsStream = new FileStream(path, options);
        WriteToken(windowsStream, token);
    }

    private static void WriteToken(FileStream stream, string token)
    {
        stream.Write(Encoding.UTF8.GetBytes($"{token}{Environment.NewLine}"));
        stream.Flush(flushToDisk: true);
    }

    private static string ReadToken(string path)
    {
        var token = File.ReadAllText(path, Encoding.UTF8).TrimEnd('\r', '\n');
        if (token.Length != 43 ||
            token.Any(character =>
                !char.IsAsciiLetterOrDigit(character) &&
                character is not '-' and not '_'))
        {
            throw new TrackerException(
                "WEB_TOKEN_FILE_INVALID",
                $"Web token file '{path}' does not contain a valid Wrighty token.",
                3);
        }

        return token;
    }

    [UnsupportedOSPlatform("windows")]
    private static void EnsureUnixDirectory(string tokenPath, string directory)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 ||
            File.GetUnixFileMode(directory) != TokenDirectoryMode)
        {
            throw UnsafePermissions(tokenPath);
        }

        VerifyUnixOwnership(directory, tokenPath);
    }

    private static void EnsureSecureTokenFile(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw UnsafePermissions(path);
        }

        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(path);
            if (mode != TokenFileMode)
            {
                throw UnsafePermissions(path);
            }

            VerifyUnixOwnership(path, path);
            return;
        }

        EnsureWindowsUserOnlyAccess(path, path, directory: false);
    }

    [UnsupportedOSPlatform("windows")]
    private static void VerifyUnixOwnership(string path, string tokenPath)
    {
        try
        {
            var mode = File.GetUnixFileMode(path);
            File.SetUnixFileMode(path, mode);
        }
        catch (UnauthorizedAccessException)
        {
            throw UnsafePermissions(tokenPath);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void SetWindowsUserOnlyAccess(string path, bool directory)
    {
        var owner = WindowsIdentity.GetCurrent().User
            ?? throw new UnauthorizedAccessException(
                "The current Windows user does not have a security identifier.");
        FileSystemSecurity security = directory
            ? new DirectorySecurity()
            : new FileSecurity();
        security.SetOwner(owner);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            owner,
            FileSystemRights.FullControl,
            directory
                ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit
                : InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Allow));

        if (directory)
        {
            new DirectoryInfo(path).SetAccessControl((DirectorySecurity)security);
        }
        else
        {
            new FileInfo(path).SetAccessControl((FileSecurity)security);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void EnsureWindowsUserOnlyAccess(
        string tokenPath,
        string path,
        bool directory)
    {
        var owner = WindowsIdentity.GetCurrent().User
            ?? throw UnsafePermissions(tokenPath);
        FileSystemSecurity security = directory
            ? new DirectoryInfo(path).GetAccessControl(
                AccessControlSections.Owner | AccessControlSections.Access)
            : new FileInfo(path).GetAccessControl(
                AccessControlSections.Owner | AccessControlSections.Access);
        if (!owner.Equals(security.GetOwner(typeof(SecurityIdentifier))) ||
            security.GetAccessRules(
                    includeExplicit: true,
                    includeInherited: true,
                    typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .Any(rule =>
                    rule.AccessControlType == AccessControlType.Allow &&
                    !owner.Equals(rule.IdentityReference)))
        {
            throw UnsafePermissions(tokenPath);
        }
    }

    private static TrackerException UnsafePermissions(string path) =>
        new(
            "WEB_TOKEN_FILE_UNSAFE",
            $"Web token path '{path}' must be owned by the current user with user-only permissions.",
            3);

    private static string DefaultManagedRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Wrighty",
                "webui");
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".wrighty",
            "webui");
    }

    private static string Slug(string value)
    {
        var characters = value
            .Select(character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'
                    ? char.ToLowerInvariant(character)
                    : '-')
            .ToArray();
        var slug = new string(characters).Trim('-', '.', '_');
        return slug.Length == 0 ? "tracker" : slug;
    }
}
