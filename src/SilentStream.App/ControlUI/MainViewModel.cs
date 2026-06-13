using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using SilentStream.Core.Contracts;
using SilentStream.Core.Hotkeys;
using SilentStream.Core.Logging;
using SilentStream.Core.Models;

namespace SilentStream.App.ControlUI;

/// <summary>
/// ViewModel behind the control UI (plan §3.8): state badge, start/stop, performance,
/// audio volumes + mic picker, resource limit, recording panel, log viewer, settings.
/// Couples to other modules through Core/Contracts interfaces only.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly IStreamOrchestrator _orchestrator;
    private readonly IAudioMixer _audioMixer;
    private readonly IRecordingManager _recordingManager;
    private readonly IConfigStore _configStore;
    private readonly ILogService _log;

    private CancellationTokenSource? _startCts;

    public MainViewModel(
        IStreamOrchestrator orchestrator,
        IAudioMixer audioMixer,
        IRecordingManager recordingManager,
        IConfigStore configStore,
        ILogService log)
    {
        _orchestrator = orchestrator;
        _audioMixer = audioMixer;
        _recordingManager = recordingManager;
        _configStore = configStore;
        _log = log;

        var config = _configStore.Load();
        _systemVolume = config.Audio.SystemVolume;
        _micVolume = config.Audio.MicVolume;
        _selectedResourceLimit = config.Encoding.ResourceLimit;
        _hotkeyText = config.Hotkey;
        _autostartMethod = config.Autostart;
        _recordingFolder = config.Recording.Folder;
        _maxSizeGb = config.Recording.MaxSizeGb;
        _retentionDays = config.Recording.RetentionDays;

        StartCommand = new RelayCommand(Start, () => _orchestrator.State == StreamState.Idle);
        StopCommand = new RelayCommand(Stop, () => _orchestrator.State is not (StreamState.Idle or StreamState.Stopping));
        SaveSettingsCommand = new RelayCommand(SaveSettings);
        RefreshDevicesCommand = new RelayCommand(RefreshDevices);

        _orchestrator.StateChanged += (_, state) => OnUi(() => ApplyState(state));
        _orchestrator.MetricsUpdated += (_, metrics) => OnUi(() => ApplyMetrics(metrics));

        foreach (var line in InMemoryLogSink.Snapshot())
        {
            LogLines.Add(line);
        }
        InMemoryLogSink.LineAdded += line => OnUi(() =>
        {
            LogLines.Add(line);
            while (LogLines.Count > InMemoryLogSink.Capacity)
            {
                LogLines.RemoveAt(0);
            }
        });

        RefreshDevices();
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

    // ---- 오디오 ----
    private double _systemVolume;
    public double SystemVolume
    {
        get => _systemVolume;
        set
        {
            if (Set(ref _systemVolume, value))
            {
                _audioMixer.SystemVolume = value;
                PersistAudio();
            }
        }
    }

    private double _micVolume;
    public double MicVolume
    {
        get => _micVolume;
        set
        {
            if (Set(ref _micVolume, value))
            {
                _audioMixer.MicVolume = value;
                PersistAudio();
            }
        }
    }

    public ObservableCollection<AudioDeviceInfo> MicDevices { get; } = [];

    private AudioDeviceInfo? _selectedMicDevice;
    public AudioDeviceInfo? SelectedMicDevice
    {
        get => _selectedMicDevice;
        set
        {
            if (Set(ref _selectedMicDevice, value) && value is not null)
            {
                _audioMixer.MicDeviceId = value.Id;
                PersistAudio();
            }
        }
    }

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
                var config = _configStore.Load();
                config.Encoding.ResourceLimit = value;
                _configStore.Save(config);
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

    // ---- 로그 뷰어 ----
    public ObservableCollection<string> LogLines { get; } = [];

    // ---- 설정 ----
    private string _hotkeyText;
    public string HotkeyText { get => _hotkeyText; set => Set(ref _hotkeyText, value); }

    public string[] AutostartMethods { get; } = ["startup", "scheduler"];

    private string _autostartMethod;
    public string AutostartMethod { get => _autostartMethod; set => Set(ref _autostartMethod, value); }

    private string _recordingFolder;
    public string RecordingFolder { get => _recordingFolder; set => Set(ref _recordingFolder, value); }

    private int _maxSizeGb;
    public int MaxSizeGb { get => _maxSizeGb; set => Set(ref _maxSizeGb, value); }

    private int _retentionDays;
    public int RetentionDays { get => _retentionDays; set => Set(ref _retentionDays, value); }

    public RelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand SaveSettingsCommand { get; }
    public RelayCommand RefreshDevicesCommand { get; }

    /// <summary>Raised when the saved hotkey changes so App can re-register it.</summary>
    public event Action<string>? HotkeyChanged;

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

    private void SaveSettings()
    {
        if (!HotkeyGesture.TryParse(HotkeyText, out var gesture))
        {
            _log.Warn($"단축키 형식이 올바르지 않아 저장하지 않았습니다: \"{HotkeyText}\"");
            return;
        }

        var config = _configStore.Load();
        config.Hotkey = gesture!.Display;
        config.Autostart = AutostartMethod;
        config.Recording.Folder = RecordingFolder;
        config.Recording.MaxSizeGb = MaxSizeGb;
        config.Recording.RetentionDays = RetentionDays;
        _configStore.Save(config);

        HotkeyText = gesture.Display;
        HotkeyChanged?.Invoke(gesture.Display);
        _log.Info("설정이 저장되었습니다.");
        RefreshRecordingStatus();
    }

    private void RefreshDevices()
    {
        MicDevices.Clear();
        var configuredId = _configStore.Load().Audio.MicDeviceId;
        foreach (var device in _audioMixer.GetMicrophoneDevices())
        {
            MicDevices.Add(device);
            if (device.Id == configuredId)
            {
                _selectedMicDevice = device;
                Raise(nameof(SelectedMicDevice));
            }
        }
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

    private void PersistAudio()
    {
        var config = _configStore.Load();
        config.Audio.SystemVolume = _systemVolume;
        config.Audio.MicVolume = _micVolume;
        config.Audio.MicDeviceId = _selectedMicDevice?.Id ?? config.Audio.MicDeviceId;
        _configStore.Save(config);
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
