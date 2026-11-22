using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NeoRadar.PluginSdk.Logger;

namespace Viff.NeoRadar.Viff;

/// <summary>
/// Downloads the text files a vACC's CDMconfig.xml points at (rates, taxi zones, event slots, SID intervals).
/// </summary>
internal interface IRemoteText
{
    /// <summary>
    /// Gets a file's text, or null when it could not be read, already logged.
    /// </summary>
    Task<string?> GetAsync(Uri url, CancellationToken cancellationToken);
}

/// <summary>
/// Downloads text over HTTP(S), refusing anything over 4 MB.
/// </summary>
internal sealed class RemoteText : IRemoteText, IDisposable
{
    private const long MaxBytes = 4 * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly ILoggerApi _logger;

    public RemoteText(string userAgent, ILoggerApi logger)
    {
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = MaxBytes };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
    }

    public async Task<string?> GetAsync(Uri url, CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.Warning($"Could not download {url}: HTTP {(int)response.StatusCode}");
                return null;
            }

            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            _logger.Warning($"Could not download {url}: {ex.Message}");
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}
