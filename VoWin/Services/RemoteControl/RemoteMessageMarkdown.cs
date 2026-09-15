using System.Text;

namespace VoWin.Services.RemoteControl;

/// <summary>
/// Small, deliberately conservative Markdown helper for remote-control messages.
/// </summary>
internal static class RemoteMessageMarkdown
{
    private const string MarkdownPunctuation = @"\`*_{}[]()#+-.!|>";

    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var result = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            if (MarkdownPunctuation.IndexOf(character) >= 0)
                result.Append('\\');
            result.Append(character);
        }

        return result.ToString();
    }

    public static string Code(string? value)
    {
        var flattened = (value ?? "-")
            .Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace('`', '\u02cb');
        return $"`{flattened}`";
    }

    public static string Quote(string? value)
    {
        var lines = Escape(value).Split('\n');
        return string.Join("\n", lines.Select(line => $"> {line}"));
    }

    public static string Error(string title, string? detail = null)
        => string.IsNullOrWhiteSpace(detail)
            ? $"❌ **{Escape(title)}**"
            : $"❌ **{Escape(title)}**\n\n{Quote(detail)}";
}
