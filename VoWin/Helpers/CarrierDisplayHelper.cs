using VoSharp.Sim;

namespace VoWin.Helpers;

/// <summary>
/// Chooses user-facing labels from live SIM metadata. No bundled carrier profile
/// or ICCID mapping participates in display or network identity selection.
/// </summary>
public static class CarrierDisplayHelper
{
    public static string GetOperatorDisplay(SimIdentity? sim)
    {
        if (sim != null)
            return SimOperatorCatalog.Resolve(sim.Mcc, sim.Mnc, sim.OperatorName);

        return !string.IsNullOrWhiteSpace(sim?.Mcc)
            ? $"PLMN: {sim.Mcc}-{sim.Mnc}"
            : "未知运营商";
    }

    public static string GetCountryDisplay(SimIdentity? sim)
    {
        var imsiCountry = MccCountryHelper.FindByMcc(sim?.Mcc);
        if (imsiCountry != null)
            return $"{imsiCountry.Flag} {imsiCountry.Name} ({imsiCountry.Code})";

        return !string.IsNullOrWhiteSpace(sim?.Mcc) ? $"MCC: {sim.Mcc}" : "--";
    }

    /// <summary>
    /// Returns the operator text reported by the modem. ICCID itself identifies
    /// a profile, but cannot reliably establish its retail country without a
    /// carrier database or user-supplied profile metadata.
    /// </summary>
    public static string GetCardProfileDisplay(SimIdentity? sim)
    {
        return sim == null ? "未读取 SIM" : GetOperatorDisplay(sim);
    }

    /// <summary>Actual IMSI home PLMN used for network authentication.</summary>
    public static string GetImsiHomeDisplay(SimIdentity? sim)
    {
        var country = MccCountryHelper.FindByMcc(sim?.Mcc);
        var plmn = !string.IsNullOrWhiteSpace(sim?.Mcc) ? $"{sim.Mcc}-{sim.Mnc}" : null;

        if (country != null)
            return string.IsNullOrWhiteSpace(plmn)
                ? $"{country.Flag} {country.Name}"
                : $"{country.Flag} {country.Name} · PLMN {plmn}";

        return plmn ?? "--";
    }
}
