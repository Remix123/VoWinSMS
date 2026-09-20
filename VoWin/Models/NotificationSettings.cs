namespace VoWin.Models;

/// <summary>
/// Settings for the one-way notification channels. Secrets are encrypted by
/// NotificationSettingsStore before they are written to disk.
/// </summary>
public sealed class NotificationSettings
{
    public bool NotifyIncomingSms { get; set; } = true;
    public bool NotifyOtpOnly { get; set; }
    public bool NotifyIncomingCalls { get; set; } = true;

    public TelegramNotificationSettings Telegram { get; set; } = new();
    public BarkNotificationSettings Bark { get; set; } = new();
    public PushplusNotificationSettings Pushplus { get; set; } = new();
    public WebhookNotificationSettings Webhook { get; set; } = new();
    public WecomNotificationSettings Wecom { get; set; } = new();
    public LarkNotificationSettings Lark { get; set; } = new();

    public NotificationSettings Clone() => new()
    {
        NotifyIncomingSms = NotifyIncomingSms,
        NotifyOtpOnly = NotifyOtpOnly,
        NotifyIncomingCalls = NotifyIncomingCalls,
        Telegram = Telegram.Clone(),
        Bark = Bark.Clone(),
        Pushplus = Pushplus.Clone(),
        Webhook = Webhook.Clone(),
        Wecom = Wecom.Clone(),
        Lark = Lark.Clone()
    };
}

public sealed class TelegramNotificationSettings
{
    public bool Enabled { get; set; }
    public string BotToken { get; set; } = string.Empty;
    public string ChatId { get; set; } = string.Empty;
    public string ApiBaseUrl { get; set; } = "https://api.telegram.org";

    public TelegramNotificationSettings Clone() => new()
    {
        Enabled = Enabled,
        BotToken = BotToken,
        ChatId = ChatId,
        ApiBaseUrl = ApiBaseUrl
    };
}

public sealed class BarkNotificationSettings
{
    public bool Enabled { get; set; }
    public string Urls { get; set; } = string.Empty;
    public string Group { get; set; } = "vowin";
    public string Level { get; set; } = "active";
    public string Icon { get; set; } = string.Empty;

    public BarkNotificationSettings Clone() => new()
    {
        Enabled = Enabled,
        Urls = Urls,
        Group = Group,
        Level = Level,
        Icon = Icon
    };
}

public sealed class PushplusNotificationSettings
{
    public bool Enabled { get; set; }
    public string Token { get; set; } = string.Empty;
    public string Topic { get; set; } = string.Empty;

    public PushplusNotificationSettings Clone() => new()
    {
        Enabled = Enabled,
        Token = Token,
        Topic = Topic
    };
}

public sealed class WebhookNotificationSettings
{
    public bool Enabled { get; set; }
    public string Urls { get; set; } = string.Empty;
    public string Secret { get; set; } = string.Empty;
    public string TextTemplate { get; set; } = string.Empty;

    public WebhookNotificationSettings Clone() => new()
    {
        Enabled = Enabled,
        Urls = Urls,
        Secret = Secret,
        TextTemplate = TextTemplate
    };
}

public sealed class WecomNotificationSettings
{
    public bool Enabled { get; set; }
    public string Urls { get; set; } = string.Empty;

    public WecomNotificationSettings Clone() => new()
    {
        Enabled = Enabled,
        Urls = Urls
    };
}

public sealed class LarkNotificationSettings
{
    public bool Enabled { get; set; }
    public string Url { get; set; } = string.Empty;
    public bool SigningEnabled { get; set; }
    public string Secret { get; set; } = string.Empty;

    public LarkNotificationSettings Clone() => new()
    {
        Enabled = Enabled,
        Url = Url,
        SigningEnabled = SigningEnabled,
        Secret = Secret
    };
}
