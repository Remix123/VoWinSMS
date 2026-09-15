using System.Text;
using System.Text.RegularExpressions;
using VoSharp.Kernel.Pool;
using VoWin.Helpers;
using VoWin.Models;

namespace VoWin.Services.RemoteControl;

internal sealed class RemoteCommandProcessor
{
    private readonly IVoKernelService _kernel;

    public RemoteCommandProcessor(IVoKernelService kernel) => _kernel = kernel;

    public async Task<string> ExecuteAsync(string input, CancellationToken ct)
    {
        var raw = input.Trim();
        if (raw.StartsWith('/')) raw = raw[1..];
        var split = raw.IndexOfAny([' ', '\t', '\r', '\n']);
        var command = (split < 0 ? raw : raw[..split]).Trim().ToLowerInvariant();
        var args = split < 0 ? string.Empty : raw[(split + 1)..].Trim();

        try
        {
            return command switch
            {
                "帮助" or "help" or "菜单" => HelpText,
                "状态" or "status" => BuildStatus(),
                "卡片" or "卡槽" or "slots" => BuildSlots(),
                "切卡" or "switch" => SwitchSlot(args),
                "验证码" or "code" or "otp" => await GetOtpAsync(args).ConfigureAwait(false),
                "短信" or "sms" => await SendSmsAsync(args).ConfigureAwait(false),
                "拨号" or "电话" or "call" => await DialAsync(args).ConfigureAwait(false),
                "挂断" or "hangup" => await HangupAsync().ConfigureAwait(false),
                "接听" or "answer" => await AnswerAsync().ConfigureAwait(false),
                "拒接" or "reject" => await RejectAsync().ConfigureAwait(false),
                "vowifi" or "wifi通话" => await SetVoWifiAsync(args).ConfigureAwait(false),
                "飞行模式" or "flight" => await SetFlightModeAsync(args).ConfigureAwait(false),
                "刷新" or "refresh" => await RefreshAsync().ConfigureAwait(false),
                _ => "⚠️ **未知命令**\n\n发送 `帮助` 查看可用命令。"
            };
        }
        catch (Exception ex)
        {
            return RemoteMessageMarkdown.Error("操作失败", ex.Message);
        }
    }

    private string BuildStatus()
    {
        var slot = _kernel.Kernel.GetActiveSlot();
        var reg = slot?.Registration;
        var signal = slot?.Signal;
        var wifi = slot?.VoWifiDiag;
        var cellular = slot?.IsFlightMode == true
            ? "飞行模式（射频已关闭）"
            : $"{reg?.StatusDisplay ?? "未知"} {reg?.AccessTechnology ?? string.Empty}".Trim();
        var signalText = slot?.IsFlightMode == true
            ? "射频已关闭"
            : signal == null ? "未知" : $"{signal.Bars}/5 · {signal.RssiDbm} dBm · {signal.Rat}";
        var call = _kernel.CurrentCallState.ToString();
        if (!string.IsNullOrWhiteSpace(_kernel.CurrentCallNumber)) call += $" · {_kernel.CurrentCallNumber}";

        var sb = new StringBuilder("## 📡 VoWin 系统状态\n\n");
        sb.AppendLine($"- **卡槽**：{RemoteMessageMarkdown.Escape(slot?.Name ?? "无")} · {RemoteMessageMarkdown.Code(slot?.Id)}");
        sb.AppendLine($"- **模块**：{RemoteMessageMarkdown.Escape(slot?.State.ToString() ?? "离线")} · {RemoteMessageMarkdown.Code(slot?.PortName)}");
        sb.AppendLine($"- **SIM**：{RemoteMessageMarkdown.Code(MaskIccid(slot?.Sim?.Iccid))} · {RemoteMessageMarkdown.Escape(slot?.CarrierName ?? "未知运营商")}");
        sb.AppendLine($"- **蜂窝**：{RemoteMessageMarkdown.Escape(cellular)}");
        sb.AppendLine($"- **信号**：{RemoteMessageMarkdown.Escape(signalText)}");
        sb.AppendLine($"- **VoWiFi**：{RemoteMessageMarkdown.Escape(wifi?.State.ToString() ?? "未启动")}");
        sb.AppendLine($"- **通话**：{RemoteMessageMarkdown.Escape(call)}");
        sb.Append("\n> 发送 `刷新` 获取最新状态");
        return sb.ToString().TrimEnd();
    }

    private string BuildSlots()
    {
        var slots = _kernel.Kernel.GetSlots().ToList();
        if (slots.Count == 0) return "ℹ️ **当前没有可用卡槽**";
        var active = _kernel.Kernel.GetActiveSlot()?.Id;
        var lines = slots.Select((s, i) =>
            $"**{i + 1}\\. {(s.Id == active ? "🟢" : "⚪")} {RemoteMessageMarkdown.Escape(s.Name)}**\n" +
            $"模块 {RemoteMessageMarkdown.Code(s.State.ToString())} · SIM {RemoteMessageMarkdown.Code(MaskIccid(s.Sim?.Iccid))} · VoWiFi {RemoteMessageMarkdown.Code(s.VoWifiDiag?.State.ToString() ?? "未启动")}");
        return "## 💳 卡槽列表\n\n" + string.Join("\n\n", lines) +
               "\n\n> 🟢 表示当前卡槽；发送 `切卡 1` 即可切换";
    }

    private string SwitchSlot(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "ℹ️ **命令用法**\n\n`切卡 <序号/卡槽 ID/ICCID 后四位>`";
        var slots = _kernel.Kernel.GetSlots().ToList();
        ModemSlot? selected = null;
        if (int.TryParse(value, out var index) && index >= 1 && index <= slots.Count) selected = slots[index - 1];
        selected ??= slots.FirstOrDefault(s => s.Id.Equals(value, StringComparison.OrdinalIgnoreCase));
        selected ??= slots.FirstOrDefault(s => s.Sim?.Iccid?.EndsWith(value, StringComparison.OrdinalIgnoreCase) == true);
        if (selected == null) return "⚠️ **未找到该卡槽**\n\n发送 `卡片` 查看可用列表。";
        return _kernel.SelectSlot(selected.Id)
            ? $"✅ **切卡成功**\n\n当前卡槽：{RemoteMessageMarkdown.Escape(selected.Name)} · {RemoteMessageMarkdown.Code(MaskIccid(selected.Sim?.Iccid))}"
            : RemoteMessageMarkdown.Error("切卡失败");
    }

    private async Task<string> GetOtpAsync(string value)
    {
        var count = int.TryParse(value, out var n) ? Math.Clamp(n, 1, 10) : 3;
        var all = await _kernel.Preferences.GetAllSmsMessagesAsync().ConfigureAwait(false);
        var rows = all.Where(x => !x.IsOutgoing && x.HasOtpCode)
            .OrderByDescending(x => x.Timestamp).Take(count).ToList();
        if (rows.Count == 0) return "ℹ️ **暂时没有找到验证码短信**";
        return "## 🔐 最近验证码\n\n" + string.Join("\n\n", rows.Select(x =>
            $"### {RemoteMessageMarkdown.Code(x.ExtractedOtpCode)}\n" +
            $"来自 {RemoteMessageMarkdown.Code(x.SenderOrRecipient)} · {TimestampDisplayHelper.ToLocalDisplayTime(x.Timestamp):MM-dd HH:mm:ss}"));
    }

    private async Task<string> SendSmsAsync(string value)
    {
        var split = value.IndexOfAny([' ', '\t', '\r', '\n']);
        if (split <= 0) return "ℹ️ **命令用法**\n\n`短信 <号码> <内容>`";
        var number = value[..split].Trim();
        var text = value[(split + 1)..].Trim();
        if (!ValidNumber(number) || string.IsNullOrWhiteSpace(text)) return "⚠️ **号码或短信内容无效**";
        if (text.Length > 1000) return "⚠️ **短信内容过长**\n\n最多支持 1000 个字符。";
        var result = await _kernel.SendSmsAsync(number, text).ConfigureAwait(false);
        return result.AllPartsAccepted
            ? $"✅ **短信已提交**\n\n接收号码：{RemoteMessageMarkdown.Code(number)}"
            : RemoteMessageMarkdown.Error("短信发送失败", result.SubmissionStatus);
    }

    private async Task<string> DialAsync(string number)
    {
        if (!ValidNumber(number)) return "ℹ️ **命令用法**\n\n`拨号 <号码>`";
        if (_kernel.CurrentCallState is not (VoSharp.Telephony.Calls.CallState.Idle or VoSharp.Telephony.Calls.CallState.Ended))
            return $"⚠️ **当前已有通话**\n\n状态：{RemoteMessageMarkdown.Code(_kernel.CurrentCallState.ToString())}";
        var call = await _kernel.DialAsync(number).ConfigureAwait(false);
        return $"📞 **正在呼叫**\n\n号码：{RemoteMessageMarkdown.Code(number)}\n\n状态：{RemoteMessageMarkdown.Code(call.State.ToString())}";
    }

    private async Task<string> HangupAsync()
    {
        await _kernel.HangupAsync().ConfigureAwait(false);
        return "✅ **已请求挂断**";
    }

    private async Task<string> AnswerAsync()
    {
        var call = await _kernel.AnswerAsync().ConfigureAwait(false);
        return call == null
            ? "⚠️ **当前没有可接听的来电，或接听失败**"
            : "✅ **已接听**\n\n正在使用默认语音设备。";
    }

    private async Task<string> RejectAsync()
    {
        var call = await _kernel.RejectAsync().ConfigureAwait(false);
        return call == null
            ? "⚠️ **当前没有可拒接的来电，或拒接失败**"
            : "✅ **已拒接来电**";
    }

    private async Task<string> SetVoWifiAsync(string value)
    {
        if (!TryParseSwitch(value, out var enabled)) return "ℹ️ **命令用法**\n\n`VoWiFi 开` 或 `VoWiFi 关`";
        var ok = enabled ? await _kernel.StartVoWifiAsync().ConfigureAwait(false) : await _kernel.StopVoWifiAsync().ConfigureAwait(false);
        return ok
            ? $"✅ **VoWiFi 已{(enabled ? "开启" : "关闭")}**"
            : RemoteMessageMarkdown.Error($"VoWiFi {(enabled ? "启动" : "停止")}失败", "请查看 VoWin 系统日志获取详细原因。");
    }

    private async Task<string> SetFlightModeAsync(string value)
    {
        if (!TryParseSwitch(value, out var enabled)) return "ℹ️ **命令用法**\n\n`飞行模式 开` 或 `飞行模式 关`";
        var ok = await _kernel.SetFlightModeAsync(enabled).ConfigureAwait(false);
        return ok
            ? $"✅ **飞行模式已{(enabled ? "开启" : "关闭")}**"
            : RemoteMessageMarkdown.Error("飞行模式切换失败");
    }

    private async Task<string> RefreshAsync()
    {
        await _kernel.RefreshMetricsAsync().ConfigureAwait(false);
        return BuildStatus();
    }

    private static bool TryParseSwitch(string value, out bool enabled)
    {
        var normalized = value.Trim().ToLowerInvariant();
        enabled = normalized is "开" or "开启" or "on" or "1" or "enable";
        return enabled || normalized is "关" or "关闭" or "off" or "0" or "disable";
    }

    private static bool ValidNumber(string value) => Regex.IsMatch(value, @"^\+?[0-9*#]{3,32}$");
    private static string MaskIccid(string? iccid) => string.IsNullOrWhiteSpace(iccid) ? "无 SIM" : $"****{iccid[^Math.Min(4, iccid.Length)..]}";

    private const string HelpText = """
        ## 🤖 VoWin 远程控制

        **状态与卡槽**

        - `状态`　查看模块、网络、VoWiFi 和通话状态
        - `刷新`　刷新并查看最新状态
        - `卡片`　查看所有卡槽
        - `切卡 1`　按序号、卡槽 ID 或 ICCID 后四位切卡

        **短信与通话**

        - `验证码`　查看最近 3 条验证码
        - `验证码 5`　指定查看数量
        - `短信 <号码> <内容>`　发送短信
        - `拨号 <号码>`　使用当前卡拨号
        - `接听` / `拒接` / `挂断`　控制当前通话

        **连接开关**

        - `VoWiFi 开` / `VoWiFi 关`
        - `飞行模式 开` / `飞行模式 关`

        > 命令不区分中英文大小写
        """;
}
