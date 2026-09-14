using CommunityToolkit.Mvvm.ComponentModel;

namespace VoWin.Models;

/// <summary>
/// Highest-priority VoWiFi egress rule for one physical SIM/eSIM profile.
/// The presence of a row is significant: a row without a node explicitly
/// forces DIRECT and therefore still overrides the IMSI MCC/PLMN rule.
/// </summary>
public partial class IccidRouteModel : ObservableObject
{
    [ObservableProperty]
    private string _iccid = string.Empty;

    [ObservableProperty]
    private string? _cardName;

    [ObservableProperty]
    private string? _imsiSnapshot;

    [ObservableProperty]
    private string? _phoneNumber;

    [ObservableProperty]
    private string? _proxyNodeId;

    [ObservableProperty]
    private string? _proxyUrl;

    [ObservableProperty]
    private string _proxyNodeName = "直连模式 (Direct)";

    [ObservableProperty]
    private DateTime _updatedAt = DateTime.Now;

    partial void OnProxyNodeIdChanged(string? value) => OnPropertyChanged(nameof(IsDirect));
    partial void OnProxyUrlChanged(string? value) => OnPropertyChanged(nameof(IsDirect));
    partial void OnCardNameChanged(string? value) => OnPropertyChanged(nameof(DisplayName));
    partial void OnImsiSnapshotChanged(string? value) => OnPropertyChanged(nameof(ImsiDisplay));
    partial void OnPhoneNumberChanged(string? value) => OnPropertyChanged(nameof(PhoneDisplay));

    public bool IsDirect => string.IsNullOrWhiteSpace(ProxyNodeId) && string.IsNullOrWhiteSpace(ProxyUrl);

    public string DisplayName => string.IsNullOrWhiteSpace(CardName) ? "未命名 SIM" : CardName;

    public string ImsiDisplay => string.IsNullOrWhiteSpace(ImsiSnapshot) ? "IMSI 未记录" : $"IMSI {ImsiSnapshot}";

    public string PhoneDisplay => string.IsNullOrWhiteSpace(PhoneNumber) ? "号码未提供" : PhoneNumber;
}
