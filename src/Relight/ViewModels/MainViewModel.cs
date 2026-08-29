using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using Relight.Models;

namespace Relight.ViewModels;

/// <summary>Drives the main window: lighting presets, view mode and status.</summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly RelightSettings _settings = new();
    private string _status = string.Empty;
    private bool _isBusy = true;
    private bool _syncFrames = true;
    private bool _showOverlay = true;
    private int _presetIndex;
    private int _depthResolutionIndex = 1;

    public MainViewModel()
    {
        ApplyPreset(0);
    }

    /// <summary>Raised when the depth model resolution changes.</summary>
    public event Action<int>? DepthResolutionChanged;

    public RelightSettings Settings => _settings;

    public IReadOnlyList<LightingPreset> Presets => LightingPreset.All;

    /// <summary>Depth model pixel budgets; the actual input is sized to the camera aspect.</summary>
    public IReadOnlyList<int> DepthResolutions { get; } = [168, 224, 308, 448];

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set => SetProperty(ref _isBusy, value);
    }

    /// <summary>Controls whether the transport overlay is on screen.</summary>
    public bool ShowOverlay
    {
        get => _showOverlay;
        set => SetProperty(ref _showOverlay, value);
    }

    /// <summary>
    /// When set, the camera image is held back and presented together with the depth field
    /// inferred from it, so shading always matches the image.
    /// </summary>
    public bool SyncFrames
    {
        get => _syncFrames;
        set => SetProperty(ref _syncFrames, value);
    }

    public int PresetIndex
    {
        get => _presetIndex;
        set
        {
            if (value < 0 || value >= Presets.Count || _presetIndex == value)
            {
                return;
            }

            ApplyPreset(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(PresetName));
            OnPropertyChanged(nameof(PresetDescription));
        }
    }

    public string PresetName => Presets[_presetIndex].Name;

    public string PresetDescription => Presets[_presetIndex].Description;

    public int ViewModeIndex
    {
        get => (int)_settings.Mode;
        set
        {
            if (value < 0 || (int)_settings.Mode == value)
            {
                return;
            }

            _settings.Mode = (RelightMode)value;
            OnPropertyChanged();
        }
    }

    public int DepthResolutionIndex
    {
        get => _depthResolutionIndex;
        set
        {
            if (value < 0 || value >= DepthResolutions.Count || _depthResolutionIndex == value)
            {
                return;
            }

            _depthResolutionIndex = value;
            OnPropertyChanged();
            DepthResolutionChanged?.Invoke(DepthResolutions[value]);
        }
    }

    public void NextPreset() => PresetIndex = (_presetIndex + 1) % Presets.Count;

    public void PreviousPreset() => PresetIndex = (_presetIndex + Presets.Count - 1) % Presets.Count;

    public void NextViewMode() => ViewModeIndex = (ViewModeIndex + 1) % 4;

    private void ApplyPreset(int index)
    {
        _presetIndex = index;
        Presets[index].Apply(_settings);
    }
}
