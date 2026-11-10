using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Masterly.NightShift.Logging;

namespace Masterly.NightShift.Dashboard;

/// <summary>The user name and password that open the dashboard under HTTP Basic authentication.</summary>
public sealed class DashboardBasicAuthCredential
{
    /// <summary>Initializes the credential.</summary>
    /// <param name="userName">The user name, which can't contain a colon. Compared exactly.</param>
    /// <param name="password">The password. Compared exactly.</param>
    /// <exception cref="ArgumentException">A value is empty, or the user name contains a colon.</exception>
    public DashboardBasicAuthCredential([NotNull] string userName, [NotNull] string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(userName);
        ArgumentException.ThrowIfNullOrEmpty(password);

        if (userName.Contains(':', StringComparison.Ordinal))
        {
            throw new ArgumentException("HTTP Basic authentication doesn't allow a colon in the user name.", nameof(userName));
        }

        UserName = userName;
        Password = password;
    }

    /// <summary>The user name.</summary>
    public string UserName { get; }

    /// <summary>The password.</summary>
    public string Password { get; }
}

/// <summary>
/// Supplies the dashboard's credential on each request, for values kept in a secret store. The filter calls it
/// for every request, including the page's polling, so cache what is expensive to read.
/// </summary>
public interface IDashboardBasicAuthCredentialProvider
{
    /// <summary>Gets the current credential.</summary>
    /// <param name="cancellationToken">Canceled when the request is aborted.</param>
    /// <returns>The credential, or <c>null</c> to refuse everyone.</returns>
    Task<DashboardBasicAuthCredential?> GetCredentialAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Protects the dashboard with HTTP Basic authentication, for hosts without a sign-in of their own. A wrong,
/// missing or malformed <c>Authorization</c> header gets the browser's sign-in prompt. An accepted user becomes
/// the request's user, unless the request is already signed in, so the audit log records the name.
/// </summary>
/// <remarks>
/// Browsers send Basic credentials on their own, as they do cookies, so keep the dashboard's antiforgery
/// protection on and register <c>AddAntiforgery()</c>. Nothing here limits password guessing: put a rate
/// limiter on the endpoint when it is reachable from outside.
/// </remarks>
public sealed class BasicAuthDashboardAuthorizationFilter : IDashboardAsyncAuthorizationFilter
{
    private const string Scheme = "Basic";
    private const char Separator = ':';
    private const int MaxEncodedLength = 8192;

    private static readonly ILog Logger = LogProvider.GetLogger(typeof(BasicAuthDashboardAuthorizationFilter));

    private readonly IDashboardBasicAuthCredentialProvider _credentialProvider;

    /// <summary>Initializes the filter with a fixed user name and password.</summary>
    /// <param name="userName">The user name, which can't contain a colon.</param>
    /// <param name="password">The password.</param>
    public BasicAuthDashboardAuthorizationFilter([NotNull] string userName, [NotNull] string password)
        : this(new FixedCredentialProvider(new DashboardBasicAuthCredential(userName, password)))
    {
    }

    /// <summary>Initializes the filter with a provider that supplies the credential on each request.</summary>
    /// <param name="credentialProvider">Supplies the credential.</param>
    public BasicAuthDashboardAuthorizationFilter([NotNull] IDashboardBasicAuthCredentialProvider credentialProvider)
    {
        ArgumentNullException.ThrowIfNull(credentialProvider);
        _credentialProvider = credentialProvider;
    }

    /// <summary>The name browsers show in the sign-in prompt.</summary>
    public string Realm { get; init; } = "Masterly.NightShift Dashboard";

    /// <summary>
    /// Refuses requests that aren't HTTPS, because Basic authentication sends the password readable. Loopback
    /// connections are allowed, for local development. Default: <c>true</c>. Behind a proxy that terminates TLS,
    /// use <c>UseForwardedHeaders</c> so the request reports HTTPS.
    /// </summary>
    public bool RequireHttps { get; init; } = true;

    /// <inheritdoc />
    public async Task<bool> AuthorizeAsync(DashboardContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        HttpContext httpContext = context.GetHttpContext();

        if (RequireHttps && !IsSecureEnough(httpContext))
        {
            // No challenge: it would ask the browser to send the password over this connection
            Logger.Warn("A dashboard request over plain HTTP was refused, because Basic authentication would expose the password. Serve the dashboard over HTTPS, call UseForwardedHeaders behind a proxy that terminates TLS, or set RequireHttps to false.");
            return false;
        }

        DashboardBasicAuthCredential? expected = await _credentialProvider.GetCredentialAsync(httpContext.RequestAborted);

        if (expected != null && Matches(httpContext.Request.Headers.Authorization.ToString(), expected))
        {
            SignIn(httpContext, expected.UserName);
            return true;
        }

        httpContext.Response.Headers.WWWAuthenticate = $"{Scheme} realm=\"{EscapeQuotedString(Realm)}\", charset=\"UTF-8\"";
        return false;
    }

    private static bool IsSecureEnough(HttpContext httpContext)
    {
        return httpContext.Request.IsHttps
            || (httpContext.Connection.RemoteIpAddress is { } remoteIp && IPAddress.IsLoopback(remoteIp));
    }

    private static bool Matches(string header, DashboardBasicAuthCredential expected)
    {
        if (!AuthenticationHeaderValue.TryParse(header, out AuthenticationHeaderValue? value)
            || !Scheme.Equals(value.Scheme, StringComparison.OrdinalIgnoreCase)
            || value.Parameter is not { Length: > 0 and <= MaxEncodedLength } encoded)
        {
            return false;
        }

        Span<byte> decoded = stackalloc byte[MaxEncodedLength];

        if (!Convert.TryFromBase64String(encoded, decoded, out int length))
        {
            return false;
        }

        // Compared as UTF-8 bytes, so nothing depends on decoding text an attacker chose. Colons are one byte.
        ReadOnlySpan<byte> credentials = decoded[..length];
        int separatorIndex = credentials.IndexOf((byte)Separator);

        if (separatorIndex < 0)
        {
            return false;
        }

        // Both are always compared, with "&", so the response time doesn't tell which one was wrong
        return SecretEquals(credentials[..separatorIndex], expected.UserName) & SecretEquals(credentials[(separatorIndex + 1)..], expected.Password);
    }

    // Hashing first gives both sides the same length, so the comparison doesn't reveal the secret's length
    private static bool SecretEquals(ReadOnlySpan<byte> received, string expected)
    {
        Span<byte> receivedHash = stackalloc byte[SHA256.HashSizeInBytes];
        Span<byte> expectedHash = stackalloc byte[SHA256.HashSizeInBytes];

        SHA256.HashData(received, receivedHash);
        SHA256.HashData(Encoding.UTF8.GetBytes(expected), expectedHash);

        return CryptographicOperations.FixedTimeEquals(receivedHash, expectedHash);
    }

    private static void SignIn(HttpContext httpContext, string userName)
    {
        if (httpContext.User.Identity?.IsAuthenticated == true) return;

        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, userName)], Scheme));
    }

    private static string EscapeQuotedString(string text)
    {
        return text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
    }

    private sealed class FixedCredentialProvider(DashboardBasicAuthCredential credential) : IDashboardBasicAuthCredentialProvider
    {
        public Task<DashboardBasicAuthCredential?> GetCredentialAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<DashboardBasicAuthCredential?>(credential);
        }
    }
}
