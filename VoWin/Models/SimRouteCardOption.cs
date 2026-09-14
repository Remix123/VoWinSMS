namespace VoWin.Models;

/// <summary>
/// A live SIM identity discovered from one modem. The selector is rebuilt when
/// a slot changes cards so selection follows ICCID rather than the slot object.
/// </summary>
public sealed record SimRouteCardOption(
    string SlotId,
    string SlotName,
    string Iccid,
    string? Imsi,
    string CardName,
    string PhoneNumber,
    string OperatorName)
{
    public string CardAndNumberDisplay => $"{CardName} · {PhoneNumber}";
    public string IdentityDisplay => $"ICCID {Iccid} · {SlotName}";
    public string NetworkDisplay => string.IsNullOrWhiteSpace(Imsi)
        ? OperatorName
        : $"{OperatorName} · IMSI {Imsi}";
}
