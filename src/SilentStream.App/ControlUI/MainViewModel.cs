using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SilentStream.Core.Contracts;
using SilentStream.Core.Hotkeys;
using SilentStream.Core.Implementations;
using SilentStream.Core.Logging;
using SilentStream.Core.Models;

namespace SilentStream.App.ControlUI;

/// <summary>
/// ViewModel behind the control UI (plan §3.8): state badge, start/stop, performance, the
/// multi-source audio mixer (per-source gain/mute/gate + realtime meters), master audio filters,
/// mic-silence warning, capture monitor/region, resource limit, recording panel, log viewer,
/// settings. Couples to other modules through Core/Contracts interfaces only.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly IStreamOrchestrator _orchestrator;
    private readonly IAudioMixer _audioMixer;
    private readonly IScreenCaptureSource _capture;
    private readonly IPreviewProvider _preview;
    private readonly IRecordingManager _recordingManager;
    private readonly IConfigStore _configStore;
    private readonly ITokenProtector _tokenProtector;
    private readonly INotifier _notifier;
    private readonly ILogService _log;

    /// <summary>Shown instead of the stored bot token — the plaintext is never bound back into the UI.</summary>
    private const string TokenPlaceholder = "********";

    private CancellationTokenSource? _startCts;
    private readonly HashSet<string> _silentMics = [];
    private bool _loadingAudio;

    // Coalesces config writes from gain-slider drags (which fire dozens of ticks) so we don't
    // do a synchronous load+save per tick on the UI thread.
    private readonly DispatcherTimer _persistDebounce = new() { Interval = TimeSpan.FromMilliseconds(600) };

    // Guards LogLines so it can be fed from background logging threads while WPF's
    // virtualizing ListBox reads it on the UI thread (BindingOperations.EnableCollectionSynchronization).
    private readonly object _logSync = new();

    public MainViewModel(
        IStreamOrchestrator orchestrator,
        IAudioMixer audioMixer,
        IScreenCaptureSource capture,
        IPreviewProvider preview,
        IRecordingManager recordingManager,
        IConfigStore configStore,
        ITokenProtector tokenProtector,
        INotifier notifier,
        ILogService log)
    {
        _orchestrator = orchestrator;
        _audioMixer = audioMixer;
        _capture = capture;
        _preview = preview;
        _recordingManager = recordingManager;
        _configStore = configStore;
        _tokenProtector = tokenProtector;
        _notifier = notifier;
        _log = log;

        var config = _configStore.Load();
        _selectedResourceLimit = config.Encoding.ResourceLimit;
        _resolution = config.Encoding.Resolution;
        _fps = config.Encoding.Fps;
        _videoBitrateKbps = config.Encoding.VideoBitrateKbps;
        _audioBitrateKbps = config.Encoding.AudioBitrateKbps;
        _hotkeyText = config.Hotkey;
        _autostartMethod = config.Autostart;
        _roomName = config.DeviceName ?? string.Empty;
        _showStatusBox = config.ShowStatusBox;
        _recordingFolder = config.Recording.Folder;
        _maxSizeGb = config.Recording.MaxSizeGb;
        _retentionDays = config.Recording.RetentionDays;

        // ---- 텔레그램 알림 (Phase 1): 저장된 토큰은 플레이스홀더로만 표시 ----
        _telegramEnabled = config.Notifications.Enabled;
        _telegramBotToken = string.IsNullOrEmpty(config.Notifications.TelegramBotTokenEnc)
                            && string.IsNullOrEmpty(config.Notifications.TelegramBotToken)
            ? string.Empty
            : TokenPlaceholder;
        _telegramChatId = config.Notifications.TelegramChatId;
        var notifyLevel = (config.Notifications.NotifyLevel ?? string.Empty).Trim().ToLowerInvariant();
        _telegramNotifyLevel = TelegramNotifyLevels.Contains(notifyLevel) ? notifyLevel : "warn";

        // ---- audio master filters (apply next session) ----
        _noiseSuppressionEnabled = config.Audio.Filters.NoiseSuppressionEnabled;
        _noiseSuppressionDb = config.Audio.Filters.NoiseSuppressionDb;
        _compressorEnabled = config.Audio.Filters.CompressorEnabled;
        _limiterEnabled = config.Audio.Filters.LimiterEnabled;
        _masterGainDb = config.Audio.Filters.MasterGainDb;

        // ---- capture monitor / region ----
        _regionX = config.Capture.RegionX;
        _regionY = config.Capture.RegionY;
        _regionWidth = config.Capture.RegionWidth;
        _regionHeight = config.Capture.RegionHeight;

        StartCommand = new RelayCommand(Start, () => _orchestrator.State == StreamState.Idle);
        StopCommand = new RelayCommand(Stop, () => _orchestrator.State is not (StreamState.Idle or StreamState.Stopping));
        SaveSettingsCommand = new RelayCommand(SaveSettings);
        RefreshDevicesCommand = new RelayCommand(RefreshDevices);
        AddMicCommand = new RelayCommand(AddMic);
        SendTestAlertCommand = new RelayCommand(SendTestAlert);
        _persistDebounce.Tick += (_, _) => PersistAudioSources();

        _orchestrator.StateChanged += (_, state) => OnUi(() => ApplyState(state));
        _orchestrator.MetricsUpdated += (_, metrics) => OnUi(() => ApplyMetrics(metrics));
        _audioMixer.LevelsUpdated += (_, levels) => OnUi(() => ApplyLevels(levels));
        _audioMixer.MicSignalChanged += (_, status) => OnUi(() => ApplyMicSignal(status));
        _preview.FrameUpdated += () => OnUi(UpdatePreview);

        // Let WPF synchronise on _logSync so log lines appended from background
        // threads don't desync the virtualizing ListBox's item generator.
        BindingOperations.EnableCollectionSynchronization(LogLines, _logSync);
        lock (_logSync)
        {
            foreach (var line in InMemoryLogSink.Snapshot())
            {
                LogLines.Add(line);
            }
        }
        // Marshal to the UI thread: appending raises CollectionChanged, whose handler in
        // ControlWindow touches the ListBox's CollectionView (UI-thread affinity). Logging
        // fires from background threads (capture/encoder/orchestrator), so doing this off
        // the UI thread throws a cross-thread exception straight back into the *caller* —
        // e.g. faulting StreamOrchestrator.StartAsync and silently killing auto-start.
        InMemoryLogSink.LineAdded += line => OnUi(() =>
        {
            lock (_logSync)
            {
                LogLines.Add(line);
                while (LogLines.Count > InMemoryLogSink.Capacity)
                {
                    LogLines.RemoveAt(0);
                }
            }
        });

        RefreshDevices();
        LoadAudioSources(config);
        LoadMonitors(config);
        RefreshRecordingStatus();
        ApplyState(_orchestrator.State);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    // ---- 상태 배지 ----
    private string _stateBadge = "대기";
    public string StateBadge { get => _stateBadge; private set => Set(ref _stateBadge, value); }

    private string _stateColor = "#E74C3C";
    public string StateColor { get => _stateColor; private set => Set(ref _stateColor, value); }

    // ---- 성능 ----
    private string _bitrateText = "0 kbps";
    public string BitrateText { get => _bitrateText; private set => Set(ref _bitrateText, value); }

    private string _fpsText = "0 fps";
    public string FpsText { get => _fpsText; private set => Set(ref _fpsText, value); }

    private string _cpuText = "-";
    public string CpuText { get => _cpuText; private set => Set(ref _cpuText, value); }

    private string _gpuText = "-";
    public string GpuText { get => _gpuText; private set => Set(ref _gpuText, value); }

    // ---- 오디오 믹서 (다중 소스) ----
    public ObservableCollection<AudioSourceViewModel> AudioSources { get; } = [];
    public ObservableCollection<AudioDeviceInfo> MicDevices { get; } = [];

    private double _masterMeterRms;
    public double MasterMeterRms { get => _masterMeterRms; private set => Set(ref _masterMeterRms, value); }

    private double _masterMeterPeak;
    public double MasterMeterPeak { get => _masterMeterPeak; private set => Set(ref _masterMeterPeak, value); }

    private string _masterMeterBrush = "#2ECC40";
    public string MasterMeterBrush { get => _masterMeterBrush; private set => Set(ref _masterMeterBrush, value); }

    private bool _micWarningVisible;
    public bool MicWarningVisible { get => _micWarningVisible; private set => Set(ref _micWarningVisible, value); }

    private string _micWarningText = "";
    public string MicWarningText { get => _micWarningText; private set => Set(ref _micWarningText, value); }

    // ---- 오디오 마스터 필터 (다음 세션부터 적용) ----
    private bool _noiseSuppressionEnabled;
    public bool NoiseSuppressionEnabled
    {
        get => _noiseSuppressionEnabled;
        set { if (Set(ref _noiseSuppressionEnabled, value)) PersistFilters(); }
    }

    private int _noiseSuppressionDb;
    public int NoiseSuppressionDb
    {
        get => _noiseSuppressionDb;
        set { if (Set(ref _noiseSuppressionDb, value)) PersistFilters(); }
    }

    private bool _compressorEnabled;
    public bool CompressorEnabled
    {
        get => _compressorEnabled;
        set { if (Set(ref _compressorEnabled, value)) PersistFilters(); }
    }

    private bool _limiterEnabled;
    public bool LimiterEnabled
    {
        get => _limiterEnabled;
        set { if (Set(ref _limiterEnabled, value)) PersistFilters(); }
    }

    private double _masterGainDb;
    public double MasterGainDb
    {
        get => _masterGainDb;
        set { if (Set(ref _masterGainDb, value)) { Raise(nameof(MasterGainText)); PersistFilters(); } }
    }

    public string MasterGainText => $"{_masterGainDb:+0.0;-0.0;0.0} dB";

    // ---- 캡처 모니터 / 영역 ----
    public ObservableCollection<MonitorInfo> Monitors { get; } = [];

    private MonitorInfo? _selectedMonitor;
    public MonitorInfo? SelectedMonitor
    {
        get => _selectedMonitor;
        set
        {
            if (Set(ref _selectedMonitor, value) && value is not null && !_loadingAudio)
            {
                _configStore.Update(config => config.Capture.MonitorIndex = value.Index);
                _log.Info($"캡처 모니터 변경: {value.Name} ({value.Width}x{value.Height}) — 다음 세션부터 적용");
            }
        }
    }

    private int _regionX;
    public int RegionX { get => _regionX; set => Set(ref _regionX, value); }

    private int _regionY;
    public int RegionY { get => _regionY; set => Set(ref _regionY, value); }

    private int _regionWidth;
    public int RegionWidth { get => _regionWidth; set => Set(ref _regionWidth, value); }

    private int _regionHeight;
    public int RegionHeight { get => _regionHeight; set => Set(ref _regionHeight, value); }

    // ---- 송출 미리보기 ----
    private ImageSource? _previewImage;
    public ImageSource? PreviewImage { get => _previewImage; private set => Set(ref _previewImage, value); }

    // ---- 인코딩 (다음 세션부터 적용) ----
    public string[] ResolutionOptions { get; } = ["source", "1920x1080", "1280x720", "854x480"];

    private string _resolution;
    public string Resolution { get => _resolution; set => Set(ref _resolution, value); }

    public string[] FpsOptions { get; } = ["source", "30", "24", "15"];

    private string _fps;
    public string Fps { get => _fps; set => Set(ref _fps, value); }

    private int _videoBitrateKbps;
    /// <summary>Manual video bitrate (kbps); 0 = auto.</summary>
    public int VideoBitrateKbps { get => _videoBitrateKbps; set => Set(ref _videoBitrateKbps, value); }

    private int _audioBitrateKbps;
    public int AudioBitrateKbps { get => _audioBitrateKbps; set => Set(ref _audioBitrateKbps, value); }

    // ---- 자원 제한 ----
    public string[] ResourceLimits { get; } = ["25", "50", "75", "none"];

    private string _selectedResourceLimit;
    public string SelectedResourceLimit
    {
        get => _selectedResourceLimit;
        set
        {
            if (Set(ref _selectedResourceLimit, value))
            {
                _configStore.Update(config => config.Encoding.ResourceLimit = value);
                _log.Info($"자원 사용 제한 변경: {value} (다음 세션부터 적용, 근사 상한)");
            }
        }
    }

    // ---- 녹화 패널 ----
    private string _recordingFileText = "-";
    public string RecordingFileText { get => _recordingFileText; private set => Set(ref _recordingFileText, value); }

    private string _recordingUsageText = "-";
    public string RecordingUsageText { get => _recordingUsageText; private set => Set(ref _recordingUsageText, value); }

    private string _freeDiskText = "-";
    public string FreeDiskText { get => _freeDiskText; private set => Set(ref _freeDiskText, value); }

    // ---- 폰 원격 제어 ----
    private string _remoteStatusText = "원격 제어가 꺼져 있거나 시작 중입니다 (설정: remote.mode).";
    public string RemoteStatusText { get => _remoteStatusText; private set => Set(ref _remoteStatusText, value); }

    private string _remotePinText = "";
    public string RemotePinText { get => _remotePinText; private set => Set(ref _remotePinText, value); }

    /// <summary>Called by App when the remote server emits a fresh pairing PIN.</summary>
    public void SetRemotePin(string pin) => OnUi(() =>
    {
        RemoteStatusText = "폰 브라우저로 접속 후 아래 PIN을 입력해 페어링하세요.";
        RemotePinText = $"PIN: {pin}";
    });

    private string _remoteUrlText = "";
    public string RemoteUrlText { get => _remoteUrlText; private set => Set(ref _remoteUrlText, value); }

    /// <summary>Called by App when the Cloudflare tunnel reports the public phone URL (mode=cloudflare).</summary>
    public void SetRemoteUrl(string url) => OnUi(() =>
    {
        RemoteStatusText = "폰 브라우저로 아래 주소를 열고 PIN을 입력해 페어링하세요.";
        RemoteUrlText = $"주소: {url}";
    });

    // ---- 버전 / 자동 업데이트 상태 ----
    private string _versionText = "";
    /// <summary>현재 실행 중인 버전(예 "v0.2.0"). 제어창 제목·하단 상태바에 표시.</summary>
    public string VersionText { get => _versionText; private set => Set(ref _versionText, value); }

    private string _updateStatusText = "";
    /// <summary>내려받아 적용 대기 중인 업데이트 안내(없으면 빈 문자열).</summary>
    public string UpdateStatusText { get => _updateStatusText; private set => Set(ref _updateStatusText, value); }

    private bool _updateAvailable;
    /// <summary>적용 대기 중인 업데이트가 있는지 — 상태바의 업데이트 안내 표시 여부를 제어.</summary>
    public bool UpdateAvailable { get => _updateAvailable; private set => Set(ref _updateAvailable, value); }

    /// <summary>App가 시작 시 현재 실행 버전을 전달한다(제어창 제목/상태바 표시용).</summary>
    public void SetVersion(string version) => OnUi(() => VersionText = $"v{version}");

    /// <summary>업데이트가 내려받아져 다음 재시작 때 적용 예약되면 App(→AppUpdateManager)가 호출한다.</summary>
    public void SetStagedUpdate(string version) => OnUi(() =>
    {
        UpdateStatusText = $"⬆ v{version} 다운로드됨 — 재시작 시 적용";
        UpdateAvailable = true;
    });

    // ---- 로그 뷰어 ----
    public ObservableCollection<string> LogLines { get; } = [];

    // ---- 설정 ----
    private string _hotkeyText;
    public string HotkeyText { get => _hotkeyText; set => Set(ref _hotkeyText, value); }

    public string[] AutostartMethods { get; } = ["startup", "scheduler"];

    private string _autostartMethod;
    public string AutostartMethod { get => _autostartMethod; set => Set(ref _autostartMethod, value); }

    private bool _showStatusBox;
    /// <summary>
    /// Whether the 6px broadcast-status box is shown at the screen's top-left corner. Applies
    /// immediately (no [설정 저장] needed): persists to config and tells App to show/hide the window.
    /// </summary>
    public bool ShowStatusBox
    {
        get => _showStatusBox;
        set
        {
            if (Set(ref _showStatusBox, value))
            {
                _configStore.Update(config => config.ShowStatusBox = value);
                StatusBoxVisibilityChanged?.Invoke(value);
                _log.Info(value ? "방송 상태 박스를 표시합니다." : "방송 상태 박스를 숨깁니다.");
            }
        }
    }

    private string _recordingFolder;
    public string RecordingFolder { get => _recordingFolder; set => Set(ref _recordingFolder, value); }

    private string _roomName;
    /// <summary>
    /// Per-device room label (호실명). Saved to <see cref="AppConfig.DeviceName"/> by [설정 저장] and
    /// stamped onto live/VOD titles via the {호실} token from the next broadcast/period on.
    /// </summary>
    public string RoomName { get => _roomName; set => Set(ref _roomName, value); }

    private int _maxSizeGb;
    public int MaxSizeGb { get => _maxSizeGb; set => Set(ref _maxSizeGb, value); }

    private int _retentionDays;
    public int RetentionDays { get => _retentionDays; set => Set(ref _retentionDays, value); }

    // ---- 텔레그램 알림 (Phase 1, [설정 저장]으로 일괄 저장) ----
    private bool _telegramEnabled;
    public bool TelegramEnabled { get => _telegramEnabled; set => Set(ref _telegramEnabled, value); }

    private string _telegramBotToken;
    /// <summary>
    /// Bot-token entry. Shows <see cref="TokenPlaceholder"/> while a token is stored; typing a real
    /// token replaces it (encrypted on save), clearing the box deletes the stored token on save.
    /// </summary>
    public string TelegramBotToken { get => _telegramBotToken; set => Set(ref _telegramBotToken, value); }

    private string _telegramChatId;
    public string TelegramChatId { get => _telegramChatId; set => Set(ref _telegramChatId, value); }

    public string[] TelegramNotifyLevels { get; } = ["info", "warn", "critical"];

    private string _telegramNotifyLevel;
    public string TelegramNotifyLevel { get => _telegramNotifyLevel; set => Set(ref _telegramNotifyLevel, value); }

    private string _telegramStatusText = "";
    public string TelegramStatusText { get => _telegramStatusText; private set => Set(ref _telegramStatusText, value); }

    public RelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand SaveSettingsCommand { get; }
    public RelayCommand RefreshDevicesCommand { get; }
    public RelayCommand AddMicCommand { get; }
    public RelayCommand SendTestAlertCommand { get; }

    /// <summary>Raised when the saved hotkey changes so App can re-register it.</summary>
    public event Action<string>? HotkeyChanged;

    /// <summary>Raised when the status-box toggle changes so App can show/hide the box window.</summary>
    public event Action<bool>? StatusBoxVisibilityChanged;

    private void Start()
    {
        _startCts = new CancellationTokenSource();
        _ = _orchestrator.StartAsync(_startCts.Token);
    }

    private void Stop()
    {
        _startCts?.Cancel();
        _ = _orchestrator.StopAsync();
    }

    // ---- audio mixer wiring ----

    private void LoadAudioSources(AppConfig config)
    {
        _loadingAudio = true;
        AudioSources.Clear();
        foreach (var settings in AudioConfigMapper.ToSourceSettings(config.Audio))
        {
            AudioSources.Add(CreateSourceVm(settings));
        }
        _loadingAudio = false;
    }

    private AudioSourceViewModel CreateSourceVm(AudioSourceSettings settings) =>
        new(settings, MicDevices, OnSourceChanged, RemoveSource);

    private void OnSourceChanged(AudioSourceViewModel vm, AudioChangeKind kind)
    {
        if (_loadingAudio)
        {
            return;
        }
        switch (kind)
        {
            case AudioChangeKind.Gain:
                _audioMixer.SetGain(vm.Id, vm.Gain);
                SchedulePersist(); // debounce: a slider drag fires many ticks
                break;
            case AudioChangeKind.Mute:
                _audioMixer.SetMuted(vm.Id, vm.Muted);
                PersistAudioSources();
                break;
            case AudioChangeKind.Structural:
                _audioMixer.ConfigureSources(BuildSourceSettings());
                ReconcileSilentMics();
                PersistAudioSources();
                break;
        }
    }

    private void SchedulePersist()
    {
        _persistDebounce.Stop();
        _persistDebounce.Start();
    }

    private void AddMic()
    {
        var used = AudioSources.Where(s => s.IsMic).Select(s => s.DeviceId).ToHashSet();
        var device = MicDevices.FirstOrDefault(d => !used.Contains(d.Id));
        var settings = new AudioSourceSettings(
            AudioConfigMapper.SourceId("mic", device?.Id),
            AudioSourceKind.Microphone, device?.Id, device?.Name ?? "마이크");
        AudioSources.Add(CreateSourceVm(settings));
        _audioMixer.ConfigureSources(BuildSourceSettings());
        PersistAudioSources();
        _log.Info($"오디오 소스 추가: {settings.Name}");
    }

    private void RemoveSource(AudioSourceViewModel vm)
    {
        if (vm.IsSystem || !AudioSources.Remove(vm))
        {
            return;
        }
        _audioMixer.ConfigureSources(BuildSourceSettings());
        ReconcileSilentMics();
        PersistAudioSources();
        _log.Info($"오디오 소스 제거: {vm.Name}");
    }

    private IReadOnlyList<AudioSourceSettings> BuildSourceSettings() =>
        AudioSources.Select(s => s.ToSettings()).ToList();

    private void PersistAudioSources()
    {
        _persistDebounce.Stop();
        var snapshot = AudioSources.Select(s => AudioConfigMapper.ToConfig(s.ToSettings())).ToList();
        _configStore.Update(config => config.Audio.Sources = snapshot);
    }

    private void PersistFilters()
    {
        if (_loadingAudio)
        {
            return;
        }
        _configStore.Update(config =>
        {
            config.Audio.Filters.NoiseSuppressionEnabled = _noiseSuppressionEnabled;
            config.Audio.Filters.NoiseSuppressionDb = _noiseSuppressionDb;
            config.Audio.Filters.CompressorEnabled = _compressorEnabled;
            config.Audio.Filters.LimiterEnabled = _limiterEnabled;
            config.Audio.Filters.MasterGainDb = _masterGainDb;
        });
        _log.Info("오디오 마스터 필터 변경 — 다음 세션부터 적용됩니다.");
    }

    private void ApplyLevels(AudioLevels levels)
    {
        foreach (var level in levels.Sources)
        {
            // small list (system + a few mics): linear scan is fine.
            foreach (var vm in AudioSources)
            {
                if (vm.Id == level.Id)
                {
                    vm.UpdateLevel(level.PeakDb, level.RmsDb);
                    break;
                }
            }
        }
        MasterMeterRms = ToFraction(levels.MasterRmsDb);
        MasterMeterPeak = ToFraction(levels.MasterPeakDb);
        MasterMeterBrush = levels.MasterPeakDb >= -3 ? "#E74C3C"
            : levels.MasterPeakDb >= -12 ? "#F1C40F" : "#2ECC40";
    }

    private void UpdatePreview()
    {
        var jpeg = _preview.GetLatestJpegFrame();
        if (jpeg is null || jpeg.Length == 0)
        {
            return;
        }
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(jpeg);
        image.EndInit();
        image.Freeze();
        PreviewImage = image;
    }

    private void ApplyMicSignal(MicSignalStatus status)
    {
        if (status.SignalPresent)
        {
            _silentMics.Remove(status.SourceId);
        }
        else
        {
            _silentMics.Add(status.SourceId);
        }
        UpdateMicWarning();
    }

    /// <summary>Drops warnings for sources that no longer exist (device changed/removed).</summary>
    private void ReconcileSilentMics()
    {
        _silentMics.RemoveWhere(id => AudioSources.All(s => s.Id != id));
        UpdateMicWarning();
    }

    private void UpdateMicWarning()
    {
        if (_silentMics.Count == 0)
        {
            MicWarningVisible = false;
            MicWarningText = "";
            return;
        }

        var names = _silentMics
            .Select(id => AudioSources.FirstOrDefault(s => s.Id == id)?.Name ?? "마이크")
            .ToList();
        MicWarningText = $"⚠ 마이크 신호 없음: {string.Join(", ", names)} — 연결/음소거를 확인하세요.";
        MicWarningVisible = true;
    }

    private void RefreshDevices()
    {
        MicDevices.Clear();
        foreach (var device in _audioMixer.GetMicrophoneDevices())
        {
            MicDevices.Add(device);
        }
        foreach (var vm in AudioSources)
        {
            vm.RefreshDeviceSelection();
        }
    }

    private void LoadMonitors(AppConfig config)
    {
        _loadingAudio = true;
        Monitors.Clear();
        foreach (var monitor in _capture.GetMonitors())
        {
            Monitors.Add(monitor);
        }
        _selectedMonitor = Monitors.FirstOrDefault(m => m.Index == config.Capture.MonitorIndex)
                           ?? Monitors.FirstOrDefault();
        Raise(nameof(SelectedMonitor));
        _loadingAudio = false;
    }

    public void RefreshRecordingStatus()
    {
        var status = _recordingManager.GetStatus();
        RecordingFileText = status.CurrentFilePath is null
            ? "녹화 대기 중"
            : Path.GetFileName(status.CurrentFilePath);
        RecordingUsageText = FormatBytes(status.TotalUsedBytes);
        FreeDiskText = FormatBytes(status.FreeDiskBytes);
    }

    private void ApplyState(StreamState state)
    {
        (StateBadge, StateColor) = state switch
        {
            StreamState.Live => ("LIVE", "#2ECC40"),
            StreamState.Warmup => ("준비 중", "#F1C40F"),
            StreamState.ConnectingYouTube => ("연결 중", "#F1C40F"),
            StreamState.Retrying => ("재시도 중", "#E74C3C"),
            StreamState.Stopping => ("중지 중", "#E74C3C"),
            _ => ("대기", "#7F8C8D")
        };
        StartCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        RefreshRecordingStatus();
    }

    private void ApplyMetrics(MetricsSnapshot metrics)
    {
        BitrateText = $"{metrics.UploadBitrateKbps:F0} kbps";
        FpsText = $"{metrics.Fps:F0} fps";
        CpuText = metrics.CpuPercent > 0 ? $"{metrics.CpuPercent:F0}%" : "-";
        GpuText = metrics.GpuPercent >= 0 ? $"{metrics.GpuPercent:F0}%" : "-";
    }

    private void SaveSettings()
    {
        // 단축키가 잘못돼도 나머지 설정(특히 텔레그램 토큰)은 저장한다 — 저장 전체를 막으면
        // 토큰을 입력한 사용자가 실패를 모른 채 알림이 영영 설정되지 않는다. 단축키만 건너뛴다.
        var hotkeyValid = HotkeyGesture.TryParse(HotkeyText, out var gesture);
        if (!hotkeyValid)
        {
            _log.Warn($"단축키 형식이 올바르지 않아 단축키만 저장하지 않았습니다: \"{HotkeyText}\"");
        }

        var protectFailed = false;
        _configStore.Update(config =>
        {
            if (hotkeyValid)
            {
                config.Hotkey = gesture!.Display;
            }
            config.Autostart = AutostartMethod;
            config.DeviceName = (RoomName ?? string.Empty).Trim();
            config.Recording.Folder = RecordingFolder;
            config.Recording.MaxSizeGb = MaxSizeGb;
            config.Recording.RetentionDays = RetentionDays;
            config.Capture.MonitorIndex = SelectedMonitor?.Index ?? config.Capture.MonitorIndex;
            config.Capture.RegionX = RegionX;
            config.Capture.RegionY = RegionY;
            config.Capture.RegionWidth = RegionWidth;
            config.Capture.RegionHeight = RegionHeight;
            config.Encoding.Resolution = Resolution;
            config.Encoding.Fps = Fps;
            config.Encoding.VideoBitrateKbps = VideoBitrateKbps;
            config.Encoding.AudioBitrateKbps = AudioBitrateKbps;

            // 텔레그램 알림: 토큰은 즉시 DPAPI 암호화해 저장하고 평문은 config에 남기지 않는다.
            // 플레이스홀더(********)가 그대로면 기존 토큰 유지, 비우고 저장하면 삭제.
            config.Notifications.Enabled = TelegramEnabled;
            config.Notifications.TelegramChatId = (TelegramChatId ?? string.Empty).Trim();
            config.Notifications.NotifyLevel = TelegramNotifyLevel;
            var typedToken = (TelegramBotToken ?? string.Empty).Trim();
            if (typedToken.Length == 0)
            {
                config.Notifications.TelegramBotToken = string.Empty;
                config.Notifications.TelegramBotTokenEnc = string.Empty;
            }
            else if (typedToken != TokenPlaceholder)
            {
                try
                {
                    config.Notifications.TelegramBotTokenEnc = _tokenProtector.Protect(typedToken);
                    config.Notifications.TelegramBotToken = string.Empty;
                }
                catch (Exception ex)
                {
                    protectFailed = true;
                    _log.Error("텔레그램 봇 토큰 암호화 실패 — 토큰을 저장하지 못했습니다.", ex);
                }
            }
            else if (!string.IsNullOrWhiteSpace(config.Notifications.TelegramBotToken))
            {
                // 플레이스홀더 유지 = 기존 토큰 유지. 그 토큰이 아직 평문(수동 편집 직후)이라면
                // 이 기회에 암호화해 평문을 디스크에서 제거한다.
                try
                {
                    config.Notifications.TelegramBotTokenEnc =
                        _tokenProtector.Protect(config.Notifications.TelegramBotToken.Trim());
                    config.Notifications.TelegramBotToken = string.Empty;
                }
                catch (Exception ex)
                {
                    _log.Error("텔레그램 봇 토큰 암호화 실패 — 평문 토큰을 유지합니다.", ex);
                }
            }
        });

        if (protectFailed)
        {
            // 암호화 실패를 성공처럼 위장하지 않는다: 입력값을 남겨 재시도할 수 있게 하고 알린다.
            TelegramStatusText = "토큰 암호화에 실패해 저장하지 못했습니다 — 로그를 확인하세요.";
        }
        else
        {
            // 저장 결과 기준으로 토큰 입력칸을 플레이스홀더/빈칸으로 되돌린다(평문 잔류 방지).
            // 평문 필드도 함께 검사해야 수동 편집(아직 미암호화) 토큰이 빈칸→삭제로 오인되지 않는다.
            var savedNotifications = _configStore.Load().Notifications;
            TelegramBotToken = string.IsNullOrEmpty(savedNotifications.TelegramBotTokenEnc)
                               && string.IsNullOrEmpty(savedNotifications.TelegramBotToken)
                ? string.Empty
                : TokenPlaceholder;
        }

        if (hotkeyValid)
        {
            HotkeyText = gesture!.Display;
            HotkeyChanged?.Invoke(gesture.Display);
        }
        _log.Info(hotkeyValid
            ? "설정이 저장되었습니다. (캡처 모니터/영역은 다음 세션부터 적용)"
            : "설정이 저장되었습니다(단축키 제외 — 형식 오류). (캡처 모니터/영역은 다음 세션부터 적용)");
        RefreshRecordingStatus();
    }

    /// <summary>
    /// Sends a test message through the configured notifier so the operator can verify the phone
    /// receives alerts. Runs off the UI thread (RelayCommand is synchronous); the result is
    /// marshalled back into the bound status text — the app's toast-equivalent.
    /// </summary>
    private void SendTestAlert()
    {
        if (!TelegramEnabled)
        {
            TelegramStatusText = "알림 사용이 꺼져 있습니다 — 체크하고 [설정 저장] 후 테스트하세요.";
            return;
        }
        TelegramStatusText = "테스트 알림 전송 중...";
        _ = Task.Run(async () =>
        {
            try
            {
                var ok = await _notifier
                    .SendAsync("✅ [테스트] Media Capture Helper 알림이 정상 동작합니다.", CancellationToken.None)
                    .ConfigureAwait(false);
                OnUi(() => TelegramStatusText = ok
                    ? "테스트 알림을 보냈습니다. 폰에서 확인하세요."
                    : "전송 실패 — 봇 토큰/채팅 ID를 확인하고 [설정 저장] 후 다시 시도하세요.");
            }
            catch (Exception ex)
            {
                OnUi(() => TelegramStatusText = $"전송 오류: {ex.Message}");
                _log.Warn($"텔레그램 테스트 알림 실패: {ex.Message}");
            }
        });
    }

    private static double ToFraction(double db)
    {
        const double floor = -60;
        return db <= floor ? 0 : Math.Clamp((db - floor) / -floor, 0, 1);
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F1} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F1} MB",
        _ => $"{bytes / 1024.0:F0} KB"
    };

    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }
        field = value;
        Raise(name);
        return true;
    }

    private void Raise(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
