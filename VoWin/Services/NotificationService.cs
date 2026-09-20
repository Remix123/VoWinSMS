using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using VoWin.Models;

namespace VoWin.Services;

/// <summary>
/// Sends inbound SMS and call events to the configured one-way notification
/// channels. Network failures are isolated per channel and never run on the
/// modem event thread.
/// </summary>
public sealed class NotificationService : INotificationService, IHostedService
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(8)
    };

    private readonly IVoKernelService _kernel;
    private readonly NotificationSettingsStore _store = new();
    private NotificationSettings _settings = new();
    private int _loaded;

    public NotificationService(IVoKernelService kernel)
    {
        _kernel = kernel;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        _kernel.IncomingSmsReceived += OnIncomingSms;
        _kernel.IncomingCallReceived += OnIncomingCall;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _kernel.IncomingSmsReceived -= OnIncomingSms;
        _kernel.IncomingCallReceived -= OnIncomingCall;
        return Task.CompletedTask;
    }

    public async Task<NotificationSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        return _settings.Clone();
    }

    public async Task SaveSettingsAsync(NotificationSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var copy = settings.Clone();
        await _store.SaveAsync(copy, cancellationToken).ConfigureAwait(false);
        _settings = copy;
        Interlocked.Exchange(ref _loaded, 1);
    }

    public async Task<NotificationSendResult> SendTestAsync(
        string channel,
        NotificationSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var message = new NotificationEvent(
            "notification.test",
            "VoWin 通知测试",
            "VoWin 通知测试\n时间  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\n消息  如果你看到这条消息，通知渠道配置成功。",
            "如果你看到这条消息，通知渠道配置成功。",
            DateTime.UtcNow,
            "测试",
            string.Empty,
            string.Empty,
            string.Empty,
            "测试");

        try
        {
            await SendChannelAsync(channel, settings, message, cancellationToken).ConfigureAwait(false);
            return new NotificationSendResult(true, $"{ChannelDisplayName(channel)} 测试通知已发送。");
        }
        catch (Exception ex)
        {
            return new NotificationSendResult(false, $"{ChannelDisplayName(channel)} 测试失败：{ex.Message}");
        }
    }

    private void OnIncomingSms(SmsMessageModel sms) => _ = DispatchSmsAsync(sms);

    private void OnIncomingCall(string number, string? slotId) => _ = DispatchCallAsync(number, slotId);

    private async Task DispatchSmsAsync(SmsMessageModel sms)
    {
        try
        {
            var settings = _settings.Clone();
            if (!settings.NotifyIncomingSms || (settings.NotifyOtpOnly && !sms.HasOtpCode)) return;

            var slot = ResolveSlotLabel(sms.SlotId);
            var timestamp = EnsureUtc(sms.Timestamp);
            var text = string.Join('\n', new[]
            {
                "收到新短信",
                $"设备  {slot}",
                $"号码  {Fallback(sms.SenderOrRecipient, "未知号码")}",
                $"时间  {timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss}",
                $"内容  {sms.Text}"
            });
            var message = new NotificationEvent(
                "sms.received",
                sms.HasOtpCode ? "收到验证码短信" : "收到新短信",
                text,
                sms.Text,
                timestamp,
                slot,
                sms.SlotId ?? string.Empty,
                Fallback(sms.SenderOrRecipient, "未知号码"),
                sms.Text,
                slot);
            await DispatchToEnabledChannelsAsync(settings, message).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"VoWin SMS notification dispatch failed: {ex}");
        }
    }

    private async Task DispatchCallAsync(string number, string? slotId)
    {
        try
        {
            var settings = _settings.Clone();
            if (!settings.NotifyIncomingCalls) return;

            var slot = ResolveSlotLabel(slotId);
            var timestamp = DateTime.UtcNow;
            var caller = Fallback(number, "未知号码");
            var text = string.Join('\n', new[]
            {
                "📞 收到来电",
                $"设备  {slot}",
                $"来电号码  {caller}",
                $"时间  {timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
            });
            var message = new NotificationEvent(
                "call.received",
                "收到来电",
                text,
                text,
                timestamp,
                slot,
                slotId ?? string.Empty,
                caller,
                string.Empty,
                slot);
            await DispatchToEnabledChannelsAsync(settings, message).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"VoWin call notification dispatch failed: {ex}");
        }
    }

    private async Task DispatchToEnabledChannelsAsync(NotificationSettings settings, NotificationEvent message)
    {
        var channels = new List<(string Name, bool Enabled)>
        {
            ("telegram", settings.Telegram.Enabled),
            ("bark", settings.Bark.Enabled),
            ("pushplus", settings.Pushplus.Enabled),
            ("webhook", settings.Webhook.Enabled),
            ("wecom", settings.Wecom.Enabled),
            ("lark", settings.Lark.Enabled)
        };

        var tasks = channels
            .Where(channel => channel.Enabled)
            .Select(channel => SendSafelyAsync(channel.Name, settings, message))
            .ToArray();
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task SendSafelyAsync(string channel, NotificationSettings settings, NotificationEvent message)
    {
        try
        {
            await SendChannelAsync(channel, settings, message, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A bad webhook must not prevent a second channel from receiving
            // the same SMS/call. Keep the failure out of the user-facing UI.
            Debug.WriteLine($"VoWin {channel} notification failed: {ex.Message}");
        }
    }

    private async Task SendChannelAsync(
        string channel,
        NotificationSettings settings,
        NotificationEvent message,
        CancellationToken cancellationToken)
    {
        switch (channel.ToLowerInvariant())
        {
            case "telegram":
                await SendTelegramAsync(settings.Telegram, message, cancellationToken).ConfigureAwait(false);
                break;
            case "bark":
                await SendBarkAsync(settings.Bark, message, cancellationToken).ConfigureAwait(false);
                break;
            case "pushplus":
                await SendPushplusAsync(settings.Pushplus, message, cancellationToken).ConfigureAwait(false);
                break;
            case "webhook":
                await SendWebhookAsync(settings.Webhook, message, cancellationToken).ConfigureAwait(false);
                break;
            case "wecom":
                await SendWecomAsync(settings.Wecom, message, cancellationToken).ConfigureAwait(false);
                break;
            case "lark":
                await SendLarkAsync(settings.Lark, message, cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new InvalidOperationException($"不支持的通知渠道：{channel}");
        }
    }

    private static async Task SendTelegramAsync(
        TelegramNotificationSettings settings,
        NotificationEvent message,
        CancellationToken cancellationToken)
    {
        Require(settings.BotToken, "Telegram Bot Token");
        Require(settings.ChatId, "Telegram Chat ID");
        var baseUrl = string.IsNullOrWhiteSpace(settings.ApiBaseUrl) ? "https://api.telegram.org" : settings.ApiBaseUrl.TrimEnd('/');
        var url = $"{baseUrl}/bot{Uri.EscapeDataString(settings.BotToken.Trim())}/sendMessage";
        await PostJsonAsync(url, new { chat_id = settings.ChatId.Trim(), text = message.Text }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task SendBarkAsync(
        BarkNotificationSettings settings,
        NotificationEvent message,
        CancellationToken cancellationToken)
    {
        var urls = RequireUrls(settings.Urls, "Bark URL");
        foreach (var url in urls)
        {
            var payload = new Dictionary<string, object?>
            {
                ["title"] = message.Title,
                ["body"] = message.DetailText,
                ["level"] = string.IsNullOrWhiteSpace(settings.Level) ? "active" : settings.Level.Trim()
            };
            if (!string.IsNullOrWhiteSpace(settings.Group)) payload["group"] = settings.Group.Trim();
            if (!string.IsNullOrWhiteSpace(settings.Icon)) payload["icon"] = settings.Icon.Trim();
            await PostJsonAsync(url, payload, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task SendPushplusAsync(
        PushplusNotificationSettings settings,
        NotificationEvent message,
        CancellationToken cancellationToken)
    {
        Require(settings.Token, "Pushplus Token");
        var payload = new Dictionary<string, object?>
        {
            ["token"] = settings.Token.Trim(),
            ["title"] = message.Title,
            ["content"] = message.Text,
            ["template"] = "html"
        };
        if (!string.IsNullOrWhiteSpace(settings.Topic)) payload["topic"] = settings.Topic.Trim();
        await PostJsonAsync("https://www.pushplus.plus/send", payload, cancellationToken).ConfigureAwait(false);
    }

    private static async Task SendWebhookAsync(
        WebhookNotificationSettings settings,
        NotificationEvent message,
        CancellationToken cancellationToken)
    {
        var urls = RequireUrls(settings.Urls, "Webhook URL");
        var rendered = RenderWebhookTemplate(settings.TextTemplate, message);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            @event = message.Event,
            title = message.Title,
            message = rendered,
            timestamp = message.Timestamp.ToUniversalTime().ToString("O"),
            slot_id = message.SlotId,
            slot_name = message.SlotName,
            number = message.Number,
            content = message.Content
        });

        foreach (var url in urls)
        {
            using var request = CreateJsonRequest(url, payload);
            if (!string.IsNullOrWhiteSpace(settings.Secret))
            {
                using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(settings.Secret));
                request.Headers.TryAddWithoutValidation("X-VoWin-Signature", "sha256=" + Convert.ToHexString(hmac.ComputeHash(payload)).ToLowerInvariant());
            }
            await SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task SendWecomAsync(
        WecomNotificationSettings settings,
        NotificationEvent message,
        CancellationToken cancellationToken)
    {
        var urls = RequireUrls(settings.Urls, "企业微信 Webhook URL");
        var content = message.Text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);
        foreach (var url in urls)
        {
            await PostJsonAsync(url, new { msgtype = "markdown", markdown = new { content } }, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task SendLarkAsync(
        LarkNotificationSettings settings,
        NotificationEvent message,
        CancellationToken cancellationToken)
    {
        Require(settings.Url, "飞书/Lark Webhook URL");
        var payload = new Dictionary<string, object?>
        {
            ["msg_type"] = "text",
            ["content"] = new { text = message.Text }
        };
        if (settings.SigningEnabled)
        {
            Require(settings.Secret, "飞书/Lark 签名密钥");
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes($"{timestamp}\n{settings.Secret}"));
            payload["timestamp"] = timestamp;
            payload["sign"] = Convert.ToBase64String(hmac.ComputeHash(Array.Empty<byte>()));
        }
        await PostJsonAsync(settings.Url.Trim(), payload, cancellationToken).ConfigureAwait(false);
    }

    private static async Task PostJsonAsync(string url, object payload, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        using var request = CreateJsonRequest(url, bytes);
        await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static HttpRequestMessage CreateJsonRequest(string rawUrl, byte[] payload)
    {
        if (!Uri.TryCreate(rawUrl.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException($"通知 URL 无效：{rawUrl}");

        var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new ByteArrayContent(payload)
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        request.Headers.UserAgent.ParseAdd("VoWin-notification/1.0");
        return request;
    }

    private static async Task SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode) return;

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (body.Length > 240) body = body[..240];
        throw new HttpRequestException($"HTTP {(int)response.StatusCode}：{body}");
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _loaded) == 1) return;
        var loaded = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        _settings = loaded;
        Interlocked.Exchange(ref _loaded, 1);
    }

    private string ResolveSlotLabel(string? slotId)
    {
        if (!string.IsNullOrWhiteSpace(slotId))
        {
            var slot = _kernel.Slots.FirstOrDefault(item => item.Id.Equals(slotId, StringComparison.OrdinalIgnoreCase));
            if (slot != null && !string.IsNullOrWhiteSpace(slot.Name)) return slot.Name;
        }
        return _kernel.ActiveSlot?.Name ?? "当前 SIM";
    }

    private static string RenderWebhookTemplate(string template, NotificationEvent message)
    {
        var rendered = string.IsNullOrWhiteSpace(template) ? message.Text : template;
        var replacements = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["{{text}}"] = message.Text,
            ["{{event}}"] = message.Event,
            ["{{title}}"] = message.Title,
            ["{{timestamp}}"] = message.Timestamp.ToUniversalTime().ToString("O"),
            ["{{time}}"] = message.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
            ["{{slot_id}}"] = message.SlotId,
            ["{{slot_name}}"] = message.SlotName,
            ["{{number}}"] = message.Number,
            ["{{content}}"] = message.Content
        };
        foreach (var (placeholder, value) in replacements) rendered = rendered.Replace(placeholder, value, StringComparison.Ordinal);
        return rendered;
    }

    private static List<string> RequireUrls(string value, string label)
    {
        var urls = value.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToList();
        if (urls.Count == 0) throw new InvalidOperationException($"请填写 {label}。");
        return urls;
    }

    private static void Require(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException($"请填写 {label}。");
    }

    private static string Fallback(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static DateTime EnsureUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    private static string ChannelDisplayName(string channel) => channel.ToLowerInvariant() switch
    {
        "telegram" => "Telegram",
        "bark" => "Bark",
        "pushplus" => "Pushplus",
        "webhook" => "Webhook",
        "wecom" => "企业微信",
        "lark" => "飞书/Lark",
        _ => channel
    };

    private sealed record NotificationEvent(
        string Event,
        string Title,
        string Text,
        string DetailText,
        DateTime Timestamp,
        string SlotName,
        string SlotId,
        string Number,
        string Content,
        string DeviceLabel);
}
