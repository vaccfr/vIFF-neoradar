using NeoRadar.PluginSdk.Chat;
using NeoRadar.PluginSdk.Feedback;

namespace Viff.NeoRadar.Common;

/// <summary>
/// Tells the controller what happened: a line in the plugin's own chat channel, or a toast.
/// </summary>
internal sealed class Notifier
{
    public const string Channel = "vIFF";

    private readonly IChatApi _chat;
    private readonly IFeedbackApi _feedback;

    public Notifier(IChatApi chat, IFeedbackApi feedback)
    {
        _chat = chat;
        _feedback = feedback;
    }

    /// <summary>
    /// Posts a line the controller can read again in the vIFF chat channel.
    /// </summary>
    public void Say(string message, bool urgent = false) =>
        _chat.SendClientMessage(new ClientTextMessageEvent("vIFF", message, IsUrgent: urgent, Channel: Channel));

    /// <summary>
    /// Shows a toast the controller may safely miss.
    /// </summary>
    public void Toast(string message, FeedbackKind kind = FeedbackKind.Info) => _feedback.Show(message, kind, 3000);
}
