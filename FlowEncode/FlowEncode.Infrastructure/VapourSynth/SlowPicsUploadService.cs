using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FlowEncode.Application;

namespace FlowEncode.Infrastructure;

public sealed class SlowPicsUploadService
{
    private const string ComparisonPageUrl = "https://slow.pics/comparison";
    private const string ComparisonApiUrl = "https://slow.pics/api/comparison";
    private const string DefaultUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) FlowEncode";
    private readonly Func<HttpMessageHandler> _handlerFactory;

    public SlowPicsUploadService()
        : this(CreateDefaultHandler)
    {
    }

    internal SlowPicsUploadService(Func<HttpMessageHandler> handlerFactory)
    {
        _handlerFactory = handlerFactory;
    }

    public async Task<SlowPicsUploadResult> UploadAsync(
        SlowPicsUploadRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Groups.Count == 0)
        {
            return new SlowPicsUploadResult(false, null, null, "No comparison frames selected.");
        }

        using var handler = _handlerFactory();
        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(5)
        };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", DefaultUserAgent);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", ComparisonPageUrl);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://slow.pics");

        try
        {
            progress?.Report("Connecting to slow.pics...");
            string? xsrfToken = null;
            using (var handshake = await client.GetAsync(ComparisonPageUrl, cancellationToken).ConfigureAwait(false))
            {
                // Handshake establishes session cookies (including XSRF-TOKEN).
                xsrfToken = TryExtractXsrfToken(handler, handshake);
            }

            if (!string.IsNullOrWhiteSpace(xsrfToken))
            {
                client.DefaultRequestHeaders.TryAddWithoutValidation("X-XSRF-TOKEN", xsrfToken);
            }

            using var form = new MultipartFormDataContent();
            var collectionName = string.IsNullOrWhiteSpace(request.CollectionName)
                ? "FlowEncode Comparison"
                : request.CollectionName.Trim();
            form.Add(new StringContent(collectionName), "collectionName");
            form.Add(new StringContent(request.IsPublic ? "true" : "false"), "public");
            form.Add(new StringContent(request.OptimizeImages ? "true" : "false"), "optimize-images");

            var totalImages = 0;
            for (var g = 0; g < request.Groups.Count; g++)
            {
                var group = request.Groups[g];
                var groupName = string.IsNullOrWhiteSpace(group.Name) ? $"Frame {group.FrameNumber}" : group.Name;
                form.Add(new StringContent(groupName), $"comparisons[{g}].name");

                for (var i = 0; i < group.Images.Count; i++)
                {
                    var img = group.Images[i];
                    if (!File.Exists(img.RelativeOrAbsolutePath))
                    {
                        return new SlowPicsUploadResult(false, null, null, $"Image file not found: {img.RelativeOrAbsolutePath}");
                    }

                    form.Add(new StringContent(img.OutputName), $"comparisons[{g}].images[{i}].name");
                    var bytes = await File.ReadAllBytesAsync(img.RelativeOrAbsolutePath, cancellationToken).ConfigureAwait(false);
                    var fileContent = new ByteArrayContent(bytes);
                    fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
                    form.Add(fileContent, $"comparisons[{g}].images[{i}].file", Path.GetFileName(img.RelativeOrAbsolutePath));
                    totalImages++;
                }
            }

            progress?.Report($"Uploading {totalImages} PNG images to slow.pics...");
            using var response = await client.PostAsync(ComparisonApiUrl, form, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var snippet = body.Length > 240 ? body[..240] : body;
                return new SlowPicsUploadResult(
                    false,
                    null,
                    null,
                    $"HTTP {(int)response.StatusCode} ({response.ReasonPhrase}): {snippet}");
            }

            var parsedKey = TryParseComparisonKey(body);
            if (string.IsNullOrWhiteSpace(parsedKey))
            {
                return new SlowPicsUploadResult(
                    false,
                    null,
                    null,
                    $"Unexpected response from slow.pics: {(body.Length > 200 ? body[..200] : body)}");
            }

            var url = parsedKey.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? parsedKey
                : $"https://slow.pics/c/{parsedKey}";
            return new SlowPicsUploadResult(true, url, parsedKey, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new SlowPicsUploadResult(false, null, null, ex.Message);
        }
    }

    internal static string? TryParseComparisonKey(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return null;
        }

        var trimmed = responseBody.Trim();
        if (trimmed.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                if (doc.RootElement.TryGetProperty("key", out var keyEl) && keyEl.ValueKind == JsonValueKind.String)
                {
                    return keyEl.GetString()?.Trim();
                }
                if (doc.RootElement.TryGetProperty("url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String)
                {
                    return urlEl.GetString()?.Trim();
                }
            }
            catch
            {
                // Fall through to plain token parsing.
            }
        }

        if (trimmed.StartsWith('"') && trimmed.EndsWith('"') && trimmed.Length > 2)
        {
            trimmed = trimmed[1..^1].Trim();
        }

        if (trimmed.StartsWith("https://slow.pics/", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        if (trimmed.Length is >= 4 and <= 48 && !trimmed.Contains('<') && !trimmed.Contains(' '))
        {
            return trimmed;
        }

        return null;
    }

    private static string? TryExtractXsrfToken(HttpMessageHandler handler, HttpResponseMessage handshake)
    {
        if (handler is HttpClientHandler { CookieContainer: { } cookies })
        {
            var tokenCookie = cookies.GetCookies(new Uri("https://slow.pics/"))["XSRF-TOKEN"];
            if (!string.IsNullOrWhiteSpace(tokenCookie?.Value))
            {
                return Uri.UnescapeDataString(tokenCookie.Value);
            }
        }

        if (handshake.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            foreach (var header in setCookies)
            {
                const string prefix = "XSRF-TOKEN=";
                var idx = header.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
                if (idx < 0)
                {
                    continue;
                }

                var start = idx + prefix.Length;
                var end = header.IndexOf(';', start);
                var raw = end >= 0 ? header[start..end] : header[start..];
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    return Uri.UnescapeDataString(raw.Trim());
                }
            }
        }

        return null;
    }

    private static HttpMessageHandler CreateDefaultHandler()
    {
        return new HttpClientHandler
        {
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
    }
}
