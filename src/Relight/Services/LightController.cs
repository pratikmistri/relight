using System;
using Relight.Models;

namespace Relight.Services;

/// <summary>
/// Steers the light from pointer input, falling back to a slow idle orbit when the
/// pointer leaves. Mirrors the interaction model of the TypeGPU reference example.
/// </summary>
public sealed class LightController
{
    private const double OrbitSpeed = 0.00024;
    private const double OrbitRadius = 0.26;
    private const float WheelSensitivity = 0.0015f;
    private const float WheelStepLimit = 60f;
    private const float GrabRadius = 0.08f;

    /// <summary>
    /// How far past the frame edge a light may be pushed, in image widths. The pointer keeps
    /// steering the light in the letterboxed area, so the source can sit out of shot while
    /// still lighting the subject.
    /// </summary>
    private const float OffscreenMargin = 0.5f;

    private enum ControlMode
    {
        Orbit,
        Cursor,
        Pinned,
    }

    private readonly RelightSettings _settings;
    private ControlMode _mode = ControlMode.Orbit;

    public LightController(RelightSettings settings)
    {
        _settings = settings;
    }

    /// <summary>
    /// Set while the user is arranging lights by hand, so pointer steering and the idle orbit
    /// do not fight the custom rig.
    /// </summary>
    public bool Suspended { get; set; }

    /// <summary>
    /// Pointer steering and the idle orbit are opt-in. A preset is a designed rig, so having the
    /// key light chase the cursor, or drift on its own while idle, takes it apart.
    /// </summary>
    public bool FollowPointer { get; set; }

    private bool IsSteering => FollowPointer && !Suspended;

    /// <summary>Advances the idle orbit; call once per rendered frame.</summary>
    public void Tick(double elapsedMilliseconds)
    {
        if (!IsSteering || _mode != ControlMode.Orbit)
        {
            return;
        }

        double phase = elapsedMilliseconds * OrbitSpeed;
        Place(
            (float)(0.5 + (Math.Cos(phase) * OrbitRadius)),
            (float)(0.44 + (Math.Sin(phase * 1.37) * OrbitRadius * 0.8)));
    }

    public void PointerEntered()
    {
        if (IsSteering && _mode != ControlMode.Pinned)
        {
            _mode = ControlMode.Cursor;
        }
    }

    public void PointerExited()
    {
        if (IsSteering && _mode != ControlMode.Pinned)
        {
            _mode = ControlMode.Orbit;
        }
    }

    public void PointerMoved(float x, float y)
    {
        if (IsSteering && _mode == ControlMode.Cursor)
        {
            Place(x, y);
        }
    }

    /// <summary>Clicking the light toggles the pin; clicking elsewhere pins it there.</summary>
    public void PointerPressed(float x, float y)
    {
        if (!IsSteering)
        {
            return;
        }

        bool grabbed = MathF.Sqrt(
            ((x - _settings.KeyLight.X) * (x - _settings.KeyLight.X)) +
            ((y - _settings.KeyLight.Y) * (y - _settings.KeyLight.Y))) <= GrabRadius;

        if (grabbed)
        {
            _mode = _mode == ControlMode.Pinned ? ControlMode.Cursor : ControlMode.Pinned;
            return;
        }

        Place(x, y);
        _mode = ControlMode.Pinned;
    }

    public void WheelChanged(int delta)
    {
        if (!IsSteering)
        {
            return;
        }

        float clamped = Math.Sign(delta) * MathF.Min(MathF.Abs(delta), WheelStepLimit);
        _settings.KeyLight.Z = Math.Clamp(
            _settings.KeyLight.Z + (clamped * WheelSensitivity),
            RelightSettings.LightZMin,
            RelightSettings.LightZMax);
    }

    private void Place(float x, float y)
    {
        _settings.KeyLight.X = Math.Clamp(x, -OffscreenMargin, 1f + OffscreenMargin);
        _settings.KeyLight.Y = Math.Clamp(y, -OffscreenMargin, 1f + OffscreenMargin);
    }
}
