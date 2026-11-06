using System.Security.Cryptography;

namespace KaraokeToMp4.Downloads;

/// <summary>A downloadable file with a known checksum.</summary>
/// <param name="Url">Where to fetch it from (pinned to a fixed version so the checksum stays valid).</param>
/// <param name="Sha256">Expected SHA-256 (hex), or null to skip verification.</param>
/// <param name="SizeBytes">Expected size, used for progress and for telling the user how big it is.</param>
public sealed record DownloadAsset(Uri Url, string? Sha256, long SizeBytes);

/// <summary>Downloads files to disk, verifying their SHA-256 before they are kept.</summary>
internal static class VerifiedDownloader
{
    /// <summary>
    /// Downloads <paramref name="asset"/> to <paramref name="destination"/> via a temporary ".part" file.
    /// </summary>
    /// <param name="onBytes">Called with the number of bytes received so far.</param>
    public static async Task DownloadAsync(
        DownloadAsset asset,
        string destination,
        string displayName,
        Action<long>? onBytes,
        HttpClient? httpClient,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        var partial = destination + ".part";
        var client = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        try
        {
            using var response = await client.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                long read = 0;
                int n;
                while ((n = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
                    sha.AppendData(buffer, 0, n);
                    read += n;
                    onBytes?.Invoke(read);
                }
            }

            var actual = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
            if (asset.Sha256 != null && !string.Equals(actual, asset.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new KaraokeConversionException(
                    $"Downloaded {displayName} failed its checksum (expected {asset.Sha256}, got {actual}).");

            File.Move(partial, destination, overwrite: true);
        }
        catch (HttpRequestException ex)
        {
            throw new KaraokeConversionException($"Could not download {displayName}: {ex.Message}", ex);
        }
        finally
        {
            if (httpClient == null) client.Dispose();
            try { if (File.Exists(partial)) File.Delete(partial); } catch { /* best effort */ }
        }
    }
}
