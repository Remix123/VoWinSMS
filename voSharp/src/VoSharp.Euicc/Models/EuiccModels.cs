namespace VoSharp.Euicc.Models;

public enum ProfileState
{
    Disabled = 0,
    Enabled = 1
}

public enum ProfileClass
{
    Test = 0,
    Provisioning = 1,
    Operational = 2
}

public class Profile
{
    /// <summary>ISD-R application AID that owns this profile. Required on multi-eUICC cards.</summary>
    public string? EuiccAid { get; set; }

    /// <summary>
    /// Owning eUICC identity when the card exposes it alongside profile data.
    /// Standard single-eUICC responses omit this; dual-EID adapters may include
    /// a 16-byte 5A value in each profile container.
    /// </summary>
    public string? Eid { get; set; }

    /// <summary>
    /// Only profiles on the currently addressed eUICC may receive ES10
    /// mutations. The UI uses this to prevent a dual-EID card from appearing
    /// to switch a profile on an inaccessible chip.
    /// </summary>
    public bool IsCurrentlyAddressable { get; set; } = true;

    public string ICCID { get; set; } = string.Empty;
    public string ISDPAID { get; set; } = string.Empty;
    public ProfileState State { get; set; } = ProfileState.Disabled;
    public string? Nickname { get; set; }
    public string? ServiceProviderName { get; set; }
    public string? ProviderName => ServiceProviderName ?? ProfileName;
    public string? ProfileName { get; set; }
    public ProfileClass ProfileClass { get; set; } = ProfileClass.Operational;
    public int IconType { get; set; }

    public override string ToString()
    {
        var name = !string.IsNullOrWhiteSpace(Nickname) ? Nickname : (!string.IsNullOrWhiteSpace(ProfileName) ? ProfileName : "Unnamed");
        var sp = !string.IsNullOrWhiteSpace(ServiceProviderName) ? $" ({ServiceProviderName})" : "";
        var eid = string.IsNullOrWhiteSpace(Eid) ? string.Empty : $" | EID={Eid}";
        return $"[{State}] ICCID={ICCID} | {name}{sp}{eid}";
    }
}

/// <summary>
/// One independently selectable eUICC storage.  EID alone identifies the chip,
/// whereas ES10 operations must select its ISD-R AID before changing profiles.
/// </summary>
public sealed record EuiccInventoryEntry(string Eid, string Aid, IReadOnlyList<Profile> Profiles)
{
    public string DisplayName => $"EID …{(Eid.Length > 4 ? Eid[^4..] : Eid)} · ISD-R …{(Aid.Length > 4 ? Aid[^4..] : Aid)}";
}

public class EuiccInfo
{
    public string EID { get; set; } = string.Empty;
    public string? Manufacturer { get; set; }
    public string? FirmwareVer { get; set; }
    public int FreeNvramBytes { get; set; }
    public string? DefaultSmdp { get; set; }
    public string? RootDs { get; set; }
}
