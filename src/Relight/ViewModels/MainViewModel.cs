using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Relight.Models;
using Relight.Services;

namespace Relight.ViewModels;

/// <summary>Drives the main window: lighting presets, view mode and status.</summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly RelightSettings _settings = new();
    private readonly CustomRigStore _rigStore = new();
    private string _status = string.Empty;
    private bool _isBusy = true;
    private bool _syncFrames = true;
    private bool _showOverlay = true;
    private bool _isCustomMode;
    private int _presetIndex;
    private int _selectedLightIndex;
    private int _depthResolutionIndex = 1;

    public MainViewModel()
    {
        ApplyPreset(0);
        RebuildLights();
    }

    /// <summary>Raised when the depth model resolution changes.</summary>
    public event Action<int>? DepthResolutionChanged;

    public RelightSettings Settings => _settings;

    public IReadOnlyList<LightingPreset> Presets => LightingPreset.All;

    /// <summary>The active lights, as a bindable view over the fixed-size pool.</summary>
    public ObservableCollection<LightSlot> Lights { get; } = [];

    /// <summary>Slider bounds for a light's depth, mirrored from the settings limits.</summary>
    public double LightZMin => RelightSettings.LightZMin;

    public double LightZMax => RelightSettings.LightZMax;

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
            RebuildLights();
            RaiseGlobalsChanged();
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

    /// <summary>How much of the light source itself is drawn; see <see cref="BulbVisibility"/>.</summary>
    public int BulbVisibilityIndex
    {
        get => (int)_settings.Bulb;
        set
        {
            if (value < 0 || value > (int)BulbVisibility.Hidden || (int)_settings.Bulb == value)
            {
                return;
            }

            _settings.Bulb = (BulbVisibility)value;
            OnPropertyChanged();
        }
    }

    /// <summary>Look strength as a percentage, for the overlay slider.</summary>
    public double StrengthPercent
    {
        get => Math.Round(_settings.Strength * 100.0);
        set
        {
            float strength = (float)Math.Clamp(value / 100.0, 0.0, 1.0);
            if (Math.Abs(_settings.Strength - strength) < 0.0005f)
            {
                return;
            }

            _settings.Strength = strength;
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

    public void NextBulbVisibility() =>
        BulbVisibilityIndex = (BulbVisibilityIndex + 1) % ((int)BulbVisibility.Hidden + 1);

    /// <summary>
    /// When set, the pane is shown, presets stop being applied and the pointer stops steering the
    /// key light, so the hand-built rig stays exactly where the user put it.
    /// </summary>
    public bool IsCustomMode
    {
        get => _isCustomMode;
        set
        {
            if (_isCustomMode == value)
            {
                return;
            }

            _isCustomMode = value;

            // Entering custom seeds from whatever is on screen, unless a saved rig exists.
            if (value && _rigStore.TryLoad(_settings))
            {
                RebuildLights();
                RaiseGlobalsChanged();
            }
            else if (!value)
            {
                _rigStore.Save(_settings);
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(CustomPaneVisibility));
            OnPropertyChanged(nameof(SecondaryControlsVisibility));
        }
    }

    /// <summary>Drives the pane's visibility without needing a converter.</summary>
    public Microsoft.UI.Xaml.Visibility CustomPaneVisibility =>
        _isCustomMode ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    /// <summary>
    /// The pane takes real width off the viewport, so the overlay sheds its least relevant groups
    /// while arranging lights rather than being clipped.
    /// </summary>
    public Microsoft.UI.Xaml.Visibility SecondaryControlsVisibility =>
        _isCustomMode ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

    public int SelectedLightIndex
    {
        get => _selectedLightIndex;
        set
        {
            int clamped = Lights.Count == 0 ? 0 : Math.Clamp(value, 0, Lights.Count - 1);
            if (_selectedLightIndex == clamped)
            {
                return;
            }

            _selectedLightIndex = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedLight));
        }
    }

    public LightSource SelectedLight =>
        Lights.Count == 0 ? _settings.Lights[0] : Lights[Math.Clamp(_selectedLightIndex, 0, Lights.Count - 1)].Light;

    public bool CanAddLight => _settings.LightCount < RelightSettings.MaxLights;

    public bool CanRemoveLight => _settings.LightCount > 1;

    /// <summary>Adds a soft, unshadowed fill offset from the selected light and selects it.</summary>
    public void AddLight()
    {
        if (!CanAddLight)
        {
            return;
        }

        var source = SelectedLight;
        var added = _settings.Lights[_settings.LightCount];
        added.Set(
            Math.Clamp(source.X + 0.18f, 0f, 1f),
            Math.Clamp(source.Y + 0.12f, 0f, 1f),
            source.Z,
            source.ColorR,
            source.ColorG,
            source.ColorB,
            0.8f,
            false);

        _settings.LightCount++;
        RebuildLights();
        SelectedLightIndex = _settings.LightCount - 1;
        _rigStore.Save(_settings);
    }

    public void RemoveSelectedLight()
    {
        if (!CanRemoveLight)
        {
            return;
        }

        // Close the gap so the active lights stay at the front of the pool.
        for (int index = _selectedLightIndex; index < _settings.LightCount - 1; index++)
        {
            var next = _settings.Lights[index + 1];
            _settings.Lights[index].Set(
                next.X, next.Y, next.Z, next.ColorR, next.ColorG, next.ColorB, next.Intensity, next.CastsShadow);
        }

        _settings.LightCount--;
        RebuildLights();
        SelectedLightIndex = Math.Min(_selectedLightIndex, _settings.LightCount - 1);
        _rigStore.Save(_settings);
    }

    /// <summary>Writes the current rig to disk; called after a drag or a slider change settles.</summary>
    public void SaveCustomRig()
    {
        if (_isCustomMode)
        {
            _rigStore.Save(_settings);
        }
    }

    private void RebuildLights()
    {
        Lights.Clear();
        for (int index = 0; index < _settings.LightCount; index++)
        {
            Lights.Add(new LightSlot(index + 1, _settings.Lights[index]));
        }

        if (_selectedLightIndex >= Lights.Count)
        {
            _selectedLightIndex = Math.Max(0, Lights.Count - 1);
        }

        OnPropertyChanged(nameof(SelectedLight));
        OnPropertyChanged(nameof(SelectedLightIndex));
        OnPropertyChanged(nameof(CanAddLight));
        OnPropertyChanged(nameof(CanRemoveLight));
    }

    private void RaiseGlobalsChanged()
    {
        OnPropertyChanged(nameof(Exposure));
        OnPropertyChanged(nameof(Relief));
        OnPropertyChanged(nameof(Specular));
        OnPropertyChanged(nameof(Shadow));
        OnPropertyChanged(nameof(Occlusion));
        OnPropertyChanged(nameof(StrengthPercent));
    }

    public double Exposure
    {
        get => _settings.Exposure;
        set
        {
            float next = (float)value;
            if (Math.Abs(_settings.Exposure - next) < 0.0005f)
            {
                return;
            }

            _settings.Exposure = next;
            OnPropertyChanged();
            SaveCustomRig();
        }
    }

    public double Relief
    {
        get => _settings.Relief;
        set
        {
            float next = (float)value;
            if (Math.Abs(_settings.Relief - next) < 0.0005f)
            {
                return;
            }

            _settings.Relief = next;
            OnPropertyChanged();
            SaveCustomRig();
        }
    }

    public double Specular
    {
        get => _settings.Specular;
        set
        {
            float next = (float)value;
            if (Math.Abs(_settings.Specular - next) < 0.0005f)
            {
                return;
            }

            _settings.Specular = next;
            OnPropertyChanged();
            SaveCustomRig();
        }
    }

    public double Shadow
    {
        get => _settings.Shadow;
        set
        {
            float next = (float)value;
            if (Math.Abs(_settings.Shadow - next) < 0.0005f)
            {
                return;
            }

            _settings.Shadow = next;
            OnPropertyChanged();
            SaveCustomRig();
        }
    }

    public double Occlusion
    {
        get => _settings.Occlusion;
        set
        {
            float next = (float)value;
            if (Math.Abs(_settings.Occlusion - next) < 0.0005f)
            {
                return;
            }

            _settings.Occlusion = next;
            OnPropertyChanged();
            SaveCustomRig();
        }
    }

    private void ApplyPreset(int index)
    {
        _presetIndex = index;
        Presets[index].Apply(_settings);
    }
}
