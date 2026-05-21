using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.RegularExpressions;

namespace FlowEncode.Infrastructure;

public enum FlowEncodeHttpClientProfile
{
    Api,
    Download
}

public interface IFlowEncodeHttpClientFactory
{
    HttpClient CreateClient(FlowEncodeHttpClientProfile profile);
}

internal sealed partial class FlowEncodeHttpClientFactory : IFlowEncodeHttpClientFactory, IDisposable
{
    private const string FlowEncodeGitHubTokenVariable = "FLOWENCODE_GITHUB_TOKEN";
    private const string GitHubTokenVariable = "GITHUB_TOKEN";
    internal static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(30);
    internal static readonly TimeSpan ConnectionLifetime = TimeSpan.FromMinutes(15);
    internal static readonly TimeSpan ConnectionIdleTimeout = TimeSpan.FromMinutes(5);

    private readonly SocketsHttpHandler _handler = new()
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        PooledConnectionLifetime = ConnectionLifetime,
        PooledConnectionIdleTimeout = ConnectionIdleTimeout
    };

    public HttpClient CreateClient(FlowEncodeHttpClientProfile profile)
    {
        var client = new HttpClient(_handler, disposeHandler: false)
        {
            Timeout = profile switch
            {
                FlowEncodeHttpClientProfile.Api => ApiTimeout,
                FlowEncodeHttpClientProfile.Download => DownloadTimeout,
                _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, null)
            }
        };

        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("FlowEncode", ResolveUserAgentVersion()));
        TryApplyGitHubAuthorizationHeader(client, profile);
        return client;
    }

    internal TimeSpan PooledConnectionLifetimeForTesting => _handler.PooledConnectionLifetime;

    internal TimeSpan PooledConnectionIdleTimeoutForTesting => _handler.PooledConnectionIdleTimeout;

    public void Dispose()
    {
        _handler.Dispose();
    }

    private static void TryApplyGitHubAuthorizationHeader(HttpClient client, FlowEncodeHttpClientProfile profile)
    {
        if (profile != FlowEncodeHttpClientProfile.Api)
        {
            return;
        }

        var token = Environment.GetEnvironmentVariable(FlowEncodeGitHubTokenVariable)
            ?? Environment.GetEnvironmentVariable(GitHubTokenVariable);
        if (string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
    }

    private static string ResolveUserAgentVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        return NormalizeVersionLabel(informationalVersion)
            ?? NormalizeVersionLabel(assembly.GetName().Version?.ToString())
            ?? "1.0.0";
    }

    private static string? NormalizeVersionLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length > 1
            && (trimmed[0] == 'v' || trimmed[0] == 'V')
            && char.IsDigit(trimmed[1]))
        {
            trimmed = trimmed[1..];
        }

        var versionMatch = UserAgentVersionRegex().Match(trimmed);
        if (!versionMatch.Success)
        {
            return null;
        }

        var suffix = versionMatch.Groups["suffix"].Success
            ? versionMatch.Groups["suffix"].Value.ToLowerInvariant()
            : string.Empty;
        return versionMatch.Groups["base"].Value + suffix;
    }

    [GeneratedRegex("(?<base>\\d+\\.\\d+(?:\\.\\d+)*)(?<suffix>[0-9a-f]{7,12})?", RegexOptions.IgnoreCase)]
    private static partial Regex UserAgentVersionRegex();
}
