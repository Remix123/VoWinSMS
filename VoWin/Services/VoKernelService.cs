using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows.Threading;
using VoSharp.Euicc.Models;
using VoSharp.Ike.Transport;
using VoSharp.Kernel;
using VoSharp.Kernel.Events;
using VoSharp.Kernel.Pool;
using VoSharp.Modem.At;
using VoSharp.Sim;
using VoSharp.StateMachine;
using VoSharp.Telephony.Calls;
using VoSharp.Telephony.Sms;
using VoSharp.Telephony.VoWifi;
using VoWin.Helpers;
using VoWin.Models;

namespace VoWin.Services
{
    public class VoKernelService : IVoKernelService, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private bool _isInitialScanRunning = true;
        public bool IsInitialScanRunning
        {
            get => _isInitialScanRunning;
            private set
            {
                if (_isInitialScanRunning == value) return;
                _isInitialScanRunning = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsInitialScanRunning)));
            }
        }

        private string _initialScanStatus = "正在准备设备扫描…";
        public string InitialScanStatus
        {
            get => _initialScanStatus;
            private set
            {
                if (_initialScanStatus == value) return;
                _initialScanStatus = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InitialScanStatus)));
            }
        }

        private readonly ConcurrentDictionary<string, byte> _autoVoWifiStarts = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _preferenceApplyGates = new(StringComparer.OrdinalIgnoreCase);
        public IVoKernel Kernel { get; }

        public ObservableCollection<ModemSlot> Slots { get; } = new();
        public ModemSlot? ActiveSlot => Kernel.GetActiveSlot();
        public ObservableCollection<SmsConversationModel> Conversations { get; } = new();
        public ObservableCollection<CallRecordModel> CallHistory { get; } = new();
        public ObservableCollection<ProxyNodeModel> ProxyPresets { get; } = new();
        public ObservableCollection<IccidRouteModel> IccidRoutes { get; } = new();
        public ObservableCollection<CountryRouteModel> CountryRoutes { get; } = new();
        public ObservableCollection<LogEntryModel> Logs { get; } = new();

        public TelephonyState StateMachineState => Kernel.StateMachine.CurrentState;
        public SignalQuality? CurrentSignal => Kernel.Pool.ActiveSlot?.Signal;
        public NetworkRegistration? CurrentRegistration => Kernel.Pool.ActiveSlot?.Registration;
        public SimIdentity? CurrentSim => Kernel.CurrentSim;
        public VoWifiDiagnosticInfo? VoWifiDiag
        {
            get
            {
                try
                {
                    return Kernel.GetVoWifiDiagnostics();
                }
                catch
                {
                    return null;
                }
            }
        }
        public CallSession? ActiveCall => Kernel.ActiveCall;
        public CallState CurrentCallState { get; private set; } = CallState.Idle;
        public string? CurrentCallNumber { get; private set; }
        public bool HasIncomingCall { get; private set; }
        public string? IncomingCallerNumber { get; private set; }
        public event Action<string>? CallMediaStatusChanged;
        public event Action<SmsMessageModel>? IncomingSmsReceived;
        public event Action<string, string?>? IncomingCallReceived;
        public event Action? EgressRoutesChanged;

        public IPreferenceDatabaseService Preferences { get; }
        private DateTime? _callStartTime;
        private readonly Dispatcher _dispatcher;
        private CancellationTokenSource? _usbDebounceCts;
        private readonly ConcurrentQueue<LogEntryModel> _pendingUiLogs = new();
        private readonly Channel<string> _logLines = Channel.CreateBounded<string>(new BoundedChannelOptions(4096)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        private readonly CancellationTokenSource _logWriterCts = new();
        private readonly Task _logWriterTask;
        private readonly SemaphoreSlim _slotDiscoveryGate = new(1, 1);
        private int _logDrainScheduled;
        private int _slotSyncScheduled;
        private int _slotSyncRequestVersion;

        // Microphone capture
        private NAudio.Wave.WaveIn? _waveIn;
        private readonly CellularAudioBridge _cellularAudioBridge = new();
        private readonly Qdc507VoiceRuntime _qdc507VoiceRuntime = new();
        private readonly CallAlerting _callAlerting = new();
        private readonly CallExperienceSettingsStore _callExperienceStore = new();
        private CancellationTokenSource? _cellularCallAudioCts;
        private CancellationTokenSource? _incomingAutoAnswerCts;
        private CallExperienceSettings _callExperience = new();
        private string? _pendingAutoAnswerMessagePath;
        private CallDirection _currentCallDirection = CallDirection.Outgoing;
        private bool _callRecordSavedForCurrentSession = true;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _syncingSlots = new();

        public VoKernelService(IPreferenceDatabaseService? preferences = null)
        {
            _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
            Preferences = preferences ?? new PreferenceDatabaseService();
            Kernel = new VoKernel();
            _logWriterTask = RunLogWriterAsync(_logWriterCts.Token);

            RegisterKernelEvents();
        }

        private static readonly HashSet<string> LegacyBundledCountryCodes = new(StringComparer.OrdinalIgnoreCase)
        {
            "CN", "US", "GB", "HK", "JP", "SG"
        };

        /// <summary>
        /// Removes only the built-in examples that older releases wrote to the
        /// database.  User-created entries are intentionally retained.  A
        /// profile which pointed at one of the examples is removed as well so a
        /// no-config installation cannot silently regain a localhost proxy.
        /// </summary>
        private async Task RemoveLegacyBundledEgressDefaultsAsync(
            List<ProxyNodeModel> savedNodes,
            List<CountryRouteModel> savedRoutes)
        {
            var bundledNodes = savedNodes.Where(IsLegacyBundledProxy).ToList();
            var bundledNodeIds = bundledNodes
                .Select(node => node.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var bundledRoutes = savedRoutes
                .Where(route =>
                    (route.IsDirect && LegacyBundledCountryCodes.Contains(route.CountryCode)) ||
                    (!string.IsNullOrWhiteSpace(route.ProxyNodeId) && bundledNodeIds.Contains(route.ProxyNodeId)))
                .ToList();

            foreach (var route in bundledRoutes)
            {
                await Preferences.DeleteCountryRouteAsync(route.CountryCode);
                savedRoutes.Remove(route);
            }

            foreach (var node in bundledNodes)
            {
                await Preferences.DeleteProxyNodeAsync(node.Id);
                savedNodes.Remove(node);
            }

            if (bundledNodes.Count != 0 || bundledRoutes.Count != 0)
            {
                AddLog("INFO", "Proxy",
                    $"已清理旧版本内置代理/分流项：{bundledNodes.Count} 个代理节点，{bundledRoutes.Count} 条路由。未配置时将保持直连。");
            }
        }

        private static bool IsLegacyBundledProxy(ProxyNodeModel node)
        {
            if (!string.IsNullOrWhiteSpace(node.Username) || !string.IsNullOrWhiteSpace(node.Password) ||
                !node.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return (node.Name.Equals("本地 Clash / v2ray (SOCKS5)", StringComparison.Ordinal) &&
                    node.Protocol.Equals("socks5", StringComparison.OrdinalIgnoreCase) && node.Port == 7890) ||
                   (node.Name.Equals("通用本地 SOCKS5 (1080)", StringComparison.Ordinal) &&
                    node.Protocol.Equals("socks5", StringComparison.OrdinalIgnoreCase) && node.Port == 1080) ||
                   (node.Name.Equals("本地 HTTP 代理 (7890)", StringComparison.Ordinal) &&
                    node.Protocol.Equals("http", StringComparison.OrdinalIgnoreCase) && node.Port == 7890);
        }

        public async Task InitializeAsync()
        {
            IsInitialScanRunning = true;
            InitialScanStatus = "正在加载本地设置…";
            AddLog("INFO", "VoKernelService", "Initializing VoSharp core engine & SQLite database...");

            try
            {
                await Preferences.InitializeAsync();
                _callExperience = await _callExperienceStore.LoadAsync();
                ApplyVolteAudioPrewarmPreference();
                ApplyCallRecordingPreference();

                // 1. Load Call Records from SQLite
                var callHistory = await Preferences.GetCallRecordsAsync(300);
                await RunOnUiAsync(() =>
                {
                    CallHistory.Clear();
                    foreach (var call in callHistory)
                    {
                        CallHistory.Add(call);
                    }
                });

                // 2. Load SMS Messages & Reconstruct Conversations
                var allSms = await Preferences.GetAllSmsMessagesAsync();
                await RunOnUiAsync(() =>
                {
                    Conversations.Clear();
                    var grouped = allSms.GroupBy(m => m.SenderOrRecipient.Trim(), StringComparer.OrdinalIgnoreCase);
                    string[] colors = { "#0078D4", "#107C10", "#D13438", "#8764B8", "#FF8C00", "#008272", "#E3008C" };
                    var convList = new List<SmsConversationModel>();

                    foreach (var g in grouped)
                    {
                        var clean = g.Key;
                        int hash = Math.Abs(clean.GetHashCode());
                        var letter = clean.Length > 0 ? (char.IsLetter(clean[0]) ? clean[0].ToString().ToUpper() : clean[^1].ToString()) : "#";
                        var conv = new SmsConversationModel
                        {
                            ContactNumber = clean,
                            AvatarLetter = letter,
                            AvatarColor = colors[hash % colors.Length]
                        };
                        foreach (var m in g.OrderBy(x => x.Timestamp))
                        {
                            conv.Messages.Add(m);
                            if (!m.IsOutgoing && m.DeliveryState == SmsDeliveryState.Received)
                            {
                                conv.UnreadCount++;
                            }
                        }
                        conv.UpdateLastMessage();
                        convList.Add(conv);
                    }

                    foreach (var conv in convList.OrderByDescending(c => c.LastMessageTime))
                    {
                        Conversations.Add(conv);
                    }
                });

                // 3. Load only user-created proxy nodes and country routes.  No
                // local proxy or country route is assumed by default: an empty
                // configuration must remain direct and never inherit a TUN's
                // incidental localhost listener.
                var savedNodes = (await Preferences.GetAllProxyNodesAsync()).ToList();
                var savedRoutes = (await Preferences.GetAllCountryRoutesAsync()).ToList();
                var savedIccidRoutes = (await Preferences.GetAllIccidRoutesAsync()).ToList();
                await RemoveLegacyBundledEgressDefaultsAsync(savedNodes, savedRoutes);

                // Older releases stored a per-SIM proxy URL inside SimPreferences.
                // Promote it once into the explicit ICCID routing tier so existing
                // users keep their route and can now see/edit the rule in one place.
                var legacySimPreferences = await Preferences.GetAllSimPreferencesAsync();
                foreach (var simPreference in legacySimPreferences.Where(preference =>
                             !string.IsNullOrWhiteSpace(preference.Iccid) &&
                             !string.IsNullOrWhiteSpace(preference.DedicatedProxyUrl)))
                {
                    if (savedIccidRoutes.Any(route => string.Equals(route.Iccid, simPreference.Iccid, StringComparison.Ordinal)))
                        continue;

                    var legacyUrl = simPreference.DedicatedProxyUrl!.Trim();
                    var matchingNode = savedNodes.FirstOrDefault(node =>
                        string.Equals(node.ToProxyUrl(), legacyUrl, StringComparison.OrdinalIgnoreCase));
                    var migrated = new IccidRouteModel
                    {
                        Iccid = simPreference.Iccid.Trim(),
                        CardName = simPreference.CardNickname,
                        ImsiSnapshot = simPreference.Imsi,
                        PhoneNumber = null,
                        ProxyNodeId = matchingNode?.Id,
                        ProxyUrl = matchingNode == null ? legacyUrl : null,
                        ProxyNodeName = matchingNode?.Name ?? DescribeProxyEndpoint(legacyUrl),
                        UpdatedAt = DateTime.Now
                    };
                    savedIccidRoutes.Add(migrated);
                    await Preferences.SaveIccidRouteAsync(migrated);
                }

                if (savedNodes.Count > 0)
                {
                    await RunOnUiAsync(() =>
                    {
                        ProxyPresets.Clear();
                        foreach (var n in savedNodes) ProxyPresets.Add(n);
                    });
                }

                if (savedRoutes.Count > 0)
                {
                    await RunOnUiAsync(() =>
                    {
                        CountryRoutes.Clear();
                        foreach (var r in savedRoutes) CountryRoutes.Add(r);
                    });
                }

                if (savedIccidRoutes.Count > 0)
                {
                    await RunOnUiAsync(() =>
                    {
                        IccidRoutes.Clear();
                        foreach (var route in savedIccidRoutes) IccidRoutes.Add(route);
                    });
                }

                AddLog("INFO", "Preferences", $"SQLite initialized ({callHistory.Count} calls, {allSms.Count} messages loaded)");
            }
            catch (Exception ex)
            {
                AddLog("WARN", "Preferences", $"SQLite initialization error: {ex.Message}");
            }

            await RunOnUiAsync(SyncSlots);

            AddLog("INFO", "VoKernelService", "Starting initial Modem scan...");
            InitialScanStatus = "正在并发扫描串口设备…";
            try
            {
                var ports = SerialPort.GetPortNames();
                AddLog("INFO", "VoKernelService", $"Ports: [{(ports.Length > 0 ? string.Join(", ", ports) : "None")}]");

                var found = await DiscoverSlotsCoreAsync();
                await RunOnUiAsync(SyncSlots);
                AddLog("INFO", "VoKernelService", $"Scan finished, found {found.Count} slots");

                // The eUICC probe is read-only and happens before restoring
                // auto-VoWiFi.  This both makes eSIM support visible at launch
                // and avoids competing APDU traffic with an automatic IMS start.
                InitialScanStatus = "正在识别 eSIM 芯片…";
                foreach (var slot in found)
                {
                    await ProbeEuiccAsync(slot.Id).ConfigureAwait(false);
                }

                InitialScanStatus = "正在恢复模块设置…";
                foreach (var slot in found)
                {
                    await ApplyPreferencesToSlotAsync(slot);
                }

                _ = PrepareQdc507VoiceRuntimeAsync();

                if (Slots.Count == 0)
                {
                    AddLog("INFO", "VoKernelService", "No Modem found");
                }
            }
            catch (Exception ex)
            {
                AddLog("WARN", "VoKernelService", $"Scan failed: {ex.Message}");
            }
            finally
            {
                InitialScanStatus = "扫描完成";
                IsInitialScanRunning = false;
            }
        }

        private void RegisterKernelEvents()
        {
            // Call Events
            Kernel.CallStateChanged += (s, e) =>
            {
                _dispatcher.BeginInvoke(new Action(() =>
                {
                    CurrentCallState = e.NewState;
                    CurrentCallNumber = e.TargetNumber;

                    if (e.NewState == CallState.Ringing && e.IsOutgoing)
                    {
                        _callAlerting.StartRingback();
                    }
                    else if (e.IsOutgoing && e.OldState == CallState.Ringing)
                    {
                        _callAlerting.StopRingback();
                    }

                    if (e.NewState == CallState.Active)
                    {
                        _callStartTime = DateTime.UtcNow;
                        HasIncomingCall = false;
                        Views.Windows.IncomingCallFloatingWindow.Dismiss();
                    }
                    else if (e.NewState == CallState.Ended || e.NewState == CallState.Idle)
                    {
                        HasIncomingCall = false;
                        Views.Windows.IncomingCallFloatingWindow.Dismiss();
                        if (!_callRecordSavedForCurrentSession && _callStartTime.HasValue)
                        {
                            var dur = DateTime.UtcNow - _callStartTime.Value;
                            RecordCallFinished(e.TargetNumber, _currentCallDirection, e.NewState, _callStartTime.Value, dur);
                        }
                        _callStartTime = null;
                    }

                    AddLog("INFO", "Calls", $"Call [{e.TargetNumber}] state changed -> {e.NewState}");
                }));
            };

            Kernel.IncomingCall += (s, e) =>
            {
                _currentCallDirection = CallDirection.Incoming;
                _callRecordSavedForCurrentSession = false;
                try { IncomingCallReceived?.Invoke(e.CallerNumber, e.SlotId); } catch { }
                PostToUi(() =>
                {
                    HasIncomingCall = true;
                    IncomingCallerNumber = e.CallerNumber;
                    CurrentCallNumber = e.CallerNumber;
                    CurrentCallState = CallState.Incoming;

                    AddLog("INFO", "Calls", $"Incoming call from {e.CallerNumber} on slot {e.SlotId ?? "Main"}");

                    var incomingSlotId = e.SlotId;
                    var incomingSlotName = !string.IsNullOrEmpty(incomingSlotId)
                        ? Slots.FirstOrDefault(slot => slot.Id.Equals(incomingSlotId, StringComparison.OrdinalIgnoreCase))?.Name
                        : null;

                    // Show modern top-right floating call window!
                    Views.Windows.IncomingCallFloatingWindow.ShowIncoming(
                        e.CallerNumber,
                        incomingSlotName ?? ActiveSlot?.Name ?? "SIM",
                        onAnswer: async () =>
                        {
                            await AnswerAsync(incomingSlotId);
                            TrayManager.RestoreMainWindow();
                        },
                        onReject: async () =>
                        {
                            await RejectAsync(incomingSlotId);
                        }
                    );
                });
                StartIncomingAlertingAndAutoAnswer(e.SlotId);
            };

            Kernel.CallConnected += async (s, e) =>
            {
                PostToUi(() =>
                {
                    StopIncomingAlerting();
                    CurrentCallState = CallState.Active;
                    _callStartTime = DateTime.UtcNow;
                    HasIncomingCall = false;
                    Views.Windows.IncomingCallFloatingWindow.Dismiss();
                    AddLog("INFO", "Calls", $"Call connected: {e.TargetNumber} (Codec: {e.Codec ?? "Default"})");
                });

                if (e.Codec?.StartsWith("Cellular", StringComparison.OrdinalIgnoreCase) == true)
                {
                    _callAlerting.ReleaseHostAudio();
                    var audioCts = new CancellationTokenSource();
                    var previous = Interlocked.Exchange(ref _cellularCallAudioCts, audioCts);
                    previous?.Cancel();
                    previous?.Dispose();
                    try
                    {
                        await StartCellularCallAudioAsync(
                            e.Codec.Contains("Cellular/UAC", StringComparison.OrdinalIgnoreCase),
                            Interlocked.Exchange(ref _pendingAutoAnswerMessagePath, null), audioCts.Token);
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        AddLog("ERROR", "CellularAudio", $"Unexpected cellular audio failure: {ex.Message}");
                    }
                }
                else
                {
                    PostToUi(() =>
                    {
                        _callAlerting.ReleaseHostAudio();
                        _ = StopCellularAudioBridgeAsync();
                        StartMicrophone();
                    });
                }
            };

            Kernel.CallEnded += (s, e) =>
            {
                _dispatcher.BeginInvoke(new Action(() =>
                {
                    StopIncomingAlerting();
                    _callAlerting.StopRingback();
                    CurrentCallState = CallState.Ended;
                    HasIncomingCall = false;
                    Views.Windows.IncomingCallFloatingWindow.Dismiss();
                    Views.Windows.InCallFloatingWindow.Dismiss();
                    var audioCts = Interlocked.Exchange(ref _cellularCallAudioCts, null);
                    audioCts?.Cancel();
                    audioCts?.Dispose();
                    StopMicrophone();
                    _ = StopCellularAudioBridgeAsync();
                    _ = StopQdc507VoiceRouteAsync();
                    ApplyVolteAudioPrewarmPreference();

                    var dur = e.Duration ?? (_callStartTime.HasValue ? DateTime.UtcNow - _callStartTime.Value : TimeSpan.Zero);
                    RecordCallFinished(e.TargetNumber, _currentCallDirection, CallState.Ended, _callStartTime ?? DateTime.UtcNow, dur, null, e.SlotId, e.WavRecordingPath);
                    _callStartTime = null;
                    AddLog("INFO", "Calls",
                        $"Call ended: {e.TargetNumber}, duration: {dur:mm\\:ss}, reason: {e.Reason ?? "unknown"}");
                }));
            };

            // SMS Events
            Kernel.SmsReceived += (s, e) =>
            {
                _ = AddIncomingSmsAsync(e.Message, e.SlotId);
                AddLog("INFO", "SMS", $"Received SMS from {e.Message.SenderOrRecipient}: {e.Message.Text}");
            };

            Kernel.SmsSent += (s, e) =>
            {
                AddLog("INFO", "SMS", $"SMS sent to {e.Result.Recipient}, status: {e.Result.SubmissionStatus}");
            };

            Kernel.SmsStatusReportReceived += (s, e) =>
            {
                UpdateSmsDeliveryReport(e.Report);
                AddLog("INFO", "SMS", $"SMS Delivery Report for ref #{e.Report.MessageReference} -> {e.Report.DeliveryStatus}");
            };

            // Slot & Pool Events
            Kernel.SlotListChanged += (s, e) =>
            {
                RequestSlotSync();
            };

            Kernel.ActiveSlotChanged += (s, e) =>
            {
                RequestSlotSync();
                AddLog("INFO", "ModemPool", $"Active slot changed to: {e.ActiveSlotId}");
            };

            Kernel.SlotStateChanged += (s, e) =>
            {
                RequestSlotSync();
                AddLog("INFO", "ModemPool", $"Slot {e.SlotId} state -> {e.NewState}");
                if (e.NewState == SlotState.Online)
                {
                    var slot = Slots.FirstOrDefault(x => x.Id == e.SlotId);
                    if (slot != null)
                    {
                        _ = ApplyPreferencesToSlotAsync(slot);
                    }
                }
            };

            // State Machine & Diagnostics
            Kernel.TelephonyStateChanged += (s, e) =>
            {
                AddLog("INFO", "StateMachine", $"State transition: {e.From} -> {e.To}");
            };

            Kernel.VoWifiStateChanged += (s, e) =>
            {
                AddLog("INFO", "VoWiFi", $"VoWiFi state: {e.NewState}");
            };

            Kernel.LogEmitted += (s, e) =>
            {
                AddLog(e.Level, e.Source, e.Message);
                UpdateInitialStartupProgress(e.Source, e.Message);
            };

            Kernel.SystemErrorOccurred += (s, e) =>
            {
                AddLog("ERROR", e.Source, e.ErrorMessage);
            };
        }

        private void UpdateInitialStartupProgress(string source, string message)
        {
            if (!IsInitialScanRunning || !source.Equals("SIM", StringComparison.OrdinalIgnoreCase))
                return;

            string? status = null;
            if (message.Contains("startup stage=SIM readiness", StringComparison.OrdinalIgnoreCase))
            {
                status = message.Contains("ADF.USIM=selected", StringComparison.OrdinalIgnoreCase)
                    ? "SIM 已响应，正在确认 USIM 应用稳定性…"
                    : "正在等待 SIM 卡与 USIM 应用就绪…";
            }
            else if (message.Contains("startup SIM readiness timed out", StringComparison.OrdinalIgnoreCase))
            {
                status = "SIM 初始化较慢；正在保留设备并等待后续重试…";
            }

            if (status is not null)
                PostToUi(() => InitialScanStatus = status, DispatcherPriority.DataBind);
        }

        private void RequestSlotSync()
        {
            Interlocked.Increment(ref _slotSyncRequestVersion);
            ScheduleSlotSync();
        }

        private void ScheduleSlotSync()
        {
            if (Interlocked.Exchange(ref _slotSyncScheduled, 1) != 0)
            {
                return;
            }

            PostToUi(() =>
            {
                var handledVersion = Volatile.Read(ref _slotSyncRequestVersion);
                try
                {
                    SyncSlots();
                }
                finally
                {
                    Interlocked.Exchange(ref _slotSyncScheduled, 0);
                    if (handledVersion != Volatile.Read(ref _slotSyncRequestVersion))
                    {
                        ScheduleSlotSync();
                    }
                }
            }, DispatcherPriority.DataBind);
        }

        private void SyncSlots()
        {
            var kernelSlots = Kernel.GetSlots().ToList();

            var toRemove = Slots.Where(s => !kernelSlots.Any(k => k.Id == s.Id)).ToList();
            foreach (var r in toRemove) Slots.Remove(r);

            foreach (var k in kernelSlots)
            {
                if (!Slots.Any(s => s.Id == k.Id))
                {
                    Slots.Add(k);
                }
            }

            ApplyCallRecordingPreference();
        }

        private void AddLog(string level, string source, string message)
        {
            var now = DateTime.Now;
            message = DiagnosticLogRedactor.Redact(message);
            _pendingUiLogs.Enqueue(new LogEntryModel
            {
                Timestamp = now,
                Level = level,
                Source = source,
                Message = message
            });

            ScheduleLogDrain();
            _logLines.Writer.TryWrite($"[{now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] [{source}] {message}");
        }

        private void ScheduleLogDrain()
        {
            if (Interlocked.Exchange(ref _logDrainScheduled, 1) != 0)
            {
                return;
            }

            PostToUi(DrainPendingLogs, DispatcherPriority.Background);
        }

        private void DrainPendingLogs()
        {
            const int maxBatchSize = 100;
            var drained = 0;

            while (drained < maxBatchSize && _pendingUiLogs.TryDequeue(out var entry))
            {
                Logs.Add(entry);
                drained++;
            }

            while (Logs.Count > 500)
            {
                Logs.RemoveAt(0);
            }

            Interlocked.Exchange(ref _logDrainScheduled, 0);
            if (!_pendingUiLogs.IsEmpty)
            {
                ScheduleLogDrain();
            }
        }

        private static string GetLogFilePath()
        {
            var logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VoWin",
                "Logs");
            Directory.CreateDirectory(logDirectory);
            return Path.Combine(logDirectory, "vowin.log");
        }

        private async Task RunLogWriterAsync(CancellationToken cancellationToken)
        {
            try
            {
                await using var stream = new FileStream(
                    GetLogFilePath(),
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite,
                    16 * 1024,
                    useAsync: true);
                await using var writer = new StreamWriter(stream) { AutoFlush = false };

                while (await _logLines.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    while (_logLines.Reader.TryRead(out var line))
                    {
                        await writer.WriteLineAsync(line).ConfigureAwait(false);
                    }

                    await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch
            {
                // Logging must never bring down the telephony UI.
            }
        }

        private void RecordCallFinished(string? number, CallDirection? dir, CallState state, DateTime startTime, TimeSpan duration, string? codec = null, string? slotId = null, string? wavPath = null)
        {
            if (_callRecordSavedForCurrentSession) return;
            _callRecordSavedForCurrentSession = true;

            var target = !string.IsNullOrWhiteSpace(number) ? number : (!string.IsNullOrWhiteSpace(CurrentCallNumber) ? CurrentCallNumber : "未知号码");
            var direction = dir ?? _currentCallDirection;

            AddCallRecord(target, direction, state, startTime, duration, codec, slotId, wavPath);
        }

        private void AddCallRecord(string number, CallDirection dir, CallState state, DateTime startTime, TimeSpan duration, string? codec = null, string? slotId = null, string? wavPath = null)
        {
            var record = new CallRecordModel
            {
                PhoneNumber = number,
                Direction = dir,
                FinalState = state,
                Timestamp = startTime,
                Duration = duration,
                Codec = codec,
                SlotId = slotId,
                WavRecordingPath = wavPath
            };

            PostToUi(() => CallHistory.Insert(0, record));
            _ = Preferences.SaveCallRecordAsync(record);
        }

        public void DeleteCallRecord(string id)
        {
            PostToUi(() =>
            {
                var item = CallHistory.FirstOrDefault(c => c.Id == id);
                if (item != null)
                {
                    CallHistory.Remove(item);
                    AddLog("INFO", "Calls", $"Deleted call record {id}");
                }
            });
            _ = Preferences.DeleteCallRecordAsync(id);
        }

        private void PostToUi(Action action, DispatcherPriority priority = DispatcherPriority.Normal)
        {
            if (_dispatcher.CheckAccess())
            {
                action();
            }
            else
            {
                _dispatcher.BeginInvoke(action, priority);
            }
        }

        private Task RunOnUiAsync(Action action, DispatcherPriority priority = DispatcherPriority.Normal)
        {
            if (_dispatcher.CheckAccess())
            {
                action();
                return Task.CompletedTask;
            }

            return _dispatcher.InvokeAsync(action, priority).Task;
        }

        private void AddIncomingSms(SmsMessage msg, string? slotId) => _ = AddIncomingSmsAsync(msg, slotId);

        private async Task AddIncomingSmsAsync(SmsMessage msg, string? slotId)
        {
            var remote = !string.IsNullOrWhiteSpace(msg.SenderOrRecipient) ? msg.SenderOrRecipient : "Unknown";
            var receivedAtUtc = DateTime.UtcNow;
            var normalizedTimestamp = TimestampDisplayHelper.NormalizeIncomingNetworkTime(msg.Timestamp, receivedAtUtc);
            if (normalizedTimestamp != TimestampDisplayHelper.ToUtcStorageTime(msg.Timestamp))
            {
                AddLog("INFO", "SMS", $"Corrected network timestamp from {msg.Timestamp:o} to {normalizedTimestamp:o}");
            }

            var exists = await Preferences.HasSmsMessageAsync(remote, normalizedTimestamp, msg.Text, msg.RawPdu).ConfigureAwait(false);
            if (exists)
            {
                return;
            }

            var item = new SmsMessageModel
            {
                Index = msg.Index,
                SlotId = slotId,
                SenderOrRecipient = remote,
                Text = msg.Text,
                Timestamp = normalizedTimestamp,
                IsOutgoing = false,
                DeliveryState = SmsDeliveryState.Received,
                DeliveryStatus = msg.DeliveryStatus,
                MessageReference = msg.MessageReference,
                RawPdu = msg.RawPdu
            };

            await Preferences.SaveSmsMessageAsync(item).ConfigureAwait(false);
            try { IncomingSmsReceived?.Invoke(item); } catch { }

            await RunOnUiAsync(() =>
            {
                var conv = GetOrCreateConversation(remote);
                if (!conv.Messages.Any(m => m.Timestamp == item.Timestamp && m.Text == item.Text))
                {
                    conv.Messages.Add(item);
                    conv.UnreadCount++;
                    conv.UpdateLastMessage();
                    MoveConversationToTop(conv);
                }

                // If incoming SMS contains verification code (OTP): play alert chime and show floating notification window
                if (item.HasOtpCode)
                {
                    var slotName = !string.IsNullOrEmpty(slotId)
                        ? Slots.FirstOrDefault(s => s.Id.Equals(slotId, StringComparison.OrdinalIgnoreCase))?.Name
                        : ActiveSlot?.Name ?? "SIM 1";

                    SoundEffectService.Instance.PlaySmsChime();
                    Views.Windows.IncomingSmsFloatingWindow.ShowNotification(item, slotName);
                }
            }).ConfigureAwait(false);
        }

        private void UpdateSmsDeliveryReport(SmsStatusReport report)
        {
            PostToUi(() =>
            {
                var conv = Conversations.FirstOrDefault(c => c.ContactNumber == report.Recipient);
                if (conv != null)
                {
                    var msg = conv.Messages.LastOrDefault(m => m.IsOutgoing && m.MessageReference == report.MessageReference);
                    if (msg != null)
                    {
                        msg.DeliveryState = report.StatusCode == 0 ? SmsDeliveryState.Delivered : SmsDeliveryState.Failed;
                        msg.DeliveryStatus = report.DeliveryStatus;
                        conv.UpdateLastMessage();
                        _ = Preferences.UpdateSmsDeliveryStatusAsync(msg.Id, msg.DeliveryState, report.DeliveryStatus);
                    }
                }
            });
        }

        private SmsConversationModel GetOrCreateConversation(string number)
        {
            var clean = number.Trim();
            var conv = Conversations.FirstOrDefault(c => c.ContactNumber.Equals(clean, StringComparison.OrdinalIgnoreCase));
            if (conv == null)
            {
                string[] colors = { "#0078D4", "#107C10", "#D13438", "#8764B8", "#FF8C00", "#008272", "#E3008C" };
                int hash = Math.Abs(clean.GetHashCode());
                var letter = clean.Length > 0 ? (char.IsLetter(clean[0]) ? clean[0].ToString().ToUpper() : clean[^1].ToString()) : "#";

                conv = new SmsConversationModel
                {
                    ContactNumber = clean,
                    AvatarLetter = letter,
                    AvatarColor = colors[hash % colors.Length]
                };
                Conversations.Add(conv);
            }
            return conv;
        }

        private void MoveConversationToTop(SmsConversationModel conv)
        {
            int oldIdx = Conversations.IndexOf(conv);
            if (oldIdx > 0)
            {
                Conversations.Move(oldIdx, 0);
            }
            else if (oldIdx < 0)
            {
                Conversations.Insert(0, conv);
            }
        }

        // Operation implementations

        public async Task<IReadOnlyList<ModemSlot>> DiscoverSlotsAsync()
        {
            AddLog("INFO", "ModemPool", "Scanning system COM ports for modem hardware...");
            var slots = await DiscoverSlotsCoreAsync();
            await RunOnUiAsync(SyncSlots);
            return slots;
        }

        private async Task<IReadOnlyList<ModemSlot>> DiscoverSlotsCoreAsync(CancellationToken cancellationToken = default)
        {
            await _slotDiscoveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await Task.Run(
                    async () => await Kernel.DiscoverSlotsAsync(cancellationToken).ConfigureAwait(false),
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _slotDiscoveryGate.Release();
            }
        }

        public async Task<ModemSlot?> AddSlotAsync(string portName, int baudRate = 115200, string? name = null, string? proxyUrl = null)
        {
            AddLog("INFO", "ModemPool", $"Adding modem slot on {portName} ({baudRate} bps)...");
            var slot = await Kernel.AddSlotAsync(portName, baudRate, name, proxyUrl: proxyUrl);
            await RunOnUiAsync(SyncSlots);
            return slot;
        }

        public async Task<bool> RemoveSlotAsync(string slotId)
        {
            AddLog("INFO", "ModemPool", $"Removing slot {slotId}...");
            bool ok = await Kernel.RemoveSlotAsync(slotId);
            await RunOnUiAsync(SyncSlots);
            return ok;
        }

        public bool SelectSlot(string slotId)
        {
            bool ok = Kernel.SelectSlot(slotId);
            RequestSlotSync();
            return ok;
        }

        public async Task RefreshMetricsAsync(string? slotId = null)
        {
            var slot = !string.IsNullOrWhiteSpace(slotId)
                ? Slots.FirstOrDefault(candidate => string.Equals(candidate.Id, slotId, StringComparison.OrdinalIgnoreCase))
                : ActiveSlot;
            if (slot != null)
            {
                // A single coherent modem snapshot avoids three overlapping
                // AT transactions and, importantly, avoids cycling CFUN/SIM
                // just because the user asked to refresh signal status.
                await slot.RefreshMetricsAsync().ConfigureAwait(false);
                return;
            }
            await Kernel.RefreshSignalAsync(slotId).ConfigureAwait(false);
        }

        public async Task<string> ExecuteAtCommandAsync(string command, string? slotId = null)
        {
            var trimmed = command.Trim();
            var loggedCommand = System.Text.RegularExpressions.Regex.Replace(
                trimmed,
                @"(?i)^(AT\+CPIN\s*=).*$",
                "$1<redacted>");
            AddLog("INFO", "AT", $">> {loggedCommand}");

            try
            {
                ModemSlot? targetSlot = null;
                if (!string.IsNullOrEmpty(slotId))
                {
                    Kernel.Pool.Slots.TryGetValue(slotId, out targetSlot);
                }
                else
                {
                    targetSlot = Kernel.Pool.ActiveSlot;
                }

                if (targetSlot?.IsPcscReader == true)
                {
                    const string message = "PC/SC 卡槽不提供 AT 指令；可使用 eSIM/USIM 管理、VoWiFi、IMS 通话和短信。";
                    AddLog("WARN", "PC/SC", message);
                    return $"ERROR: {message}";
                }

                if (targetSlot?.Modem != null && targetSlot.Modem.IsOpen)
                {
                    var resp = await targetSlot.ExecuteAtCommandAsync(trimmed, 5000).ConfigureAwait(false);
                    var output = resp.RawOutput ?? string.Join("\r\n", resp.Lines);
                    AddLog("INFO", "AT", $"<< {output}");
                    return output;
                }
                else
                {
                    // Fallback to Kernel execute command
                    var res = await Kernel.ExecuteCommandAsync(trimmed);
                    AddLog("INFO", "AT", $"<< [{res.Success}] {res.Message}");
                    return res.Message;
                }
            }
            catch (Exception ex)
            {
                AddLog("ERROR", "AT", $"<< ERROR: {ex.Message}");
                return $"ERROR: {ex.Message}";
            }
        }

        public async Task<string> SendUssdAsync(string code, string? slotId = null)
        {
            AddLog("INFO", "USSD", $">> Sending USSD code: {code}");
            try
            {
                ModemSlot? slot = null;
                if (!string.IsNullOrEmpty(slotId))
                {
                    Kernel.Pool.Slots.TryGetValue(slotId, out slot);
                }
                else
                {
                    slot = Kernel.Pool.ActiveSlot;
                }

                if (slot?.IsPcscReader == true)
                {
                    const string message = "PC/SC 卡槽不支持 USSD；请在 VoWiFi 注册后使用 IMS 通话和短信。";
                    AddLog("WARN", "PC/SC", message);
                    return $"ERROR: {message}";
                }

                if (slot != null)
                {
                    var result = await slot.SendUssdAsync(code);
                    AddLog("INFO", "USSD", $"<< {result}");
                    return result;
                }
                return "未找到可用卡槽或调制解调器未就绪。";
            }
            catch (Exception ex)
            {
                AddLog("ERROR", "USSD", $"<< USSD 异常: {ex.Message}");
                return $"ERROR: {ex.Message}";
            }
        }

        public async Task<bool> SetFlightModeAsync(bool enable, string? slotId = null)
        {
            AddLog("INFO", "Modem", $"设置飞行模式: {enable}");
            try
            {
                ModemSlot? slot = null;
                if (!string.IsNullOrEmpty(slotId))
                {
                    Kernel.Pool.Slots.TryGetValue(slotId, out slot);
                }
                else
                {
                    slot = Kernel.Pool.ActiveSlot;
                }

                if (slot?.IsPcscReader == true)
                {
                    AddLog("WARN", "PC/SC", "PC/SC 卡槽没有蜂窝射频，无法设置飞行模式。");
                    return false;
                }

                if (slot != null)
                {
                    var iccid = slot.Sim?.Iccid;
                    var saved = string.IsNullOrWhiteSpace(iccid)
                        ? null
                        : await Preferences.GetSimPreferenceAsync(iccid);
                    var applied = await ApplySimSwitchesAsync(
                        slot.Id,
                        enable,
                        saved?.DefaultVoWifi ?? slot.VoWifi.State != VoWifiState.Disconnected,
                        saved?.DefaultCellularData ?? slot.CellularDataEnabled ?? false,
                        saved?.DefaultDataRoaming ?? slot.DataRoamingEnabled ?? false);
                    AddLog(applied ? "INFO" : "WARN", "Modem", applied
                        ? $"飞行模式已确认{(enable ? "开启 (CFUN=4)" : "关闭 (CFUN=1)")}。"
                        : $"飞行模式未生效：模组没有确认 CFUN={(enable ? 4 : 1)}。偏好未更新。");
                    return applied;
                }
                AddLog("WARN", "Modem", "设置飞行模式失败：未找到目标卡槽。");
                return false;
            }
            catch (Exception ex)
            {
                AddLog("ERROR", "Modem", $"设置飞行模式失败: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> ApplySimSwitchesAsync(
            string slotId,
            bool flightMode,
            bool voWifi,
            bool cellularData,
            bool dataRoaming,
            CancellationToken cancellationToken = default)
        {
            if (!Kernel.Pool.Slots.TryGetValue(slotId, out var slot) || slot == null)
                return false;

            var applyGate = _preferenceApplyGates.GetOrAdd(slot.Id, _ => new SemaphoreSlim(1, 1));
            await applyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var allApplied = true;
                if (slot.IsPcscReader)
                {
                    flightMode = false;
                    cellularData = false;
                    dataRoaming = false;
                }
                else
                {
                    if ((!slot.IsFlightModeKnown || slot.IsFlightMode != flightMode) &&
                        !await slot.SetFlightModeAsync(flightMode, cancellationToken).ConfigureAwait(false))
                        allApplied = false;

                    // Keep the requested data/roaming policy in SQLite while
                    // flight mode is on, but only touch hardware controls when
                    // RF is actually available.
                    if (slot.IsFlightModeKnown && !slot.IsFlightMode)
                    {
                        var existing = string.IsNullOrWhiteSpace(slot.Sim?.Iccid)
                            ? null
                            : await Preferences.GetSimPreferenceAsync(slot.Sim.Iccid).ConfigureAwait(false);
                        var modulePreference = await Preferences.GetModulePreferenceAsync(slot.Id, slot.Imei).ConfigureAwait(false);
                        var desiredRoaming = existing?.DefaultDataRoaming ?? modulePreference?.DefaultDataRoaming;
                        var desiredCellularData = existing?.DefaultCellularData ?? modulePreference?.DefaultCellularData;

                        // Avoid issuing unrelated controls on every aggregate
                        // update. Readback wins when available; persisted
                        // intent is the comparison source when firmware cannot
                        // report a control state.
                        var shouldApplyRoaming = slot.DataRoamingEnabled.HasValue
                            ? slot.DataRoamingEnabled.Value != dataRoaming
                            : desiredRoaming.HasValue
                                ? desiredRoaming.Value != dataRoaming
                                : dataRoaming;
                        if (shouldApplyRoaming)
                        {
                            try
                            {
                                if (!await slot.SetDataRoamingEnabledAsync(dataRoaming, cancellationToken).ConfigureAwait(false))
                                    allApplied = false;
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                allApplied = false;
                                AddLog("WARN", "Modem", $"漫游开关应用失败，但继续处理其他独立开关: {ex.Message}");
                            }
                        }

                        var shouldApplyCellularData = slot.CellularDataEnabled.HasValue
                            ? slot.CellularDataEnabled.Value != cellularData
                            : desiredCellularData.HasValue
                                ? desiredCellularData.Value != cellularData
                                : cellularData;
                        if (shouldApplyCellularData)
                        {
                            try
                            {
                                if (!await slot.SetCellularDataEnabledAsync(cellularData, cancellationToken).ConfigureAwait(false))
                                    allApplied = false;
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                allApplied = false;
                                AddLog("WARN", "Modem", $"蜂窝数据开关应用失败，但继续处理其他独立开关: {ex.Message}");
                            }
                        }
                    }
                }

                try
                {
                    if (voWifi)
                    {
                        if (slot.VoWifi.State == VoWifiState.Disconnected &&
                            !await StartVoWifiForSlotAsync(slot, restorePreferences: false).ConfigureAwait(false))
                            allApplied = false;
                    }
                    else if (slot.VoWifi.State != VoWifiState.Disconnected &&
                             !await Kernel.StopVoWifiAsync(slot.Id).ConfigureAwait(false))
                    {
                        allApplied = false;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    allApplied = false;
                    AddLog("WARN", "VoWiFi", $"VoWiFi 独立开关应用失败；其他开关结果仍保留: {ex.Message}");
                }

                var iccid = slot.Sim?.Iccid;
                if (!string.IsNullOrWhiteSpace(iccid))
                {
                    var existing = await Preferences.GetSimPreferenceAsync(iccid).ConfigureAwait(false);
                    await SaveSimPreferencesAsync(
                        iccid,
                        flightMode,
                        voWifi,
                        cellularData,
                        dataRoaming,
                        existing?.DedicatedProxyUrl,
                        existing?.CardNickname).ConfigureAwait(false);
                }
                return allApplied;
            }
            finally
            {
                applyGate.Release();
            }
        }

        public async Task<bool> RebootModemAsync(string? slotId = null)
        {
            AddLog("INFO", "Modem", "发送模组重启指令...");
            try
            {
                ModemSlot? slot = null;
                if (!string.IsNullOrEmpty(slotId))
                {
                    Kernel.Pool.Slots.TryGetValue(slotId, out slot);
                }
                else
                {
                    slot = Kernel.Pool.ActiveSlot;
                }

                if (slot?.IsPcscReader == true)
                {
                    AddLog("WARN", "PC/SC", "PC/SC 卡槽没有可重启的 Modem。");
                    return false;
                }

                if (slot != null)
                {
                    return await slot.RebootAsync();
                }
                return false;
            }
            catch (Exception ex)
            {
                AddLog("ERROR", "Modem", $"模组重启失败: {ex.Message}");
                return false;
            }
        }

        public async Task<CallInfo> DialAsync(string number, string? slotId = null, bool forceCellular = false)
        {
            AddLog("INFO", "Calls", $"Dialing {number} (ForceCellular={forceCellular})...");
            _currentCallDirection = CallDirection.Outgoing;
            _callRecordSavedForCurrentSession = false;
            CurrentCallNumber = number;
            CurrentCallState = CallState.Dialing;
            _callStartTime = DateTime.UtcNow;

            try
            {
                ModemSlot? slot = null;
                if (!string.IsNullOrEmpty(slotId))
                    Kernel.Pool.Slots.TryGetValue(slotId, out slot);
                else
                    slot = Kernel.Pool.ActiveSlot;

                if (slot?.Modem != null && slot.Modem.IsOpen)
                {
                    return await Kernel.DialAsync(number, slotId, forceCellular);
                }
                else
                {
                    throw new InvalidOperationException("No active modem or modem is not open.");
                }
            }
            catch (Exception ex)
            {
                CurrentCallState = CallState.Ended;
                RecordCallFinished(number, CallDirection.Outgoing, CallState.Ended, _callStartTime ?? DateTime.UtcNow, TimeSpan.Zero);
                AddLog("ERROR", "Calls", $"Dial failed: {ex.Message}");
                throw;
            }
        }

        public async Task<CallInfo?> HangupAsync(string? slotId = null)
        {
            AddLog("INFO", "Calls", "Hanging up current call...");
            Views.Windows.IncomingCallFloatingWindow.Dismiss();

            CallInfo? res = null;
            try
            {
                res = await Kernel.HangupAsync(slotId);
            }
            catch { }

            var dur = _callStartTime.HasValue ? DateTime.UtcNow - _callStartTime.Value : TimeSpan.FromSeconds(15);
            if (!string.IsNullOrEmpty(CurrentCallNumber))
            {
                RecordCallFinished(CurrentCallNumber, _currentCallDirection, CallState.Ended, _callStartTime ?? DateTime.UtcNow, dur, "AMR-WB");
            }

            CurrentCallState = CallState.Ended;
            HasIncomingCall = false;
            _callStartTime = null;

            return res;
        }

        public async Task<CallInfo?> AnswerAsync(string? slotId = null)
        {
            AddLog("INFO", "Calls", "Answering incoming call...");
            Views.Windows.IncomingCallFloatingWindow.Dismiss();
            HasIncomingCall = false;

            try
            {
                return await Kernel.AnswerAsync(slotId);
            }
            catch (Exception ex)
            {
                CurrentCallState = CallState.Ended;
                AddLog("ERROR", "Calls", $"Answer failed: {ex.Message}");
                return null;
            }
        }

        public async Task<CallInfo?> RejectAsync(string? slotId = null)
        {
            AddLog("INFO", "Calls", "Rejecting incoming call...");
            Views.Windows.IncomingCallFloatingWindow.Dismiss();
            HasIncomingCall = false;
            CurrentCallState = CallState.Ended;

            if (!string.IsNullOrEmpty(IncomingCallerNumber))
            {
                RecordCallFinished(IncomingCallerNumber, CallDirection.Missed, CallState.Ended, DateTime.UtcNow, TimeSpan.Zero, "AMR-WB");
            }

            try
            {
                return await Kernel.RejectAsync(slotId);
            }
            catch
            {
                return null;
            }
        }

        public async Task<bool> SendDtmfAsync(char digit, string? slotId = null)
        {
            AddLog("INFO", "Calls", $"Sending DTMF digit: '{digit}'");
            return await Kernel.SendDtmfAsync(digit, slotId);
        }

        public async Task<SmsSubmitResult> SendSmsAsync(string recipient, string text, bool requestStatusReport = true, string? slotId = null, bool forceVowifi = false, bool forceCellular = false)
        {
            AddLog("INFO", "SMS", $"Sending SMS to {recipient}: \"{text}\"");

            SmsConversationModel conv = null!;
            SmsMessageModel model = null!;
            await RunOnUiAsync(() =>
            {
                conv = GetOrCreateConversation(recipient);
                model = new SmsMessageModel
                {
                    SenderOrRecipient = recipient,
                    Text = text,
                    Timestamp = DateTime.UtcNow,
                    IsOutgoing = true,
                    DeliveryState = SmsDeliveryState.Sending
                };
                conv.Messages.Add(model);
                conv.UpdateLastMessage();
                MoveConversationToTop(conv);
            });

            await Preferences.SaveSmsMessageAsync(model);

            try
            {
                var result = await Kernel.SendSmsAsync(recipient, text, requestStatusReport, slotId, forceVowifi, forceCellular);
                await RunOnUiAsync(() =>
                {
                    model.DeliveryState = result.AllPartsAccepted ? SmsDeliveryState.Sent : SmsDeliveryState.Failed;
                    model.DeliveryStatus = result.SubmissionStatus;
                    model.MessageReference = result.ConcatReference;
                    conv.UpdateLastMessage();
                });
                await Preferences.UpdateSmsDeliveryStatusAsync(model.Id, model.DeliveryState, model.DeliveryStatus);
                return result;
            }
            catch (Exception ex)
            {
                await RunOnUiAsync(() =>
                {
                    model.DeliveryState = SmsDeliveryState.Failed;
                    model.DeliveryStatus = ex.Message;
                    conv.UpdateLastMessage();
                });
                await Preferences.UpdateSmsDeliveryStatusAsync(model.Id, SmsDeliveryState.Failed, ex.Message);
                AddLog("ERROR", "SMS", $"Send SMS failed: {ex.Message}");
                throw;
            }
        }

        public void DeleteConversation(string contactNumber)
        {
            PostToUi(() =>
            {
                var conv = Conversations.FirstOrDefault(c => c.ContactNumber == contactNumber);
                if (conv != null)
                {
                    Conversations.Remove(conv);
                    AddLog("INFO", "SMS", $"Deleted conversation with {contactNumber}");
                }
            });
            _ = Preferences.DeleteConversationAsync(contactNumber);
        }

        public void ClearConversation(string contactNumber)
        {
            PostToUi(() =>
            {
                var conv = Conversations.FirstOrDefault(c => c.ContactNumber == contactNumber);
                if (conv != null)
                {
                    conv.Messages.Clear();
                    conv.UpdateLastMessage();
                    AddLog("INFO", "SMS", $"Cleared messages for conversation with {contactNumber}");
                }
            });
            _ = Preferences.DeleteConversationAsync(contactNumber);
        }

        public void DeleteMessage(string conversationNumber, string messageId)
        {
            PostToUi(() =>
            {
                var conv = Conversations.FirstOrDefault(c => c.ContactNumber == conversationNumber);
                if (conv != null)
                {
                    var msg = conv.Messages.FirstOrDefault(m => m.Id == messageId);
                    if (msg != null)
                    {
                        conv.Messages.Remove(msg);
                        conv.UpdateLastMessage();
                    }
                }
            });
            _ = Preferences.DeleteSmsMessageAsync(messageId);
        }

        public void ClearCallHistory()
        {
            PostToUi(CallHistory.Clear);
            _ = Preferences.ClearCallRecordsAsync();
            AddLog("INFO", "Calls", "Call history cleared.");
        }

        public async Task<bool> StartVoWifiAsync(string? slotId = null)
        {
            var targetSlot = (!string.IsNullOrEmpty(slotId) ? Slots.FirstOrDefault(s => s.Id == slotId) : ActiveSlot) ?? Slots.FirstOrDefault();
            if (targetSlot == null) return false;
            await ApplyPreferencesToSlotAsync(targetSlot, restoreAutoVoWifi: false).ConfigureAwait(false);
            return await StartVoWifiForSlotAsync(targetSlot, restorePreferences: false).ConfigureAwait(false);
        }

        private async Task<bool> StartVoWifiForSlotAsync(ModemSlot targetSlot, bool restorePreferences)
        {
            if (restorePreferences)
                await ApplyPreferencesToSlotAsync(targetSlot, restoreAutoVoWifi: false).ConfigureAwait(false);

            // VoCat applies the selected device's current route immediately
            // before opening the tunnel. Keep this separate from preference
            // restoration so a switch transaction can call it without
            // re-entering the per-slot preference gate.
            var effectiveProxy = ResolveEgressProxyForSlot(targetSlot.Id);
            if (!Kernel.SetSlotProxy(targetSlot.Id, effectiveProxy))
            {
                AddLog("ERROR", "Proxy", $"Unable to apply the proxy route to slot {targetSlot.Id}.");
                return false;
            }

            targetSlot.ProxyUrl = effectiveProxy;
            AddLog("INFO", "VoWiFi", string.IsNullOrEmpty(effectiveProxy)
                ? $"VoWiFi 使用直连 UDP -> 卡槽 [{targetSlot.Name}]"
                : $"VoWiFi 代理路由已应用至核心: {DescribeProxyEndpoint(effectiveProxy)} -> 卡槽 [{targetSlot.Name}]");
            AddLog("INFO", "VoWiFi", $"Starting VoWiFi on slot {targetSlot.Id}...");
            return await Kernel.StartVoWifiAsync(targetSlot.Id).ConfigureAwait(false);
        }

        public async Task<bool> StopVoWifiAsync(string? slotId = null)
        {
            AddLog("INFO", "VoWiFi", $"Stopping VoWiFi on slot {slotId ?? "Default"}...");
            return await Kernel.StopVoWifiAsync(slotId);
        }

        public async Task<(bool Success, long RttMs, string Status)> ProbeVoWifiLivenessAsync(string? slotId = null)
        {
            var result = await Kernel.ProbeVoWifiLivenessAsync(slotId);
            AddLog(result.Success ? "INFO" : "WARN", "VoWiFi", $"SIP 探针保活检测: {(result.Success ? "成功" : "失败")}, RTT: {result.RttMs}ms, 状态: {result.Status}");
            return result;
        }

        public async Task<EuiccProbeResult> ProbeEuiccAsync(string? slotId = null, CancellationToken cancellationToken = default)
        {
            var slot = (!string.IsNullOrEmpty(slotId) ? Slots.FirstOrDefault(s => s.Id == slotId) : ActiveSlot) ?? Slots.FirstOrDefault();
            if (slot == null)
            {
                return new EuiccProbeResult(
                    EuiccCapability.Error,
                    null,
                    Array.Empty<Profile>(),
                    "未发现可用卡槽，无法检测 eUICC 芯片。",
                    Array.Empty<string>());
            }

            var result = await slot.ProbeEuiccAsync(cancellationToken).ConfigureAwait(false);
            var level = result.Capability == EuiccCapability.Error ? "WARN" : "INFO";
            AddLog(level, "eSIM", $"卡槽 [{slot.Name}] eUICC 检测：{result.Message}");
            return result;
        }

        public async Task<IReadOnlyList<Profile>> GetEuiccProfilesAsync(string? slotId = null)
        {
            var slot = (!string.IsNullOrEmpty(slotId) ? Slots.FirstOrDefault(s => s.Id == slotId) : ActiveSlot) ?? Slots.FirstOrDefault();
            if (slot != null)
            {
                return await slot.GetEuiccProfilesAsync();
            }
            return await Kernel.GetEuiccProfilesAsync();
        }

        public async Task<bool> SwitchEuiccProfileAsync(string iccidOrAid, string? slotId = null, string? euiccAid = null)
        {
            AddLog("INFO", "eSIM", $"Switching to eSIM profile: {iccidOrAid} on slot {slotId ?? "Active"}");
            var slot = (!string.IsNullOrEmpty(slotId) ? Slots.FirstOrDefault(s => s.Id == slotId) : ActiveSlot) ?? Slots.FirstOrDefault();
            if (slot != null)
            {
                var switched = await slot.SwitchEuiccProfileAsync(iccidOrAid, euiccAid: euiccAid).ConfigureAwait(false);
                if (switched)
                {
                    // Only the newly verified ICCID may select SIM preferences,
                    // proxy routing, permanent IMSI overrides and auto-VoWiFi.
                    await ApplyPreferencesToSlotAsync(slot).ConfigureAwait(false);
                    RequestSlotSync();
                }
                return switched;
            }
            return await Kernel.SwitchEuiccProfileAsync(iccidOrAid);
        }

        public async Task<bool> DisableEuiccProfileAsync(string iccidOrAid, string? slotId = null, string? euiccAid = null)
        {
            AddLog("INFO", "eSIM", $"Disabling eSIM profile: {iccidOrAid} on slot {slotId ?? "Active"}");
            var slot = (!string.IsNullOrEmpty(slotId) ? Slots.FirstOrDefault(s => s.Id == slotId) : ActiveSlot) ?? Slots.FirstOrDefault();
            if (slot != null)
            {
                return await slot.DisableEuiccProfileAsync(iccidOrAid, euiccAid: euiccAid);
            }
            return await Kernel.DisableEuiccProfileAsync(iccidOrAid);
        }

        public async Task<bool> DeleteEuiccProfileAsync(string iccidOrAid, string? slotId = null, string? euiccAid = null)
        {
            AddLog("INFO", "eSIM", $"Deleting eSIM profile: {iccidOrAid}");
            var slot = (!string.IsNullOrEmpty(slotId) ? Slots.FirstOrDefault(s => s.Id == slotId) : ActiveSlot) ?? Slots.FirstOrDefault();
            if (slot != null)
            {
                return await slot.DeleteEuiccProfileAsync(iccidOrAid, euiccAid: euiccAid);
            }
            return await Kernel.DeleteEuiccProfileAsync(iccidOrAid);
        }

        public async Task<bool> RenameEuiccProfileAsync(string iccidOrAid, string nickname, string? slotId = null, string? euiccAid = null)
        {
            AddLog("INFO", "eSIM", $"Renaming profile {iccidOrAid} -> \"{nickname}\" on slot {slotId ?? "Active"}");
            var slot = (!string.IsNullOrEmpty(slotId) ? Slots.FirstOrDefault(s => s.Id == slotId) : ActiveSlot) ?? Slots.FirstOrDefault();
            if (slot != null)
            {
                return await slot.RenameEuiccProfileAsync(iccidOrAid, nickname, euiccAid: euiccAid);
            }
            return await Kernel.RenameEuiccProfileAsync(iccidOrAid, nickname);
        }

        public async Task<string> GetEuiccEidAsync(string? slotId = null)
        {
            var slot = (!string.IsNullOrEmpty(slotId) ? Slots.FirstOrDefault(s => s.Id == slotId) : ActiveSlot) ?? Slots.FirstOrDefault();
            if (slot != null)
            {
                return await slot.GetEuiccEidAsync();
            }
            return await Kernel.GetEuiccEidAsync();
        }

        public async Task<EuiccDownloadResult> DownloadEuiccProfileAsync(
            string activationCode,
            string? confirmationCode = null,
            IProgress<EuiccDownloadProgress>? progress = null,
            string? slotId = null,
            CancellationToken cancellationToken = default,
            bool allowUntrustedTls = false,
            bool allowRetryAfterUncertain = false,
            string? euiccAid = null)
        {
            var slot = (!string.IsNullOrEmpty(slotId) ? Slots.FirstOrDefault(s => s.Id == slotId) : ActiveSlot) ?? Slots.FirstOrDefault();
            AddLog("INFO", "eSIM", $"Starting verified eSIM profile download on slot {slot?.Name ?? "Active"}.");
            if (allowUntrustedTls)
                AddLog("WARN", "eSIM", "TLS certificate validation bypass enabled for this SM-DP+ download session.");
            if (allowRetryAfterUncertain)
                AddLog("WARN", "eSIM", "Provider-authorized retry enabled; the matching local uncertain transaction may be reset.");
            var tracedProgress = new CallbackProgress<EuiccDownloadProgress>(value =>
            {
                AddLog("INFO", "eSIM", $"Download progress {value.Percent}%: {value.Status}");
                progress?.Report(value);
            });
            try
            {
                var result = slot != null
                    ? await slot.DownloadEuiccProfileAsync(activationCode, confirmationCode, tracedProgress, cancellationToken, allowUntrustedTls, allowRetryAfterUncertain, euiccAid)
                    : await Kernel.DownloadEuiccProfileAsync(activationCode, confirmationCode, tracedProgress, cancellationToken, allowUntrustedTls, allowRetryAfterUncertain);
                AddLog(result.InstalledWithWarning ? "WARN" : "INFO", "eSIM",
                    $"Profile download completed. ICCID={result.Iccid}; Warning={result.Warning ?? "None"}");
                return result;
            }
            catch (Exception ex)
            {
                var errorChain = new List<string>();
                for (Exception? current = ex; current != null; current = current.InnerException)
                {
                    var message = current.Message.Trim();
                    if (!string.IsNullOrEmpty(message) && !errorChain.Contains(message, StringComparer.Ordinal))
                        errorChain.Add(message);
                }
                AddLog("ERROR", "eSIM", $"Profile download failed: {string.Join(" -> ", errorChain)}");
                throw;
            }
        }

        public bool SetSlotProxy(string slotId, string? proxyUrl)
        {
            AddLog("INFO", "Proxy", $"Setting proxy for slot {slotId}: {proxyUrl ?? "Direct"}");
            return Kernel.SetSlotProxy(slotId, proxyUrl);
        }

        public async Task<int> TestProxyConnectivityAsync(ProxyNodeModel node, string targetHost = "8.8.8.8", int targetPort = 53)
        {
            var sw = Stopwatch.StartNew();
            node.Status = "正在验证 SOCKS5 公网 UDP 收发...";
            IPEndPoint? relay = null;

            try
            {
                using var proxy = Socks5Client.TryParse(node.ToProxyUrl())
                    ?? throw new InvalidOperationException("VoWiFi 需要 socks://、socks5:// 或 socks5h:// 代理；HTTP 代理不支持 IKEv2/ESP UDP。");
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                relay = await proxy.UdpAssociateAsync(cts.Token);

                var targetIp = IPAddress.TryParse(targetHost, out var parsedTarget)
                    ? parsedTarget
                    : (await Dns.GetHostAddressesAsync(targetHost, cts.Token).ConfigureAwait(false))
                        .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork)
                        ?? throw new InvalidOperationException($"无法解析 UDP 探测目标 {targetHost}。");

                var transactionId = (ushort)Random.Shared.Next(1, ushort.MaxValue + 1);
                var dnsQuery = BuildDnsHealthQuery(transactionId);
                var dnsResponse = await proxy.UdpRoundTripAsync(
                    dnsQuery,
                    new IPEndPoint(targetIp, targetPort),
                    cts.Token).ConfigureAwait(false);
                if (dnsResponse.Length < 12 ||
                    BinaryPrimitives.ReadUInt16BigEndian(dnsResponse.AsSpan(0, 2)) != transactionId ||
                    (dnsResponse[2] & 0x80) == 0)
                {
                    throw new InvalidOperationException("SOCKS5 公网 UDP 返回了无效的 DNS 响应。");
                }

                sw.Stop();
                int ms = (int)sw.ElapsedMilliseconds;
                node.LatencyMs = ms;
                node.Status = $"SOCKS5 公网 UDP 正常 ({ms}ms)";
                AddLog("INFO", "Proxy",
                    $"SOCKS5 UDP data plane healthy for {node.Name} ({node.Host}:{node.Port}); relay {relay}, DNS {targetIp}:{targetPort}, RTT {ms}ms");
                return ms;
            }
            catch (Exception ex)
            {
                sw.Stop();
                node.LatencyMs = -1;
                node.Status = relay == null
                    ? $"SOCKS5 UDP 关联失败: {ex.Message}"
                    : $"SOCKS5 UDP 已关联，但公网 DNS 探测无响应: {ex.Message}";
                AddLog("WARN", "Proxy",
                    $"SOCKS5 UDP health check failed for {node.Name} ({node.Host}:{node.Port}); " +
                    $"relay={(relay == null ? "not established" : relay)}, error={ex.Message}");
                return -1;
            }
        }

        public async Task<string> BuildImsDiagnosticReportAsync(string? slotId = null)
        {
            var slot = (!string.IsNullOrWhiteSpace(slotId)
                ? Slots.FirstOrDefault(s => s.Id == slotId)
                : ActiveSlot) ?? Slots.FirstOrDefault();
            var diag = slot?.VoWifiDiag ?? VoWifiDiag;
            var routeDecision = await ResolveVoWifiRouteAsync(slot?.Id).ConfigureAwait(false);
            var report = new StringBuilder();
            var appVersion = typeof(VoKernelService).Assembly.GetName().Version?.ToString() ?? "unknown";

            report.AppendLine("VoWin IMS diagnostic report");
            report.AppendLine("This report automatically redacts subscriber identities, phone numbers, AKA material, and proxy credentials.");
            report.AppendLine($"Generated (local): {DateTimeOffset.Now:O}");
            report.AppendLine($"App version: {appVersion}");
            report.AppendLine($"OS: {Environment.OSVersion}");
            report.AppendLine();
            report.AppendLine("=== Selected modem / SIM context ===");
            report.AppendLine($"Slot: {slot?.Id ?? "none"}");
            report.AppendLine($"Port: {slot?.PortName ?? "unknown"}");
            report.AppendLine($"Firmware: {slot?.Modem?.FirmwareRevision ?? "unknown"}");
            report.AppendLine($"Flight mode: {slot?.IsFlightMode.ToString() ?? "unknown"}");
            report.AppendLine($"SIM PLMN: {slot?.Sim?.Mcc ?? "?"}-{slot?.Sim?.Mnc ?? "?"}");
            report.AppendLine($"IMSI source: {slot?.ImsiIdentitySource ?? "unknown"}");
            report.AppendLine($"EF_IMSI PLMN: {DescribeImsiPlmn(slot?.LastPermanentImsi)}");
            report.AppendLine($"AT+CIMI PLMN: {DescribeImsiPlmn(slot?.LastReportedImsi)}");
            report.AppendLine($"Multi-IMSI PLMN conflict: {slot?.HasImsiPlmnConflict.ToString() ?? "unknown"}");
            report.AppendLine($"Stable routing MCC: {slot?.StableRoutingMcc ?? "unknown"}");
            report.AppendLine($"SOCKS route configured: {!string.IsNullOrWhiteSpace(slot?.ProxyUrl)}");
            report.AppendLine($"Route rule: {routeDecision.RuleDisplay}");
            report.AppendLine($"Route target: {routeDecision.TargetDisplay}");
            report.AppendLine();
            report.AppendLine("=== VoWiFi / IMS snapshot ===");
            report.AppendLine($"State: {diag?.State.ToString() ?? "unavailable"}");
            report.AppendLine($"Last error: {DiagnosticLogRedactor.Redact(diag?.LastError ?? "none")}");
            report.AppendLine($"Failure classification: stage={diag?.FailureStage ?? "none"}; category={diag?.FailureCategory ?? "none"}");
            report.AppendLine($"ePDG: {diag?.EpdgFqdn ?? "unknown"} ({diag?.EpdgIp ?? "unknown"}:{diag?.EpdgPort})");
            report.AppendLine($"IKE suite: {diag?.Suite.ToString() ?? "unknown"}; DH: {diag?.DhGroup ?? "unknown"}; EAP: {diag?.EapMethod ?? "unknown"}");
            report.AppendLine($"Tunnel: {(diag?.Tunnel is null ? "not established" : $"assigned={diag.Tunnel.AssignedIPv4 ?? diag.Tunnel.AssignedIPv6 ?? "unknown"}; P-CSCF={diag.Tunnel.PcscfIp}; ESP={diag.Tunnel.EncryptionAlgorithm}/{diag.Tunnel.IntegrityAlgorithm}")}");
            report.AppendLine($"IMS result: {diag?.Ims?.RegistrationState ?? "not registered"}; security={diag?.Ims?.SecurityAssociation ?? "n/a"}; expiry={diag?.Ims?.ExpiresSeconds.ToString() ?? "n/a"}");
            report.AppendLine($"SIP probes: sent={diag?.SipProbesSent ?? 0}; success={diag?.SipProbesSuccess ?? 0}; failed={diag?.SipProbesFailed ?? 0}; last={diag?.LastProbeResult ?? "none"}");
            report.AppendLine();
            report.AppendLine("=== Latest IMS registration chain ===");
            report.AppendLine("Only the most recent VoWiFi start attempt and its ePDG / IKE / EAP / tunnel / IMS / SIP events are included.");

            try
            {
                var persisted = await ReadTailLinesAsync(GetLogFilePath(), 5000).ConfigureAwait(false);
                var live = Logs.Select(entry =>
                    $"[{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{entry.Level}] [{entry.Source}] {entry.Message}");
                var chain = ExtractLatestImsRegistrationChain(persisted.Concat(live));
                foreach (var line in chain)
                    report.AppendLine(DiagnosticLogRedactor.Redact(line));
            }
            catch (Exception ex)
            {
                report.AppendLine($"[WARN] Could not read persisted log: {DiagnosticLogRedactor.Redact(ex.Message)}");
            }

            AddLog("INFO", "Diagnostics", "A concise IMS registration-chain diagnostic report was generated (sensitive fields redacted).");
            return report.ToString();
        }

        private static string DescribeImsiPlmn(string? imsi)
        {
            if (string.IsNullOrWhiteSpace(imsi) || imsi.Length < 5) return "unavailable";
            return $"{imsi[..3]}-{imsi.Substring(3, 2)}…";
        }

        private static IReadOnlyList<string> ExtractLatestImsRegistrationChain(IEnumerable<string> source)
        {
            var allLines = source
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var startIndex = allLines.FindLastIndex(IsImsRegistrationStart);
            // Keep the few setup events immediately before the start marker so
            // the selected SOCKS/direct route is visible with the attempt.
            var attemptLines = startIndex >= 0
                ? allLines.Skip(Math.Max(0, startIndex - 5))
                : allLines;
            var relevant = attemptLines.Where(IsImsRegistrationEvent).ToList();

            if (relevant.Count == 0)
                return ["No VoWiFi registration event was recorded yet. Reproduce the failure once, then export the report immediately."];

            // An ESP-heavy trace can be noisy. Preserve the beginning (DNS/IKE)
            // and the end (the actual failure) while keeping the report shareable.
            const int maxLines = 420;
            if (relevant.Count <= maxLines) return relevant;

            var excerpt = relevant.Take(100).ToList();
            excerpt.Add($"[INFO] [Diagnostics] {relevant.Count - maxLines} registration-chain events omitted; showing setup and final failure.");
            excerpt.AddRange(relevant.TakeLast(maxLines - excerpt.Count));
            return excerpt;
        }

        private static bool IsImsRegistrationStart(string line) =>
            line.Contains("Starting VoWiFi on slot", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("VoWiFi state: ResolvingEpdg", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("REGISTER diagnostics started", StringComparison.OrdinalIgnoreCase);

        private static bool IsImsRegistrationEvent(string line) =>
            line.Contains("[VoWiFi]", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("[IMS]", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("[SIP]", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("[IKE", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("[IPsec", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("[Proxy]", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("[StateMachine]", StringComparison.OrdinalIgnoreCase);

        private static async Task<IReadOnlyList<string>> ReadTailLinesAsync(string path, int maxLines)
        {
            if (!File.Exists(path)) return Array.Empty<string>();

            var lines = new Queue<string>(maxLines);
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 16 * 1024, useAsync: true);
            using var reader = new StreamReader(stream);
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (lines.Count == maxLines) lines.Dequeue();
                lines.Enqueue(line);
            }
            return lines.ToArray();
        }

        private static byte[] BuildDnsHealthQuery(ushort transactionId)
        {
            // Minimal recursive A query for example.com.
            var query = new byte[29];
            BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(0, 2), transactionId);
            BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(2, 2), 0x0100);
            BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(4, 2), 1);
            query[12] = 7;
            Encoding.ASCII.GetBytes("example").CopyTo(query, 13);
            query[20] = 3;
            Encoding.ASCII.GetBytes("com").CopyTo(query, 21);
            query[24] = 0;
            BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(25, 2), 1);
            BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(27, 2), 1);
            return query;
        }

        public void AddProxyPreset(ProxyNodeModel node)
        {
            ProxyPresets.Add(node);
            _ = Preferences.SaveProxyNodeAsync(node);
            AddLog("INFO", "Proxy", $"Added proxy preset: {node.Name} ({node.ToProxyUrl()})");
            RaiseEgressRoutesChanged();
        }

        public void RemoveProxyPreset(string nodeId)
        {
            var item = ProxyPresets.FirstOrDefault(p => p.Id == nodeId);
            if (item != null)
            {
                ProxyPresets.Remove(item);
                _ = Preferences.DeleteProxyNodeAsync(nodeId);
                AddLog("INFO", "Proxy", $"Removed proxy preset: {item.Name}");
                RaiseEgressRoutesChanged();
            }
        }

        public string? ResolveEgressProxyForSlot(string slotId)
        {
            var slot = Slots.FirstOrDefault(s => s.Id == slotId);
            if (slot == null) return null;

            // Tier 1: the existence of an ICCID row always wins. A row without
            // a proxy is an explicit DIRECT rule and must not fall through.
            var iccid = slot.Sim?.Iccid?.Trim();
            if (!string.IsNullOrWhiteSpace(iccid))
            {
                var cardRule = IccidRoutes.FirstOrDefault(route =>
                    string.Equals(route.Iccid, iccid, StringComparison.Ordinal));
                if (cardRule != null)
                {
                    if (!string.IsNullOrWhiteSpace(cardRule.ProxyNodeId))
                    {
                        return ToSupportedSocksProxy(
                            ProxyPresets.FirstOrDefault(node => node.Id == cardRule.ProxyNodeId)?.ToProxyUrl());
                    }

                    // ProxyUrl exists only for a migrated legacy per-SIM URL.
                    return ToSupportedSocksProxy(cardRule.ProxyUrl);
                }
            }

            // Tier 2: without an ICCID row, resolve the country from the active
            // IMSI MCC and match the PLMN country route.
            var mcc = slot.StableRoutingMcc ?? slot.Sim?.Mcc;
            if (!string.IsNullOrWhiteSpace(mcc))
            {
                var country = MccCountryHelper.FindByMcc(mcc);
                if (country != null)
                {
                    var rule = CountryRoutes.FirstOrDefault(r => string.Equals(r.CountryCode, country.Code, StringComparison.OrdinalIgnoreCase));
                    if (rule != null && !rule.IsDirect && !string.IsNullOrEmpty(rule.ProxyNodeId))
                    {
                        var node = ProxyPresets.FirstOrDefault(p => p.Id == rule.ProxyNodeId);
                        if (node != null)
                        {
                            return ToSupportedSocksProxy(node.ToProxyUrl());
                        }
                    }
                }
            }

            return null;
        }

        public async Task<VoWifiRouteDecision> ResolveVoWifiRouteAsync(string? slotId = null)
        {
            var slot = (!string.IsNullOrWhiteSpace(slotId)
                ? Slots.FirstOrDefault(candidate => string.Equals(candidate.Id, slotId, StringComparison.OrdinalIgnoreCase))
                : ActiveSlot) ?? Slots.FirstOrDefault();

            if (slot == null)
            {
                return new VoWifiRouteDecision(
                    "未选择通信设备",
                    "未解析",
                    "请先选择已识别的 SIM 卡。",
                    null,
                    false);
            }

            await Task.CompletedTask;
            var iccid = slot.Sim?.Iccid?.Trim();
            var cardRule = string.IsNullOrWhiteSpace(iccid)
                ? null
                : IccidRoutes.FirstOrDefault(route => string.Equals(route.Iccid, iccid, StringComparison.Ordinal));
            if (cardRule != null)
            {
                if (!string.IsNullOrWhiteSpace(cardRule.ProxyNodeId))
                {
                    var node = ProxyPresets.FirstOrDefault(candidate => candidate.Id == cardRule.ProxyNodeId);
                    if (node == null)
                    {
                        return new VoWifiRouteDecision(
                            "ICCID 卡规则（最高优先级）",
                            "直连 UDP（引用节点不存在）",
                            "已命中这张卡的 ICCID 规则，因此不会继续匹配 PLMN；请重新选择有效代理节点。",
                            null,
                            false);
                    }

                    using var parsedNode = Socks5Client.TryParse(node.ToProxyUrl());
                    if (parsedNode == null)
                    {
                        return new VoWifiRouteDecision(
                            "ICCID 卡规则（最高优先级）",
                            "直连 UDP（节点协议不支持）",
                            "已命中 ICCID 规则，但该节点不是 SOCKS5；不会继续匹配 PLMN，也不会回落到系统/TUN 代理。",
                            null,
                            false);
                    }

                    return new VoWifiRouteDecision(
                        "ICCID 卡规则（最高优先级）",
                        $"SOCKS5：{node.Name} ({node.Host}:{node.Port})",
                        $"ICCID {iccid} 已设置专属出口；IMSI MCC/PLMN 国家规则已被覆盖。",
                        node.ToProxyUrl(),
                        true);
                }

                if (!string.IsNullOrWhiteSpace(cardRule.ProxyUrl))
                {
                    return DescribeConfiguredRoute(
                        "ICCID 卡规则（最高优先级，旧版地址）",
                        cardRule.ProxyUrl,
                        $"ICCID {iccid} 已设置专属出口；IMSI MCC/PLMN 国家规则已被覆盖。");
                }

                return new VoWifiRouteDecision(
                    "ICCID 卡规则（最高优先级）",
                    "直连 UDP",
                    $"ICCID {iccid} 明确设为直连，因此不会继续匹配 IMSI MCC/PLMN 国家规则。",
                    null,
                    false);
            }

            // Country dispatch is deliberately based on the IMSI-derived MCC.
            // ICCID is used only to locate the per-SIM preference above; it is
            // not an authoritative country or carrier-routing source.
            var mcc = slot.StableRoutingMcc ?? slot.Sim?.Mcc;
            var country = MccCountryHelper.FindByMcc(mcc);
            if (country == null)
            {
                return new VoWifiRouteDecision(
                    "无 ICCID 卡规则；IMSI MCC 无法识别",
                    "直连 UDP",
                    $"未能由当前 IMSI 解析 MCC（{mcc ?? "--"}），因此没有命中国家分流规则。",
                    null,
                    false);
            }

            var countryRule = CountryRoutes.FirstOrDefault(rule =>
                string.Equals(rule.CountryCode, country.Code, StringComparison.OrdinalIgnoreCase));
            if (countryRule != null && !countryRule.IsDirect && !string.IsNullOrWhiteSpace(countryRule.ProxyNodeId))
            {
                var node = ProxyPresets.FirstOrDefault(candidate => candidate.Id == countryRule.ProxyNodeId);
                if (node != null)
                {
                    using var parsedNode = Socks5Client.TryParse(node.ToProxyUrl());
                    if (parsedNode == null)
                    {
                        return new VoWifiRouteDecision(
                            $"自动国家规则（IMSI MCC {mcc} → {country.Flag} {country.Name}）",
                            "直连 UDP（节点协议不支持）",
                            "国家规则命中的节点不是 SOCKS5；不会回落到系统/TUN 代理。",
                            null,
                            false);
                    }

                    return new VoWifiRouteDecision(
                        $"自动国家规则（IMSI MCC {mcc} → {country.Flag} {country.Name}）",
                        $"SOCKS5：{node.Name} ({node.Host}:{node.Port})",
                        slot.HasImsiPlmnConflict
                            ? "同一 ICCID 曾报告不同 PLMN；为避免出口抖动，国家规则使用首次稳定 MCC。建议为这张卡配置 ICCID 专属规则。"
                            : "未找到 ICCID 卡规则，因此由 IMSI 的 MCC 命中次级 PLMN 规则。",
                        node.ToProxyUrl(),
                        true);
                }

                return new VoWifiRouteDecision(
                    $"自动国家规则（IMSI MCC {mcc} → {country.Flag} {country.Name}）",
                    "直连 UDP",
                    "国家规则引用的代理节点已不存在，因此不会回退到系统/TUN 代理。",
                    null,
                    false);
            }

            var ruleNote = countryRule?.IsDirect == true
                ? "该国家规则明确配置为直连。"
                : "未配置该国家的代理规则。";
            return new VoWifiRouteDecision(
                $"自动国家规则（IMSI MCC {mcc} → {country.Flag} {country.Name}）",
                "直连 UDP",
                slot.HasImsiPlmnConflict
                    ? $"同一 ICCID 曾报告不同 PLMN；国家分流已锁定首次稳定 MCC。{ruleNote} 建议配置 ICCID 专属规则。"
                    : $"未找到 ICCID 卡规则；{ruleNote}",
                null,
                false);
        }

        private static VoWifiRouteDecision DescribeConfiguredRoute(string ruleDisplay, string proxyUrl, string detail)
        {
            using var parsedProxy = Socks5Client.TryParse(proxyUrl);
            if (parsedProxy == null)
            {
                return new VoWifiRouteDecision(
                    ruleDisplay,
                    "直连 UDP（保存的代理无效，已忽略）",
                    $"{detail} 但保存的地址不是受支持的 socks://、socks5:// 或 socks5h:// 代理，VoWiFi 不会使用 HTTP/TUN 代理。",
                    null,
                    false);
            }

            return new VoWifiRouteDecision(
                ruleDisplay,
                $"SOCKS5：{DescribeProxyEndpoint(proxyUrl)}",
                detail,
                proxyUrl,
                true);
        }

        private static string? ToSupportedSocksProxy(string? proxyUrl)
        {
            if (string.IsNullOrWhiteSpace(proxyUrl)) return null;
            using var parsedProxy = Socks5Client.TryParse(proxyUrl);
            return parsedProxy == null ? null : proxyUrl.Trim();
        }

        public void SaveIccidRoute(string iccid, string? proxyNodeId, string? cardName = null, string? imsi = null, string? phoneNumber = null)
        {
            var normalizedIccid = iccid?.Trim();
            if (string.IsNullOrWhiteSpace(normalizedIccid))
                throw new ArgumentException("ICCID 不能为空。", nameof(iccid));

            var node = !string.IsNullOrWhiteSpace(proxyNodeId)
                ? ProxyPresets.FirstOrDefault(candidate => candidate.Id == proxyNodeId)
                : null;
            if (!string.IsNullOrWhiteSpace(proxyNodeId) && node == null)
                throw new InvalidOperationException("所选代理节点不存在或已被删除。");

            var route = IccidRoutes.FirstOrDefault(candidate =>
                string.Equals(candidate.Iccid, normalizedIccid, StringComparison.Ordinal));
            if (route == null)
            {
                route = new IccidRouteModel { Iccid = normalizedIccid };
                IccidRoutes.Add(route);
            }

            route.CardName = string.IsNullOrWhiteSpace(cardName) ? route.CardName : cardName.Trim();
            route.ImsiSnapshot = string.IsNullOrWhiteSpace(imsi) ? route.ImsiSnapshot : imsi.Trim();
            route.PhoneNumber = string.IsNullOrWhiteSpace(phoneNumber) ? route.PhoneNumber : phoneNumber.Trim();
            route.ProxyNodeId = node?.Id;
            route.ProxyUrl = null;
            route.ProxyNodeName = node?.Name ?? "直连模式 (Direct)";
            route.UpdatedAt = DateTime.Now;
            _ = Preferences.SaveIccidRouteAsync(route);

            AddLog("INFO", "Proxy Routing",
                $"Updated ICCID egress rule: ****{normalizedIccid[^Math.Min(4, normalizedIccid.Length)..]} -> {node?.Name ?? "Direct"}");
            RaiseEgressRoutesChanged();
        }

        public void RemoveIccidRoute(string iccid)
        {
            var normalizedIccid = iccid?.Trim();
            if (string.IsNullOrWhiteSpace(normalizedIccid)) return;

            var route = IccidRoutes.FirstOrDefault(candidate =>
                string.Equals(candidate.Iccid, normalizedIccid, StringComparison.Ordinal));
            if (route == null) return;

            IccidRoutes.Remove(route);
            _ = Preferences.DeleteIccidRouteAsync(normalizedIccid);
            AddLog("INFO", "Proxy Routing",
                $"Removed ICCID egress rule: ****{normalizedIccid[^Math.Min(4, normalizedIccid.Length)..]}; the card now follows its IMSI MCC/PLMN rule.");
            RaiseEgressRoutesChanged();
        }

        private void RaiseEgressRoutesChanged()
        {
            try { EgressRoutesChanged?.Invoke(); } catch { }
        }

        public void SaveCountryRoute(string countryCode, string? proxyNodeId)
        {
            var rule = CountryRoutes.FirstOrDefault(r => string.Equals(r.CountryCode, countryCode, StringComparison.OrdinalIgnoreCase));
            var node = !string.IsNullOrEmpty(proxyNodeId) ? ProxyPresets.FirstOrDefault(p => p.Id == proxyNodeId) : null;

            if (rule != null)
            {
                rule.ProxyNodeId = proxyNodeId;
                rule.ProxyNodeName = node != null ? $"{node.Name} ({node.Host}:{node.Port})" : "直连模式 (Direct)";
            }
            else
            {
                var meta = MccCountryHelper.FindByCode(countryCode);
                if (meta != null)
                {
                    rule = new CountryRouteModel
                    {
                        CountryCode = meta.Code,
                        CountryName = meta.Name,
                        FlagEmoji = meta.Flag,
                        MccList = string.Join(", ", meta.Mccs),
                        ProxyNodeId = proxyNodeId,
                        ProxyNodeName = node != null ? $"{node.Name} ({node.Host}:{node.Port})" : "直连模式 (Direct)"
                    };
                    CountryRoutes.Add(rule);
                }
            }

            if (rule != null)
            {
                _ = Preferences.SaveCountryRouteAsync(rule);
            }

            AddLog("INFO", "Proxy Routing", $"Updated country egress rule: {countryCode} -> {(node != null ? node.Name : "Direct")}");
            RaiseEgressRoutesChanged();
        }

        public void RemoveCountryRoute(string countryCode)
        {
            var rule = CountryRoutes.FirstOrDefault(r => string.Equals(r.CountryCode, countryCode, StringComparison.OrdinalIgnoreCase));
            if (rule != null)
            {
                CountryRoutes.Remove(rule);
                _ = Preferences.DeleteCountryRouteAsync(countryCode);
                AddLog("INFO", "Proxy Routing", $"Removed country rule: {countryCode}");
                RaiseEgressRoutesChanged();
            }
        }

        public void OnUsbDeviceInserted()
        {
            AddLog("INFO", "USB Monitor", "检测到 USB 设备插入事件，等待串口驱动完成初始化...");

            _usbDebounceCts?.Cancel();
            _usbDebounceCts = new CancellationTokenSource();
            _ = HandleUsbDeviceInsertedAsync(_usbDebounceCts.Token);
        }

        public void OnUsbDeviceRemoved()
        {
            AddLog("INFO", "USB Monitor", "检测到 USB 设备拔出事件，正在保留 VoWiFi 会话并释放硬件句柄...");
            _usbDebounceCts?.Cancel();
            _usbDebounceCts = new CancellationTokenSource();
            _ = HandleUsbDeviceRemovedAsync(_usbDebounceCts.Token);
        }

        private async Task HandleUsbDeviceRemovedAsync(CancellationToken token)
        {
            try
            {
                await Task.Delay(300, token).ConfigureAwait(false);
                var availablePorts = SerialPort.GetPortNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var slot in Kernel.GetSlots().Where(slot => !availablePorts.Contains(slot.PortName)))
                {
                    await slot.DetachHardwareAsync(token).ConfigureAwait(false);
                    AddLog("INFO", "USB Monitor",
                        $"模块 [{slot.Name}] 已拔出；现有 VoWiFi 隧道将继续保活，重新插入后会自动重新注册。");
                }
                await RunOnUiAsync(SyncSlots, DispatcherPriority.DataBind).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                AddLog("ERROR", "USB Monitor", $"USB 拔出处理失败: {ex.Message}");
            }
        }

        private async Task HandleUsbDeviceInsertedAsync(CancellationToken token)
        {
            try
            {
                await Task.Delay(800, token).ConfigureAwait(false);
                AddLog("INFO", "USB Monitor", "开始扫描新插入设备提供的串口...");

                var discovered = await DiscoverSlotsCoreAsync(token).ConfigureAwait(false);
                await RunOnUiAsync(SyncSlots, DispatcherPriority.DataBind).ConfigureAwait(false);
                AddLog("INFO", "USB Monitor", $"USB 扫描完成，当前卡槽数: {discovered.Count}");

                foreach (var slot in discovered)
                {
                    await ApplyPreferencesToSlotAsync(slot).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                AddLog("ERROR", "USB Monitor", $"USB 设备扫描失败: {ex.Message}");
            }
        }

        public Task ApplyPreferencesToSlotAsync(ModemSlot slot) =>
            ApplyPreferencesToSlotAsync(slot, restoreAutoVoWifi: true);

        private async Task ApplyPreferencesToSlotAsync(ModemSlot slot, bool restoreAutoVoWifi)
        {
            var applyGate = _preferenceApplyGates.GetOrAdd(slot.Id, _ => new SemaphoreSlim(1, 1));
            await applyGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var modPref = await Preferences.GetModulePreferenceAsync(slot.Id, slot.Imei);
                SimPreferenceModel? simPref = null;
                if (slot.Sim != null && !string.IsNullOrEmpty(slot.Sim.Iccid))
                {
                    simPref = await Preferences.GetSimPreferenceAsync(slot.Sim.Iccid);
                }

                if (modPref != null && !string.IsNullOrWhiteSpace(modPref.CustomName))
                {
                    slot.Name = modPref.CustomName.Trim();
                }
                if (slot.IsPcscReader && modPref?.Imei is { Length: > 0 } configuredImei)
                {
                    // ModulePreferences.Imei historically stores the modem's
                    // AT+CGSN value. For a PC/SC-only slot it stores the user
                    // supplied genuine terminal identity used by IMS.
                    slot.VoWifiImei = configuredImei;
                }
                slot.CardNickname = simPref?.CardNickname;

                // Routing is resolved from exactly two persistent tiers:
                // ICCID card rule first, then IMSI MCC/PLMN country rule.
                // Legacy module/default proxy fields are deliberately ignored
                // here so they cannot silently override the visible rule table.
                var resolvedProxy = ResolveEgressProxyForSlot(slot.Id);
                if (!Kernel.SetSlotProxy(slot.Id, resolvedProxy))
                {
                    Kernel.SetSlotProxy(slot.Id, null);
                    slot.ProxyUrl = null;
                    AddLog("WARN", "Proxy",
                        $"卡槽 [{slot.Name}] 命中的分流代理无效，已安全回退到直连；VoWiFi 仅允许 socks://、socks5:// 或 socks5h://。");
                }
                else
                {
                    slot.ProxyUrl = resolvedProxy;
                    AddLog("INFO", "Proxy", resolvedProxy is null
                        ? $"已按 ICCID > IMSI MCC/PLMN 规则解析卡槽 [{slot.Name}]：直连。"
                        : $"已按 ICCID > IMSI MCC/PLMN 规则解析卡槽 [{slot.Name}]：{DescribeProxyEndpoint(resolvedProxy)}");
                }

                ApplyVoWifiHomeIdentity(slot, simPref);

                // A SIM preference follows the card and overrides the module.
                // If no SIM preference exists, keep the hardware state instead
                // of manufacturing a false value during startup reconciliation.
                bool? preferredFlightMode = simPref?.DefaultFlightMode ?? modPref?.DefaultFlightMode;
                if (!slot.IsPcscReader && preferredFlightMode.HasValue &&
                    (!slot.IsFlightModeKnown || slot.IsFlightMode != preferredFlightMode.Value))
                {
                    try
                    {
                        var applied = await slot.SetFlightModeAsync(preferredFlightMode.Value).ConfigureAwait(false);
                        if (!applied)
                        {
                            AddLog("WARN", "Preferences", $"无法为卡槽 [{slot.Name}] 恢复飞行模式偏好。");
                        }
                        else
                        {
                            AddLog("INFO", "Preferences", preferredFlightMode.Value
                                ? $"已为卡槽 [{slot.Name}] 恢复飞行模式，蜂窝射频已关闭。"
                                : $"已为卡槽 [{slot.Name}] 恢复正常模式，蜂窝射频已开启。");
                        }
                    }
                    catch (Exception ex)
                    {
                        AddLog("WARN", "Preferences", $"恢复飞行模式偏好失败，继续处理其他设置: {ex.Message}");
                    }
                }

                if (!slot.IsPcscReader && slot.IsFlightModeKnown && !slot.IsFlightMode && (simPref != null || modPref != null))
                {
                    bool? preferredRoaming = simPref?.DefaultDataRoaming ?? modPref?.DefaultDataRoaming;
                    if (preferredRoaming.HasValue)
                    {
                        try
                        {
                            var applied = await slot.SetDataRoamingEnabledAsync(preferredRoaming.Value).ConfigureAwait(false);
                            AddLog(applied ? "INFO" : "WARN", "Preferences", applied
                                ? $"已为卡槽 [{slot.Name}] 恢复数据漫游偏好: {(preferredRoaming.Value ? "允许" : "禁止")}。"
                                : $"模组未确认数据漫游偏好，可能不支持 Quectel 漫游指令。");
                        }
                        catch (Exception ex)
                        {
                            AddLog("WARN", "Preferences", $"恢复数据漫游偏好失败，继续处理其他设置: {ex.Message}");
                        }
                    }

                    bool? preferredCellularData = simPref?.DefaultCellularData ?? modPref?.DefaultCellularData;
                    if (preferredCellularData.HasValue)
                    {
                        try
                        {
                            var applied = await slot.SetCellularDataEnabledAsync(preferredCellularData.Value).ConfigureAwait(false);
                            AddLog(applied ? "INFO" : "WARN", "Preferences", applied
                                ? $"已为卡槽 [{slot.Name}] 恢复蜂窝数据偏好: {(preferredCellularData.Value ? "开启" : "关闭")}。"
                                : $"模组未确认蜂窝数据偏好 (CGATT)。");
                        }
                        catch (Exception ex)
                        {
                            AddLog("WARN", "Preferences", $"恢复蜂窝数据偏好失败，继续处理 VoWiFi: {ex.Message}");
                        }
                    }

                }

                if (!slot.IsPcscReader)
                {
                    try { await slot.RefreshMetricsAsync().ConfigureAwait(false); }
                    catch (Exception ex) { AddLog("WARN", "Preferences", $"偏好恢复后的网络刷新失败: {ex.Message}"); }
                }

                if (restoreAutoVoWifi && (simPref != null || modPref != null))
                {
                    var autoVoWifi = simPref?.DefaultVoWifi ?? modPref?.DefaultVoWifi ?? false;
                    if (autoVoWifi)
                    {
                        QueueAutoVoWifiStart(slot);
                    }
                    else if (slot.VoWifi.State != VoWifiState.Disconnected)
                    {
                        await slot.StopVoWifiAsync().ConfigureAwait(false);
                        AddLog("INFO", "VoWiFi", $"已按卡槽 [{slot.Name}] 的关闭偏好停止自动 VoWiFi。");
                    }
                }

                _ = SyncModemSmsAsync(slot);
            }
            catch (Exception ex)
            {
                AddLog("WARN", "Preferences", $"Apply preferences failed: {ex.Message}");
            }
            finally
            {
                applyGate.Release();
            }
        }

        private static string DescribeProxyEndpoint(string proxyUrl)
        {
            if (Uri.TryCreate(proxyUrl, UriKind.Absolute, out var uri))
                return $"{uri.Scheme}://{uri.Host}:{uri.Port}";
            return proxyUrl;
        }

        private void ApplyVoWifiHomeIdentity(ModemSlot slot, SimPreferenceModel? simPref)
        {
            var reported = slot.Sim;
            var savedImsi = simPref?.Imsi?.Trim();
            if (reported == null || string.IsNullOrWhiteSpace(savedImsi) ||
                savedImsi.Equals(reported.Imsi, StringComparison.Ordinal))
            {
                return;
            }

            // The preference record is selected by the live ICCID. Preserve a
            // previously observed permanent IMSI for a multi-IMSI profile without
            // consulting a bundled carrier/ICCID table.
            if (savedImsi.Length is < 5 or > 16 || !savedImsi.All(char.IsAsciiDigit))
                return;
            var learnedIdentity = SimIdentity.FromImsiAndIccid(
                savedImsi,
                reported.Iccid,
                reported.OperatorName,
                reported.PhoneNumber,
                reported.IsHomePlmnAuthoritative ? reported.Mnc.Length : null,
                reported.HomePlmns);
            slot.RememberVoWifiIdentity(learnedIdentity);
            AddLog("INFO", "VoWiFi", $"检测到临时 IMSI，已为卡槽 [{slot.Name}] 恢复已验证的归属网络 EAP-AKA 身份。");
        }

        private void QueueAutoVoWifiStart(ModemSlot slot)
        {
            if (slot.VoWifi.State is VoWifiState.ConnectingIkev2 or VoWifiState.AuthenticatingEapAka or
                VoWifiState.IpsecTunnelEstablished or VoWifiState.ImsRegistering or VoWifiState.ImsRegistered)
                return;
            if (!_autoVoWifiStarts.TryAdd(slot.Id, 0)) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    AddLog("INFO", "VoWiFi", $"卡槽 [{slot.Name}] 已启用自动 VoWiFi，正在注册…");
                    var started = await StartVoWifiAsync(slot.Id).ConfigureAwait(false);
                    if (!started)
                        AddLog("WARN", "VoWiFi", $"卡槽 [{slot.Name}] 自动 VoWiFi 注册失败，请查看 VoWiFi 日志。 ");
                }
                catch (Exception ex)
                {
                    AddLog("WARN", "VoWiFi", $"卡槽 [{slot.Name}] 自动 VoWiFi 启动异常: {ex.Message}");
                }
                finally
                {
                    _autoVoWifiStarts.TryRemove(slot.Id, out _);
                }
            });
        }

        public async Task SyncModemSmsAsync(ModemSlot slot, CancellationToken cancellationToken = default)
        {
            if (slot.IsPcscReader)
            {
                // Inbound/outbound SMS uses the IMS SIP path for a PC/SC slot;
                // there is no modem message store to enumerate.
                return;
            }
            if (slot?.Sms == null) return;
            if (!_syncingSlots.TryAdd(slot.Id, 0)) return;

            AddLog("INFO", "SMS", $"Starting SMS synchronization for slot [{slot.Name}] ({slot.Id})...");
            try
            {
                // 1. Fetch all SMS stored on the module (both SIM and module memory)
                var messages = await slot.Sms.ListSmsAsync(SmsStatus.All, cancellationToken).ConfigureAwait(false);
                AddLog("INFO", "SMS", $"Found {messages.Count} stored messages on modem [{slot.Name}].");

                int importedCount = 0;
                foreach (var msg in messages)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    bool isOutgoing = msg.Direction == SmsDirection.Submitted;
                    var remote = !string.IsNullOrWhiteSpace(msg.SenderOrRecipient) ? msg.SenderOrRecipient : "Unknown";
                    var normalizedTimestamp = isOutgoing
                        ? TimestampDisplayHelper.ToUtcStorageTime(msg.Timestamp)
                        : TimestampDisplayHelper.NormalizeIncomingNetworkTime(msg.Timestamp);

                    bool exists = await Preferences.HasSmsMessageAsync(remote, normalizedTimestamp, msg.Text, msg.RawPdu).ConfigureAwait(false);
                    if (!exists)
                    {
                        var model = new SmsMessageModel
                        {
                            Index = msg.Index,
                            SlotId = slot.Id,
                            SenderOrRecipient = remote,
                            Text = msg.Text,
                            Timestamp = normalizedTimestamp,
                            IsOutgoing = isOutgoing,
                            DeliveryState = isOutgoing ? SmsDeliveryState.Sent : SmsDeliveryState.Received,
                            DeliveryStatus = msg.DeliveryStatus,
                            MessageReference = msg.MessageReference,
                            RawPdu = msg.RawPdu
                        };

                        await Preferences.SaveSmsMessageAsync(model).ConfigureAwait(false);

                        await RunOnUiAsync(() =>
                        {
                            var conv = GetOrCreateConversation(remote);
                            if (!conv.Messages.Any(m => m.Timestamp == model.Timestamp && m.Text == model.Text))
                            {
                                conv.Messages.Add(model);
                                if (!model.IsOutgoing)
                                {
                                    conv.UnreadCount++;
                                }
                                conv.UpdateLastMessage();
                                MoveConversationToTop(conv);
                            }
                        }).ConfigureAwait(false);

                        importedCount++;
                    }
                }

                if (importedCount > 0)
                {
                    AddLog("INFO", "SMS", $"Imported {importedCount} new messages from modem [{slot.Name}] into local SQLite.");
                }

                // 2. Delete all messages from modem storages to prevent card storage from filling up
                if (slot.Modem != null && slot.Modem.IsOpen)
                {
                    var storages = new[] { "\"SM\",\"SM\",\"SM\"", "\"ME\",\"ME\",\"ME\"" };
                    foreach (var st in storages)
                    {
                        try
                        {
                            await slot.Modem.SendRawAtCommandAsync($"AT+CPMS={st}", 2000, cancellationToken).ConfigureAwait(false);
                            var delResp = await slot.Modem.SendRawAtCommandAsync("AT+CMGD=1,4", 5000, cancellationToken).ConfigureAwait(false);
                            if (!delResp.Success)
                            {
                                foreach (var m in messages)
                                {
                                    if (m.Index > 0)
                                    {
                                        await slot.Modem.SendRawAtCommandAsync($"AT+CMGD={m.Index}", 2000, cancellationToken).ConfigureAwait(false);
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            AddLog("WARN", "SMS", $"Clear storage {st} failed: {ex.Message}");
                        }
                    }

                    // Restore preferred storage to ME
                    try
                    {
                        await slot.Modem.SendRawAtCommandAsync("AT+CPMS=\"ME\",\"ME\",\"ME\"", 2000, cancellationToken).ConfigureAwait(false);
                    }
                    catch { }

                    AddLog("INFO", "SMS", $"Modem [{slot.Name}] SMS storage cleaned up.");
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                AddLog("ERROR", "SMS", $"Modem SMS sync failed for [{slot.Name}]: {ex.Message}");
            }
            finally
            {
                _syncingSlots.TryRemove(slot.Id, out _);
            }
        }

        public async Task SaveModulePreferencesAsync(string slotId, bool flightMode, bool vowifi, bool cellularData, bool roaming, string? proxyUrl, string? customName = null)
        {
            var slot = Kernel.GetSlots().FirstOrDefault(s => s.Id == slotId);
            var normalizedName = string.IsNullOrWhiteSpace(customName) ? null : customName.Trim();
            var model = new ModulePreferenceModel
            {
                Id = slotId,
                Imei = slot?.IsPcscReader == true ? slot.VoWifiImei : slot?.Imei,
                PortName = slot?.PortName,
                CustomName = normalizedName,
                DefaultFlightMode = flightMode,
                DefaultVoWifi = vowifi,
                DefaultCellularData = cellularData,
                DefaultDataRoaming = roaming,
                DefaultProxyUrl = proxyUrl,
                BaudRate = slot?.BaudRate ?? 115200,
                LastSeenAt = DateTime.Now
            };
            await Preferences.SaveModulePreferenceAsync(model);
            if (slot != null)
            {
                slot.Name = normalizedName ?? $"Slot {slot.Id} ({slot.PortName})";
            }
            AddLog("INFO", "Preferences", $"Saved module preference for [{slotId}]");
        }

        public async Task SaveSimPreferencesAsync(string iccid, bool flightMode, bool vowifi, bool cellularData, bool roaming, string? proxyUrl, string? nickname)
        {
            var normalizedNickname = string.IsNullOrWhiteSpace(nickname) ? null : nickname.Trim();
            var slot = Kernel.GetSlots().FirstOrDefault(s =>
                string.Equals(s.Sim?.Iccid, iccid, StringComparison.Ordinal));
            var model = new SimPreferenceModel
            {
                Iccid = iccid,
                Imsi = slot?.Sim?.Imsi,
                CardNickname = normalizedNickname,
                DefaultFlightMode = flightMode,
                DefaultVoWifi = vowifi,
                DefaultCellularData = cellularData,
                DefaultDataRoaming = roaming,
                DedicatedProxyUrl = proxyUrl,
                LastSeenAt = DateTime.Now
            };
            await Preferences.SaveSimPreferenceAsync(model);
            if (slot != null)
            {
                slot.CardNickname = normalizedNickname;
            }
            AddLog("INFO", "Preferences", $"Saved sim preference for [{iccid}]");
        }

        private int _serviceDisposed;
        public async ValueTask DisposeAsync()
        {
            StopMicrophone();
            await StopCellularAudioBridgeAsync();
            var audioCts = Interlocked.Exchange(ref _cellularCallAudioCts, null);
            audioCts?.Cancel();
            audioCts?.Dispose();
            await StopQdc507VoiceRouteAsync();
            await _cellularAudioBridge.DisposeAsync();
            if (Interlocked.Exchange(ref _serviceDisposed, 1) != 0)
            {
                return;
            }

            try
            {
                _usbDebounceCts?.Cancel();
            }
            catch { }
            try
            {
                _usbDebounceCts?.Dispose();
            }
            catch { }

            _logLines.Writer.TryComplete();
            try
            {
                await _logWriterTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch
            {
                _logWriterCts.Cancel();
            }
            _logWriterCts.Dispose();

            try
            {
                await Kernel.DisposeAsync();
            }
            catch { }
        }
        private VoSharp.Telephony.Calls.RtpSession? GetActiveRtpSession()
        {
            if (Kernel is VoKernel k)
            {
                if (k.VoWifi?.ActiveRtpSession != null) return k.VoWifi.ActiveRtpSession;
                foreach (var slot in k.Pool.Slots.Values)
                {
                    if (slot.VoWifi?.ActiveRtpSession != null)
                        return slot.VoWifi.ActiveRtpSession;
                }
            }
            return null;
        }

        private void StartMicrophone()
        {
            try
            {
                if (_waveIn != null) StopMicrophone();
                _waveIn = new NAudio.Wave.WaveIn
                {
                    WaveFormat = new NAudio.Wave.WaveFormat(8000, 16, 1),
                    BufferMilliseconds = 20
                };
                _waveIn.DataAvailable += (s, a) =>
                {
                    var rtp = GetActiveRtpSession();
                    if (rtp != null && a.BytesRecorded > 0)
                    {
                        int sampleCount = a.BytesRecorded / 2;
                        short[] samples = new short[sampleCount];
                        Buffer.BlockCopy(a.Buffer, 0, samples, 0, a.BytesRecorded);
                        rtp.SendAudioPcm(samples);
                    }
                };
                _waveIn.StartRecording();
                AddLog("INFO", "Audio", "Microphone capture started for active call.");
            }
            catch (Exception ex)
            {
                AddLog("WARN", "Audio", $"Failed to start microphone: {ex.Message}");
            }
        }

        private void StopMicrophone()
        {
            if (_waveIn != null)
            {
                try
                {
                    _waveIn.StopRecording();
                    _waveIn.Dispose();
                }
                catch { }
                _waveIn = null;
                AddLog("INFO", "Audio", "Microphone capture stopped.");
            }
        }

        public Task<CallExperienceSettings> GetCallExperienceSettingsAsync() => Task.FromResult(new CallExperienceSettings
        {
            VolteAudioPrewarmEnabled = _callExperience.VolteAudioPrewarmEnabled,
            SaveCallRecordings = _callExperience.SaveCallRecordings,
            RecordingDirectory = _callExperience.RecordingDirectory,
            AutoAnswerEnabled = _callExperience.AutoAnswerEnabled,
            AutoAnswerDelaySeconds = _callExperience.AutoAnswerDelaySeconds,
            AutoAnswerMessagePath = _callExperience.AutoAnswerMessagePath
        });

        public async Task SaveCallExperienceSettingsAsync(CallExperienceSettings settings)
        {
            settings.AutoAnswerDelaySeconds = Math.Clamp(settings.AutoAnswerDelaySeconds, 5, 120);
            _callExperience = settings;
            await _callExperienceStore.SaveAsync(settings);
            ApplyVolteAudioPrewarmPreference();
            ApplyCallRecordingPreference();
        }

        private void ApplyVolteAudioPrewarmPreference()
        {
            if (!_callExperience.VolteAudioPrewarmEnabled)
            {
                _callAlerting.ReleaseHostAudio();
                return;
            }

            if (CurrentCallState is CallState.Idle or CallState.Ended or CallState.Incoming)
                _callAlerting.PrewarmHostAudio();
        }

        private void ApplyCallRecordingPreference()
        {
            var directory = _callExperience.RecordingDirectory;
            Kernel.Calls.ConfigureAudioRecording(_callExperience.SaveCallRecordings, directory);
            foreach (var slot in Kernel.GetSlots())
                slot.Calls.ConfigureAudioRecording(_callExperience.SaveCallRecordings, directory);
        }

        private void StartIncomingAlertingAndAutoAnswer(string? slotId)
        {
            if (Volatile.Read(ref _incomingAutoAnswerCts) != null) return;
            ApplyVolteAudioPrewarmPreference();
            _callAlerting.StartRinging();
            var cts = new CancellationTokenSource();
            if (Interlocked.CompareExchange(ref _incomingAutoAnswerCts, cts, null) != null) { cts.Dispose(); return; }
            if (!_callExperience.AutoAnswerEnabled) return;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(_callExperience.AutoAnswerDelaySeconds), cts.Token);
                    if (cts.IsCancellationRequested || !HasIncomingCall || CurrentCallState != CallState.Incoming) return;
                    var message = _callExperience.AutoAnswerMessagePath;
                    _pendingAutoAnswerMessagePath = !string.IsNullOrWhiteSpace(message) && File.Exists(message) ? message : null;
                    AddLog("INFO", "Calls", "Incoming call timed out; answering automatically.");
                    await AnswerAsync(slotId);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { AddLog("WARN", "Calls", $"Auto-answer failed: {ex.Message}"); }
            });
        }

        private void StopIncomingAlerting()
        {
            var cts = Interlocked.Exchange(ref _incomingAutoAnswerCts, null);
            cts?.Cancel();
            cts?.Dispose();
            _callAlerting.StopRinging();
        }

        private async Task<bool> StartCellularAudioBridgeAsync(string? messagePath, CancellationToken cancellationToken)
        {
            try
            {
                await _cellularAudioBridge.StartAsync(messagePath, cancellationToken);
                AddLog("INFO", "CellularAudio", "PC microphone/speaker connected to modem USB Audio Class endpoints.");
                return true;
            }
            catch (Exception ex)
            {
                AddLog("ERROR", "CellularAudio", $"Failed to start cellular audio bridge: {ex.Message}");
                return false;
            }
        }

        private async Task StopCellularAudioBridgeAsync()
        {
            try
            {
                var wasRunning = _cellularAudioBridge.IsRunning;
                await _cellularAudioBridge.StopAsync();
                if (wasRunning)
                    AddLog("INFO", "CellularAudio", "Cellular USB audio bridge stopped.");
            }
            catch (Exception ex)
            {
                AddLog("WARN", "CellularAudio", $"Failed to stop cellular audio bridge: {ex.Message}");
            }
        }

        private async Task PrepareQdc507VoiceRuntimeAsync()
        {
            try
            {
                await _qdc507VoiceRuntime.PrepareAsync();
                AddLog("INFO", "CellularAudio", "QDC507 module voice drivers and VoLTE calibration are ready.");
            }
            catch (Exception ex)
            {
                AddLog("INFO", "CellularAudio", $"QDC507 runtime is not available: {ex.Message}");
            }
        }

        private async Task StartCellularCallAudioAsync(bool standardUacReady, string? messagePath, CancellationToken cancellationToken)
        {
            PostToUi(StopMicrophone);
            if (!standardUacReady)
            {
                try
                {
                    AddLog("INFO", "CellularAudio", "Starting QDC507 D4/UAC VoLTE route...");
                    await _qdc507VoiceRuntime.StartRouteAsync(cancellationToken);
                    AddLog("INFO", "CellularAudio", "QDC507 D4/UAC VoLTE route is RUNNING.");
                    // QDC507 briefly republishes its UAC stream after audio_enable=1.
                    // Opening the old endpoint immediately succeeds but remains a zero stream.
                    // On QDC507 the D4 endpoint accepts an early open but then
                    // remains a zero stream for the whole call. The module needs
                    // roughly six seconds after audio_enable before MME opens it.
                    await Task.Delay(TimeSpan.FromSeconds(7), cancellationToken);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    AddLog("ERROR", "CellularAudio",
                        $"The modem rejected AT+QPCMV and the QDC507 runtime route could not start: {ex.Message}");
                    CallMediaStatusChanged?.Invoke("Cellular/Audio unavailable");
                    return;
                }
            }

            // audio_enable=1 can briefly re-enumerate the Windows UAC endpoints.
            for (var attempt = 0; attempt < 20; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var started = await StartCellularAudioBridgeAsync(messagePath, cancellationToken);
                if (started)
                {
                    AddLog("INFO", "CellularAudio", $"Audio devices: {_cellularAudioBridge.DeviceSummary}");
                    CallMediaStatusChanged?.Invoke(standardUacReady
                        ? "Cellular/UAC 8 kHz"
                        : "Cellular/QDC507 UAC 8 kHz");
                    _ = MonitorCellularAudioLevelsAsync(cancellationToken);
                    return;
                }
                await Task.Delay(250, cancellationToken);
            }
            AddLog("ERROR", "CellularAudio", "QDC507 voice route is active, but Windows UAC endpoints did not become ready.");
            CallMediaStatusChanged?.Invoke("Cellular/Windows audio unavailable");
        }

        private async Task MonitorCellularAudioLevelsAsync(CancellationToken cancellationToken)
        {
            var reopenedZeroStream = false;
            try
            {
                for (var sample = 0; sample < 4; sample++)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                    var levels = _cellularAudioBridge.ReadAndResetPeaks();
                    AddLog("INFO", "CellularAudio",
                        $"Live PCM peaks: PC microphone -> modem {levels.HostMicrophonePeak}, modem -> PC speaker {levels.ModemDownlinkPeak}.");

                    if (!reopenedZeroStream && sample == 0 && levels.ModemDownlinkPeak <= 1)
                    {
                        reopenedZeroStream = true;
                        AddLog("WARN", "CellularAudio", "Modem UAC is still a zero stream; reopening endpoints after enumeration settled.");
                        await StartCellularAudioBridgeAsync(null, cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        private async Task StopQdc507VoiceRouteAsync()
        {
            try { await _qdc507VoiceRuntime.StopRouteAsync(); }
            catch (Exception ex) { AddLog("WARN", "CellularAudio", $"Failed to stop QDC507 voice route: {ex.Message}"); }
        }
    }
}
