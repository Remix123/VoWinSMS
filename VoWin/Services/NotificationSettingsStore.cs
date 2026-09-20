using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VoWin.Models;

namespace VoWin.Services;

internal sealed class NotificationSettingsStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("VoWin.Notifications.v1");
    private readonly string _path;

    public NotificationSettingsStore()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoWin");
        Directory.CreateDirectory(folder);
        _path = Path.Combine(folder, "notifications.json");
    }

    public async Task<NotificationSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path)) return new NotificationSettings();
        try
        {
            await using var stream = File.OpenRead(_path);
            var envelope = await JsonSerializer.DeserializeAsync<Envelope>(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(envelope?.ProtectedPayload)) return new NotificationSettings();

            var json = Unprotect(envelope.ProtectedPayload);
            return JsonSerializer.Deserialize<NotificationSettings>(json) ?? new NotificationSettings();
        }
        catch
        {
            return new NotificationSettings();
        }
    }

    public async Task SaveAsync(NotificationSettings value, CancellationToken cancellationToken = default)
    {
        var folder = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(folder);
        var json = JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = false });
        var envelope = new Envelope { Version = 1, ProtectedPayload = Protect(json) };
        var temporaryPath = _path + ".tmp";

        await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true))
        {
            await JsonSerializer.SerializeAsync(stream, envelope, cancellationToken: cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporaryPath, _path, true);
    }

    private static string Protect(string value)
    {
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    private static string Unprotect(string value)
    {
        var bytes = ProtectedData.Unprotect(Convert.FromBase64String(value), Entropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(bytes);
    }

    private sealed class Envelope
    {
        public int Version { get; set; }
        public string ProtectedPayload { get; set; } = string.Empty;
    }
}
