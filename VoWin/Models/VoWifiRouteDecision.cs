namespace VoWin.Models;

/// <summary>
/// The route that will be used when a slot starts VoWiFi.  This intentionally
/// describes the selector as well as the endpoint, so an IMSI home PLMN is not
/// mistaken for the retail country of a SIM profile.
/// </summary>
public sealed record VoWifiRouteDecision(
    string RuleDisplay,
    string TargetDisplay,
    string Detail,
    string? ProxyUrl,
    bool UsesProxy);
