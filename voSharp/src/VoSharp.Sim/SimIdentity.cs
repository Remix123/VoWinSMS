namespace VoSharp.Sim;

public record SimIdentity(
    string Imsi,
    string Iccid,
    string Mcc,
    string Mnc,
    string OperatorName,
    string? PhoneNumber = null,
    bool IsHomePlmnAuthoritative = false,
    IReadOnlyList<string>? HomePlmns = null,
    string? ReportedImsi = null,
    string? PermanentImsi = null,
    string ImsiSource = "unknown",
    bool HasPlmnConflict = false
)
{
    public static SimIdentity FromImsiAndIccid(
        string imsi,
        string iccid,
        string? opName = null,
        string? phoneNumber = null,
        int? mncLength = null,
        IReadOnlyList<string>? homePlmns = null,
        string? reportedImsi = null,
        string? permanentImsi = null,
        string? imsiSource = null,
        bool hasPlmnConflict = false)
    {
        imsi = imsi.Trim();
        iccid = iccid.Trim();
        var mcc = imsi.Length >= 3 ? imsi[..3] : "";
        var validatedMncLength = mncLength is 2 or 3 ? mncLength.Value : 2;
        var mnc = imsi.Length >= 3 + validatedMncLength
            ? imsi.Substring(3, validatedMncLength)
            : "";
        var operatorName = SimOperatorCatalog.Resolve(mcc, mnc, opName);
        return new SimIdentity(
            imsi,
            iccid,
            mcc,
            mnc,
            operatorName,
            phoneNumber,
            IsHomePlmnAuthoritative: mncLength is 2 or 3,
            HomePlmns: homePlmns?.Distinct(StringComparer.Ordinal).ToArray(),
            ReportedImsi: string.IsNullOrWhiteSpace(reportedImsi) ? null : reportedImsi.Trim(),
            PermanentImsi: string.IsNullOrWhiteSpace(permanentImsi) ? null : permanentImsi.Trim(),
            ImsiSource: string.IsNullOrWhiteSpace(imsiSource) ? "unknown" : imsiSource.Trim(),
            HasPlmnConflict: hasPlmnConflict);
    }
}
