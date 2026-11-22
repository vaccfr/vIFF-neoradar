using NeoRadar.PluginSdk.Fsd;

namespace Viff.NeoRadar.Common;

/// <summary>
/// Decides whether the client may write to vIFF: only as a connected ATC position on the live network.
/// </summary>
/// <remarks>vIFF holds live-network data, so a sweatbox or observer session must never change it.</remarks>
internal sealed class ConnectionGate
{
    private readonly IFsdApi _fsd;

    public ConnectionGate(IFsdApi fsd)
    {
        _fsd = fsd;
    }

    /// <summary>
    /// Gets the connected callsign, or empty when the client is not connected.
    /// </summary>
    public string Callsign
    {
        get
        {
            ConnectionInfo? connection = _fsd.GetConnection();
            return connection is { IsConnected: true } ? connection.Callsign : string.Empty;
        }
    }

    /// <summary>
    /// Returns whether writes are allowed, with the reason when they are not.
    /// </summary>
    public bool CanWrite(out string callsign, out string reason)
    {
        ConnectionInfo? connection = _fsd.GetConnection();
        callsign = connection?.Callsign ?? string.Empty;
        if (connection is null || !connection.IsConnected)
        {
            reason = "Not connected to the network.";
            return false;
        }

        if (!connection.IsATC)
        {
            reason = "Connect as an ATC position to change CDM data.";
            return false;
        }

        if (connection.ServerType != ServerType.Live)
        {
            reason = "CDM data is only changed on the live network, never from a sweatbox or test server.";
            return false;
        }

        reason = string.Empty;
        return true;
    }
}
