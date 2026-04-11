using System.Runtime.InteropServices;
using Valve.VR;
using VRRealityWindow.Core;
using VRRealityWindow.Core.Utilities;
using VRRealityWindow.OpenVr;

namespace BatteryDotOverlay;

internal sealed class BatteryIndicatorOverlay : IDisposable
{
    private readonly OpenVrRuntime _runtime;
    private readonly BatteryDotSettings _settings;
    private readonly string _texturePath;
    private ulong _overlayHandle;
    private bool _visible;

    public BatteryIndicatorOverlay(OpenVrRuntime runtime, BatteryDotSettings settings)
    {
        _runtime = runtime;
        _settings = settings;
        _texturePath = Path.Combine(
            Path.GetTempPath(),
            "BatteryDotOverlay",
            $"battery-dot-{Environment.ProcessId}.png");
    }

    public bool IsVisible => _visible;

    public void Initialize()
    {
        _runtime.Initialize(EVRApplicationType.VRApplication_Overlay);

        var existing = 0UL;
        var findError = _runtime.Overlay.FindOverlay(_settings.Overlay.OverlayKey, ref existing);
        if (findError == EVROverlayError.None && existing != 0)
        {
            _runtime.Overlay.DestroyOverlay(existing);
        }

        var createError = _runtime.Overlay.CreateOverlay(
            _settings.Overlay.OverlayKey,
            _settings.Overlay.OverlayName,
            ref _overlayHandle);
        ThrowIfOverlayError(createError, "CreateOverlay");

        ThrowIfOverlayError(
            _runtime.Overlay.SetOverlayWidthInMeters(_overlayHandle, Math.Clamp(_settings.Overlay.WidthInMeters, 0.005f, 0.25f)),
            "SetOverlayWidthInMeters");
        ThrowIfOverlayError(_runtime.Overlay.SetOverlayAlpha(_overlayHandle, 1.0f), "SetOverlayAlpha");
        ThrowIfOverlayError(_runtime.Overlay.SetOverlayColor(_overlayHandle, 1f, 1f, 1f), "SetOverlayColor");
        ThrowIfOverlayError(_runtime.Overlay.SetOverlaySortOrder(_overlayHandle, _settings.Overlay.SortOrder), "SetOverlaySortOrder");

        var transform = CreateHeadLockedTransform(_settings.Overlay);
        ThrowIfOverlayError(
            _runtime.Overlay.SetOverlayTransformTrackedDeviceRelative(
                _overlayHandle,
                OpenVR.k_unTrackedDeviceIndex_Hmd,
                ref transform),
            "SetOverlayTransformTrackedDeviceRelative");

        WriteTexture();
        Hide();
    }

    public void Show()
    {
        EnsureOverlayCreated();
        if (_visible)
        {
            return;
        }

        ThrowIfOverlayError(_runtime.Overlay.ShowOverlay(_overlayHandle), "ShowOverlay");
        _visible = true;
    }

    public void Hide()
    {
        EnsureOverlayCreated();
        if (!_visible)
        {
            return;
        }

        ThrowIfOverlayError(_runtime.Overlay.HideOverlay(_overlayHandle), "HideOverlay");
        _visible = false;
    }

    public void SetVisible(bool visible)
    {
        if (visible)
        {
            Show();
            return;
        }

        Hide();
    }

    public void Dispose()
    {
        if (_overlayHandle != 0)
        {
            try
            {
                _runtime.Overlay.HideOverlay(_overlayHandle);
            }
            catch
            {
            }

            _runtime.Overlay.DestroyOverlay(_overlayHandle);
            _overlayHandle = 0;
            _visible = false;
        }

        try
        {
            if (File.Exists(_texturePath))
            {
                File.Delete(_texturePath);
            }
        }
        catch
        {
        }
    }

    private void WriteTexture()
    {
        var frame = DotTextureFactory.CreateFrame(_settings.Visual);
        PngWriter.WriteBgra32(_texturePath, frame);
        ThrowIfOverlayError(_runtime.Overlay.SetOverlayFromFile(_overlayHandle, _texturePath), "SetOverlayFromFile");
    }

    private void EnsureOverlayCreated()
    {
        if (_overlayHandle == 0)
        {
            throw new InvalidOperationException("Overlay has not been initialized.");
        }
    }

    private static HmdMatrix34_t CreateHeadLockedTransform(OverlayConfig overlay)
    {
        return new HmdMatrix34_t
        {
            m0 = 1f,
            m1 = 0f,
            m2 = 0f,
            m3 = overlay.HorizontalOffsetMeters,
            m4 = 0f,
            m5 = 1f,
            m6 = 0f,
            m7 = overlay.VerticalOffsetMeters,
            m8 = 0f,
            m9 = 0f,
            m10 = 1f,
            m11 = -MathF.Abs(overlay.DistanceMeters),
        };
    }

    private static void ThrowIfOverlayError(EVROverlayError error, string operation)
    {
        if (error != EVROverlayError.None)
        {
            throw new InvalidOperationException($"{operation} failed: {error}");
        }
    }
}
