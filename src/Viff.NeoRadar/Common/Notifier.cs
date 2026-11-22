using NeoRadar.PluginSdk.Chat;
using NeoRadar.PluginSdk.Feedback;
using NeoRadar.PluginSdk.Logger;

namespace Viff.NeoRadar.Common;

/// <summary>
/// Tells the controller what happened: a line in the plugin's own chat channel, or a toast. Each is logged too, so the
/// log tells what the controller saw.
/// </summary>
internal sealed class Notifier
{
    public const string Channel = "vIFF";

    private readonly IChatApi _chat;
    private readonly IFeedbackApi _feedback;
    private readonly ILoggerApi? _logger;
    private volatile bool _muted;

    public Notifier(IChatApi chat, IFeedbackApi feedback, ILoggerApi? logger = null)
    {
        _chat = chat;
        _feedback = feedback;
        _logger = logger;
    }

    /// <summary>
    /// Stops every further message, from the start of shutdown: background work still finishing must not call the client.
    /// </summary>
    public void Mute() => _muted = true;

    /// <summary>
    /// Posts a line the controller can read again in the vIFF chat channel.
    /// </summary>
    public void Say(string message, bool urgent = false)
    {
        if (!_muted)
        {
            if (urgent)
            {
                _logger?.Warning($"Chat: {message}");
            }
            else
            {
                _logger?.Info($"Chat: {message}");
            }

            _chat.SendClientMessage(new ClientTextMessageEvent("vIFF", message, IsUrgent: urgent, Channel: Channel));
        }
    }

    /// <summary>
    /// Shows a toast the controller may safely miss.
    /// </summary>
    public void Toast(string message, FeedbackKind kind = FeedbackKind.Info)
    {
        if (!_muted)
        {
            if (kind is FeedbackKind.Warning or FeedbackKind.Error)
            {
                _logger?.Warning($"Toast: {message}");
            }
            else
            {
                _logger?.Info($"Toast: {message}");
            }

            _feedback.Show(message, kind, 3000);
        }
    }
}
