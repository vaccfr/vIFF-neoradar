using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NeoRadar.PluginSdk.Logger;

namespace Viff.NeoRadar.Viff;

/// <summary>
/// Provides the vIFF endpoints the plugin uses. Reads return null and writes false on any failure, already logged.
/// </summary>
internal interface IViffApi
{
    Task<IReadOnlyList<DepartureRecord>?> GetDeparturesAsync(string icao, CancellationToken cancellationToken);

    Task<AirportCapacity?> GetCapacityAsync(string icao, CancellationToken cancellationToken);

    /// <summary>
    /// Gets every flight on the network that holds a CTOT.
    /// </summary>
    Task<IReadOnlyList<RestrictedFlight>?> GetRestrictedAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<MasterAirport>?> GetMastersAsync(CancellationToken cancellationToken);

    Task<bool> SetMasterAsync(string icao, string position, CancellationToken cancellationToken);

    Task<bool> RemoveMasterAsync(string icao, string position, CancellationToken cancellationToken);

    Task<bool> SetCdmDataAsync(CdmDataUpdate update, CancellationToken cancellationToken);

    /// <summary>
    /// Sends a departure planning information message, such as "REA/1" or "OBT/1230/12".
    /// </summary>
    Task<bool> SendDpiAsync(string callsign, string value, CancellationToken cancellationToken);

    /// <summary>
    /// Gets a value indicating whether vIFF refused the API key on the last write (HTTP 401), so no write can succeed.
    /// </summary>
    bool KeyRejected { get; }
}

/// <summary>
/// Talks to the vIFF REST API over HTTPS.
/// </summary>
internal sealed class ViffApiClient : IViffApi, IDisposable
{
    private readonly HttpClient _http;
    private readonly ILoggerApi _logger;
    private volatile string _apiKey;
    private volatile bool _keyRejected;

    /// <param name="handler">The HTTP handler, or null for the default; tests pass their own.</param>
    public ViffApiClient(Uri baseAddress, string apiKey, string userAgent, ILoggerApi logger, HttpMessageHandler? handler = null)
    {
        _logger = logger;
        _apiKey = apiKey;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = baseAddress;
        _http.Timeout = TimeSpan.FromSeconds(10);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
    }

    public bool KeyRejected => _keyRejected;

    /// <summary>
    /// Replaces the API key sent from the next request on.
    /// </summary>
    public void SetApiKey(string apiKey)
    {
        _apiKey = apiKey;
        _keyRejected = false;
    }

    public async Task<IReadOnlyList<DepartureRecord>?> GetDeparturesAsync(string icao, CancellationToken cancellationToken)
    {
        string? body = await SendAsync(HttpMethod.Get, $"ifps/depAirport?airport={Escape(icao)}", cancellationToken).ConfigureAwait(false);
        return body is null ? null : Parsed(ViffJson.ParseDepartures(body), $"departures of {icao}");
    }

    public async Task<AirportCapacity?> GetCapacityAsync(string icao, CancellationToken cancellationToken)
    {
        string? body = await SendAsync(HttpMethod.Get, $"etfms/getCadAirport?icao={Escape(icao)}", cancellationToken).ConfigureAwait(false);
        return body is null ? null : ViffJson.ParseCapacity(body);
    }

    public async Task<IReadOnlyList<RestrictedFlight>?> GetRestrictedAsync(CancellationToken cancellationToken)
    {
        string? body = await SendAsync(HttpMethod.Get, "etfms/restricted", cancellationToken).ConfigureAwait(false);
        return body is null ? null : Parsed(ViffJson.ParseRestricted(body), "regulated flights");
    }

    public async Task<IReadOnlyList<MasterAirport>?> GetMastersAsync(CancellationToken cancellationToken)
    {
        string? body = await SendAsync(HttpMethod.Get, "airport", cancellationToken).ConfigureAwait(false);
        return body is null ? null : Parsed(ViffJson.ParseMasters(body), "master airports");
    }

    public Task<bool> SetMasterAsync(string icao, string position, CancellationToken cancellationToken) =>
        PostForTrueAsync($"airport/setMaster?airport={Escape(icao)}&position={Escape(position)}", cancellationToken);

    public Task<bool> RemoveMasterAsync(string icao, string position, CancellationToken cancellationToken) =>
        PostForTrueAsync($"airport/removeMaster?airport={Escape(icao)}&position={Escape(position)}", cancellationToken);

    public Task<bool> SetCdmDataAsync(CdmDataUpdate update, CancellationToken cancellationToken) =>
        PostForTrueAsync(
            "ifps/setCdmData" +
            $"?callsign={Escape(update.Callsign)}" +
            $"&tobt={Escape(update.Tobt)}" +
            $"&tsat={Escape(update.Tsat)}" +
            $"&ttot={Escape(update.Ttot)}" +
            $"&ctot={Escape(update.Ctot)}" +
            $"&reason={Escape(update.Reason)}" +
            $"&asrt={Escape(update.Asrt)}" +
            $"&depInfo={Escape(update.DepInfo)}" +
            $"&deice={Escape(update.Deice)}",
            cancellationToken);

    public async Task<bool> SendDpiAsync(string callsign, string value, CancellationToken cancellationToken)
    {
        string? body = await SendAsync(HttpMethod.Post, $"ifps/dpi?callsign={Escape(callsign)}&value={Escape(value)}", cancellationToken)
            .ConfigureAwait(false);
        return body is not null;
    }

    public void Dispose() => _http.Dispose();

    private async Task<bool> PostForTrueAsync(string path, CancellationToken cancellationToken)
    {
        string? body = await SendAsync(HttpMethod.Post, path, cancellationToken).ConfigureAwait(false);
        if (body is null)
        {
            return false;
        }

        if (!ViffJson.IsTrue(body))
        {
            _logger.Warning($"vIFF refused POST {Redact(path)}: {Truncate(body)}");
            return false;
        }

        return true;
    }

    private async Task<string?> SendAsync(HttpMethod method, string path, CancellationToken cancellationToken)
    {
        try
        {
            using HttpRequestMessage request = new(method, path);
            request.Headers.Add("x-api-key", _apiKey);
            if (method == HttpMethod.Post)
            {
                request.Content = new StringContent(string.Empty);
            }

            using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (method == HttpMethod.Post)
            {
                _keyRejected = response.StatusCode == System.Net.HttpStatusCode.Unauthorized;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.Warning($"vIFF {method} {Redact(path)} returned {(int)response.StatusCode}: {Truncate(body)}");
                return null;
            }

            return body;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.Warning($"vIFF {method} {Redact(path)} failed: {ex.Message}");
            return null;
        }
    }

    private T? Parsed<T>(T? value, string what)
        where T : class
    {
        if (value is null)
        {
            _logger.Warning($"vIFF sent an unreadable list of {what}");
        }

        return value;
    }

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private static string Redact(string path)
    {
        int query = path.IndexOf('?', StringComparison.Ordinal);
        return query < 0 ? path : path[..query];
    }

    private static string Truncate(string text) => text.Length <= 200 ? text : text[..200] + "...";
}
