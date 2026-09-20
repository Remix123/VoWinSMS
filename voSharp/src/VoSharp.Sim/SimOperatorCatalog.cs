namespace VoSharp.Sim;

/// <summary>
/// Small offline catalog for SIM-facing operator names. The PLMN remains the
/// authoritative network identity; this only improves the human-readable label.
/// </summary>
public static class SimOperatorCatalog
{
    private const string ChinaBroadnet = "中国广电（China Broadnet）";

    public static string Resolve(string mcc, string mnc, string? reportedName = null)
    {
        var normalizedMcc = mcc?.Trim() ?? string.Empty;
        var normalizedMnc = mnc?.Trim() ?? string.Empty;

        if (string.Equals(normalizedMcc, "460", StringComparison.Ordinal) &&
            string.Equals(normalizedMnc, "15", StringComparison.Ordinal))
        {
            return ChinaBroadnet;
        }

        if (!string.IsNullOrWhiteSpace(reportedName))
            return reportedName.Trim();

        return !string.IsNullOrWhiteSpace(normalizedMcc)
            ? $"PLMN {normalizedMcc}-{normalizedMnc}"
            : "未知运营商";
    }
}
