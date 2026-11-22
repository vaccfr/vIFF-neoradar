using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NeoRadar.PluginSdk.Logger;

namespace Viff.NeoRadar.Viff;

/// <summary>
/// Reads the vACC's CDM configuration files (event slots, and later rates and taxi zones) over HTTPS.
/// </summary>
internal interface IRemoteText
{
    /// <summary>
    /// Gets a file's text, or null when it could not be read, already logged.
    /// </summary>
    Task<string?> GetAsync(string fileName, CancellationToken cancellationToken);
}

/// <summary>
/// Reads files under one base URL, by default the French vACC's public vIFF-CDM repository the CDM plugin reads.
/// </summary>
internal sealed class RemoteText : IRemoteText, IDisposable
{
    public const string DefaultBaseUrl = "https://raw.githubusercontent.com/vaccfr/vIFF-CDM/refs/heads/main/";

    private readonly HttpClient _http;
    private readonly ILoggerApi _logger;

    public RemoteText(Uri baseAddress, string userAgent, ILoggerApi logger)
    {
        _logger = logger;
        _http = new HttpClient { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
    }

    public async Task<string?> GetAsync(string fileName, CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await _http.GetAsync(fileName, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.Warning($"Could not read {fileName}: HTTP {(int)response.StatusCode}");
                return null;
            }

            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.Warning($"Could not read {fileName}: {ex.Message}");
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}
