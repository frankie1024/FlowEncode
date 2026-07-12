using System.Net;
using System.Net.Http.Headers;
using FlowEncode.Application;

namespace FlowEncode.Infrastructure;

internal static class ResumablePackageDownloader
{
    private const int BufferSize = 81920;
    private const int MaximumAttempts = 3;
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);

    public static async Task DownloadAsync(
        HttpClient httpClient,
        string url,
        string destinationPath,
        IProgress<PackageDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        var destinationDirectory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(destinationDirectory))
        {
            Directory.CreateDirectory(destinationDirectory);
        }

        var partialPath = destinationPath + ".part";
        Exception? lastException = null;

        for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            try
            {
                await DownloadAttemptAsync(httpClient, url, partialPath, progress, cancellationToken);
                File.Move(partialPath, destinationPath, overwrite: true);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (IsRetryable(ex))
            {
                lastException = ex;
                if (attempt == MaximumAttempts)
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken);
            }
        }

        throw new InvalidOperationException(
            $"Download failed after {MaximumAttempts} attempts. The partial file was kept at '{partialPath}' for a later retry.",
            lastException);
    }

    private static async Task DownloadAttemptAsync(
        HttpClient httpClient,
        string url,
        string partialPath,
        IProgress<PackageDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var existingLength = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (existingLength > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existingLength, null);
        }

        using var idleTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idleTimeoutCts.CancelAfter(IdleTimeout);
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            idleTimeoutCts.Token);

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && existingLength > 0)
        {
            File.Delete(partialPath);
            throw new HttpRequestException("The partial download could not be resumed.", null, response.StatusCode);
        }

        response.EnsureSuccessStatusCode();

        var append = response.StatusCode == HttpStatusCode.PartialContent && existingLength > 0;
        if (!append)
        {
            existingLength = 0;
        }

        long? totalBytes = response.Content.Headers.ContentLength is long contentLength
            ? existingLength + contentLength
            : null;
        progress?.Report(new PackageDownloadProgress(existingLength, totalBytes));

        await using var source = await response.Content.ReadAsStreamAsync(idleTimeoutCts.Token);
        await using var target = File.Open(
            partialPath,
            append ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None);

        var buffer = new byte[BufferSize];
        var totalRead = existingLength;
        while (true)
        {
            idleTimeoutCts.CancelAfter(IdleTimeout);
            var bytesRead = await source.ReadAsync(buffer, idleTimeoutCts.Token);
            if (bytesRead == 0)
            {
                break;
            }

            await target.WriteAsync(buffer.AsMemory(0, bytesRead), idleTimeoutCts.Token);
            totalRead += bytesRead;
            progress?.Report(new PackageDownloadProgress(totalRead, totalBytes));
        }
    }

    private static bool IsRetryable(Exception exception)
    {
        return exception is HttpRequestException or TimeoutException
            || exception is OperationCanceledException;
    }
}
