using Microsoft.Maui.Controls;
using SkiaSharp;
using System;

namespace lasthomework;

public class NativeCameraView : View
{
    public event EventHandler<SKBitmap>? FrameReady;

    public static readonly BindableProperty IntensityProperty =
        BindableProperty.Create(nameof(Intensity), typeof(float), typeof(NativeCameraView), 1.0f);

    public float Intensity
    {
        get => (float)GetValue(IntensityProperty);
        set => SetValue(IntensityProperty, value);
    }

    public void RaiseFrameReady(SKBitmap bitmap)
    {
        FrameReady?.Invoke(this, bitmap);
    }
}