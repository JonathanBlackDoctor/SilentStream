using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using SilentStream.Core.Implementations;
using SilentStream.Core.Models;

namespace SilentStream.App.ControlUI;

/// <summary>What kind of change a source row raised, so the parent applies it the cheapest way.</summary>
public enum AudioChangeKind
{
    /// <summary>Gain moved — apply live via the mixer's SetGain (no rebuild).</summary>
    Gain,

    /// <summary>Mute toggled — apply live via the mixer's SetMuted (no rebuild).</summary>
    Mute,

    /// <summary>Device/gate changed — reconfigure the mixer source graph.</summary>
    Structural
}

/// <summary>
/// One row in the control-window audio mixer: a system or microphone source with gain
/// (amplification &gt;1), mute, an optional noise gate, a per-source device picker (mics) and a
/// realtime level meter (OBS 대비 다중 오디오 채널 + 증폭 + 실시간 미터).
/// </summary>
public sealed class AudioSourceViewModel : INotifyPropertyChanged
{
    private readonly Action<AudioSourceViewModel, AudioChangeKind> _onChanged;
    private readonly Action<AudioSourceViewModel> _onRemove;
    private bool _suppress;

    public AudioSourceViewModel(
        AudioSourceSettings settings,
        ObservableCollection<AudioDeviceInfo> micDevices,
        Action<AudioSourceViewModel, AudioChangeKind> onChanged,
        Action<AudioSourceViewModel> onRemove)
    {
        _onChanged = onChanged;
        _onRemove = onRemove;
        AvailableDevices = micDevices;
        IsSystem = settings.Kind == AudioSourceKind.System;
        _deviceId = settings.DeviceId;
        _name = settings.Name;
        _gain = settings.Gain;
        _muted = settings.Muted;
        _gateEnabled = settings.GateEnabled;
        _gateThresholdDb = settings.GateThresholdDb;
        _selectedDevice = micDevices.FirstOrDefault(d => d.Id == settings.DeviceId);
        RemoveCommand = new RelayCommand(() => _onRemove(this), () => !IsSystem);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsSystem { get; }
    public bool IsMic => !IsSystem;
    public bool CanRemove => !IsSystem;

    /// <summary>Shared list of capture devices (mic rows bind a picker to it).</summary>
    public ObservableCollection<AudioDeviceInfo> AvailableDevices { get; }

    public RelayCommand RemoveCommand { get; }

    private string? _deviceId;
    public string? DeviceId => _deviceId;

    /// <summary>Deterministic mixer key — matches <see cref="IAudioMixer"/> source ids.</summary>
    public string Id => IsSystem ? AudioConfigMapper.SystemSourceId : AudioConfigMapper.SourceId("mic", _deviceId);

    private string _name;
    public string Name { get => _name; set => Set(ref _name, value); }

    private AudioDeviceInfo? _selectedDevice;
    public AudioDeviceInfo? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (Set(ref _selectedDevice, value) && value is not null && value.Id != _deviceId)
            {
                _deviceId = value.Id;
                _name = value.Name;
                Raise(nameof(Name));
                Raise(nameof(Id));
                _onChanged(this, AudioChangeKind.Structural);
            }
        }
    }

    private double _gain;
    /// <summary>Linear gain 0..4 (1 = unity, &gt;1 amplifies).</summary>
    public double Gain
    {
        get => _gain;
        set
        {
            if (Set(ref _gain, value))
            {
                Raise(nameof(GainDbText));
                if (!_suppress)
                {
                    _onChanged(this, AudioChangeKind.Gain);
                }
            }
        }
    }

    public string GainDbText => _gain <= 0.0001
        ? "-∞ dB"
        : $"{20 * Math.Log10(_gain):+0.0;-0.0;0.0} dB";

    private bool _muted;
    public bool Muted
    {
        get => _muted;
        set
        {
            if (Set(ref _muted, value) && !_suppress)
            {
                _onChanged(this, AudioChangeKind.Mute);
            }
        }
    }

    private bool _gateEnabled;
    public bool GateEnabled
    {
        get => _gateEnabled;
        set
        {
            if (Set(ref _gateEnabled, value) && !_suppress)
            {
                _onChanged(this, AudioChangeKind.Structural);
            }
        }
    }

    private double _gateThresholdDb;
    public double GateThresholdDb
    {
        get => _gateThresholdDb;
        set
        {
            if (Set(ref _gateThresholdDb, value) && !_suppress)
            {
                _onChanged(this, AudioChangeKind.Structural);
            }
        }
    }

    // ---- realtime meter (0..1), updated from the mixer's LevelsUpdated event ----

    private double _meterRms;
    public double MeterRms { get => _meterRms; private set => Set(ref _meterRms, value); }

    private double _meterPeak;
    public double MeterPeak { get => _meterPeak; private set => Set(ref _meterPeak, value); }

    private string _meterBrush = "#2ECC40";
    public string MeterBrush { get => _meterBrush; private set => Set(ref _meterBrush, value); }

    /// <summary>Maps dBFS levels onto the [0,1] meter and picks a green/yellow/red colour.</summary>
    public void UpdateLevel(double peakDb, double rmsDb)
    {
        MeterRms = ToFraction(rmsDb);
        MeterPeak = ToFraction(peakDb);
        MeterBrush = peakDb >= -3 ? "#E74C3C" : peakDb >= -12 ? "#F1C40F" : "#2ECC40";
    }

    public AudioSourceSettings ToSettings() => new(
        Id, IsSystem ? AudioSourceKind.System : AudioSourceKind.Microphone,
        IsSystem ? null : _deviceId, _name, _gain, _muted, _gateEnabled, _gateThresholdDb);

    /// <summary>Applies a settings snapshot back to the row without re-raising change callbacks.</summary>
    public void RefreshDeviceSelection()
    {
        _suppress = true;
        SelectedDevice = AvailableDevices.FirstOrDefault(d => d.Id == _deviceId) ?? _selectedDevice;
        _suppress = false;
    }

    private static double ToFraction(double db)
    {
        const double floor = -60;
        if (db <= floor)
        {
            return 0;
        }
        return Math.Clamp((db - floor) / -floor, 0, 1);
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
