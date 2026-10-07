using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Storage;
using SkiaSharp;
using SkiaSharp.Views.Maui;

namespace lasthomework;

public partial class MainPage : ContentPage
{
    private const float MAX_INTENSITY = 8.0f;
    private const float MIN_INTENSITY = 0.1f;  // 최대 감쇄치 (백화현상 억제용)

    private SKBitmap? _liveBitmap;
    private readonly object frameLock = new object();

    // ⚡ 자동 최적화 관련 변수들
    private bool isAutoOptimizationEnabled = true;
    private float currentScaleFactor = 1.0f;
    private float _currentAutoIntensity = 1.0f; // 화면이 스스로 판단한 게이지 목표값

    private SKBitmap? _galleryBitmap;

    private readonly Stopwatch frameStopwatch = new Stopwatch();
    private int frameCount = 0;
    private DateTime lastFpsUpdate = DateTime.Now;

    public MainPage()
    {
        InitializeComponent();
        frameStopwatch.Start();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        var status = await Permissions.CheckStatusAsync<Permissions.Camera>();
        if (status != PermissionStatus.Granted)
        {
            status = await Permissions.RequestAsync<Permissions.Camera>();
        }

        if (status != PermissionStatus.Granted)
        {
            await DisplayAlert("권한 필요", "카메라 권한이 없으면 실시간 스캔을 사용할 수 없습니다.", "확인");
        }

        if (CameraIntensitySlider != null)
        {
            CameraIntensitySlider.IsEnabled = !isAutoOptimizationEnabled;
        }
    }

    private void OnCameraFrameReady(object? sender, SKBitmap? e)
    {
        if (e != null)
        {
            lock (frameLock)
            {
                _liveBitmap?.Dispose();
                _liveBitmap = e;
            }

            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (CameraViewContainer != null && CameraViewContainer.IsVisible)
                    CameraCanvas?.InvalidateSurface();
            });
        }
    }

    private void OnTabCameraClicked(object? sender, EventArgs e)
    {
        if (CameraViewContainer == null || GalleryViewContainer == null) return;
        CameraViewContainer.IsVisible = true;
        GalleryViewContainer.IsVisible = false;

        if (TabCameraButton != null)
        {
            TabCameraButton.BackgroundColor = Color.FromArgb("#007AFF");
            TabCameraButton.TextColor = Colors.White;
        }
        if (TabGalleryButton != null)
        {
            TabGalleryButton.BackgroundColor = Colors.Transparent;
            TabGalleryButton.TextColor = Colors.Gray;
        }
    }

    private void OnTabGalleryClicked(object? sender, EventArgs e)
    {
        if (CameraViewContainer == null || GalleryViewContainer == null) return;
        CameraViewContainer.IsVisible = false;
        GalleryViewContainer.IsVisible = true;

        if (TabGalleryButton != null)
        {
            TabGalleryButton.BackgroundColor = Color.FromArgb("#007AFF");
            TabGalleryButton.TextColor = Colors.White;
        }
        if (TabCameraButton != null)
        {
            TabCameraButton.BackgroundColor = Colors.Transparent;
            TabCameraButton.TextColor = Colors.Gray;
        }
    }

    private void OnAutoOptButtonClicked(object? sender, EventArgs e)
    {
        if (AutoOptButton == null) return;

        isAutoOptimizationEnabled = !isAutoOptimizationEnabled;

        if (CameraIntensitySlider != null)
        {
            CameraIntensitySlider.IsEnabled = !isAutoOptimizationEnabled;
        }

        if (isAutoOptimizationEnabled)
        {
            AutoOptButton.Text = "⚡ 최적화: ON";
            AutoOptButton.BackgroundColor = Color.FromArgb("#28A745");
        }
        else
        {
            currentScaleFactor = 1.0f;
            AutoOptButton.Text = "⚡ 최적화: OFF";
            AutoOptButton.BackgroundColor = Color.FromArgb("#DC3545");
        }
    }

    private void OnCameraCanvasPaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        if (e.Surface == null || e.Surface.Canvas == null) return;

        SKImageInfo info = e.Info;
        SKCanvas canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.Black);

        SKBitmap? frameToRender = null;
        lock (frameLock)
        {
            if (_liveBitmap != null) frameToRender = _liveBitmap.Copy();
        }
        if (frameToRender == null) return;

        long startTime = frameStopwatch.ElapsedMilliseconds;
        SKBitmap targetBitmap = frameToRender;
        bool isScaled = false;

        if (isAutoOptimizationEnabled && currentScaleFactor < 1.0f)
        {
            int scaledWidth = (int)(frameToRender.Width * currentScaleFactor);
            int scaledHeight = (int)(frameToRender.Height * currentScaleFactor);
            var resized = frameToRender.Resize(new SKImageInfo(scaledWidth, scaledHeight), new SKSamplingOptions(SKFilterMode.Linear));
            if (resized != null)
            {
                targetBitmap = resized;
                isScaled = true;
            }
        }

        if (isAutoOptimizationEnabled && targetBitmap != null)
        {
            int stepX = Math.Max(1, targetBitmap.Width / 10);
            int stepY = Math.Max(1, targetBitmap.Height / 10);
            long totalLuminance = 0;
            int sampleCount = 0;

            for (int x = 0; x < targetBitmap.Width; x += stepX)
            {
                for (int y = 0; y < targetBitmap.Height; y += stepY)
                {
                    var color = targetBitmap.GetPixel(x, y);
                    totalLuminance += (long)(color.Red * 0.299 + color.Green * 0.587 + color.Blue * 0.114);
                    sampleCount++;
                }
            }

            float avgLuminance = sampleCount > 0 ? (float)totalLuminance / sampleCount : 255f;
            float calculatedIntensity = 1.0f;

            if (avgLuminance < 150f)
            {
                calculatedIntensity = 1.0f + ((150f - avgLuminance) / 150f) * (MAX_INTENSITY - 1.0f);
            }
            else if (avgLuminance > 200f)
            {
                calculatedIntensity = 1.0f - ((avgLuminance - 200f) / 55f) * (1.0f - MIN_INTENSITY);
            }
            else
            {
                calculatedIntensity = 1.0f;
            }

            _currentAutoIntensity = _currentAutoIntensity * 0.9f + calculatedIntensity * 0.1f;

            if (CameraIntensitySlider != null && Math.Abs(CameraIntensitySlider.Value - _currentAutoIntensity) > 0.05)
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    CameraIntensitySlider.Value = Math.Clamp(_currentAutoIntensity, MIN_INTENSITY, MAX_INTENSITY);
                });
            }
        }

        float intensity = CameraIntensitySlider != null ? (float)CameraIntensitySlider.Value : 1.0f;

        float scale = intensity;
        float offset = 0f;

        if (intensity < 1.0f)
        {
            // 💡 [핵심] 화이트 피벗(White Pivot) 명암비 알고리즘
            // 밝기를 줄일수록(0.1에 가까울수록) 대비를 최대 4배까지 증폭
            float contrast = 1.0f + (1.0f - intensity) * 3.0f;
            scale = contrast;

            // 1.0(흰색) 픽셀은 계산 후에도 무조건 1.0을 유지하도록 오프셋을 역산하여 깎아냄
            // 예) contrast가 3.0일 때, 흰색 픽셀(1.0) * 3.0 + (-2.0) = 1.0 (그대로 흰색)
            // 반면 회색 텍스트(0.6) * 3.0 + (-2.0) = -0.2 -> 0.0 (완벽한 검은색으로 복원!)
            offset = 1.0f - contrast;
        }

        using (SKPaint paint = new SKPaint { IsAntialias = true })
        {
            float[] colorMatrix = new float[]
            {
                scale, 0, 0, 0, offset,
                0, scale, 0, 0, offset,
                0, 0, scale, 0, offset,
                0, 0, 0, 1, 0
            };
            paint.ColorFilter = SKColorFilter.CreateColorMatrix(colorMatrix);

            canvas.Save();
            canvas.Translate(info.Width, 0);
            canvas.RotateDegrees(90);
            SKRect destRect = new SKRect(0, 0, info.Height, info.Width);
            canvas.DrawBitmap(targetBitmap, destRect, new SKSamplingOptions(SKFilterMode.Linear), paint);
            canvas.Restore();
        }

        if (isScaled) targetBitmap?.Dispose();
        frameToRender.Dispose();

        long elapsedTime = frameStopwatch.ElapsedMilliseconds - startTime;
        UpdatePerformanceMetrics(elapsedTime);
    }

    private void UpdatePerformanceMetrics(long processTimeMs)
    {
        frameCount++;
        TimeSpan elapsed = DateTime.Now - lastFpsUpdate;

        if (elapsed.TotalSeconds >= 1.0)
        {
            double currentFps = frameCount / elapsed.TotalSeconds;
            frameCount = 0;
            lastFpsUpdate = DateTime.Now;

            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (FpsLabel != null)
                    FpsLabel.Text = $"FPS: {currentFps:F1}";

                if (isAutoOptimizationEnabled)
                {
                    if (processTimeMs > 40 || currentFps < 20.0)
                        currentScaleFactor = 0.5f;
                    else if (processTimeMs < 15 && currentFps >= 45.0)
                        currentScaleFactor = 1.0f;
                }
            });
        }
    }

    private async void OnLoadGalleryClicked(object? sender, EventArgs e)
    {
        try
        {
            var result = await FilePicker.Default.PickAsync(new PickOptions
            {
                PickerTitle = "스캔할 사진을 선택하세요",
                FileTypes = FilePickerFileType.Images
            });

            if (result != null)
            {
                using var stream = await result.OpenReadAsync();
                _galleryBitmap?.Dispose();
                _galleryBitmap = SKBitmap.Decode(stream);
                if (GalleryCanvas != null)
                    GalleryCanvas.InvalidateSurface();
            }
        }
        catch (Exception ex)
        {
            await DisplayAlert("오류", $"사진을 불러오지 못했습니다: {ex.Message}", "확인");
        }
    }

    private void OnGallerySliderValueChanged(object? sender, ValueChangedEventArgs e)
    {
        if (GalleryViewContainer != null && GalleryViewContainer.IsVisible && GalleryCanvas != null)
            GalleryCanvas.InvalidateSurface();
    }

    private void OnGalleryCanvasPaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        if (_galleryBitmap == null || e.Surface == null || e.Surface.Canvas == null) return;

        SKImageInfo info = e.Info;
        SKCanvas canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.Black);

        float intensity = GalleryIntensitySlider != null ? (float)GalleryIntensitySlider.Value : 1.0f;

        float scale = intensity;
        float offset = 0f;

        if (intensity < 1.0f)
        {
            float contrast = 1.0f + (1.0f - intensity) * 3.0f;
            scale = contrast;
            offset = 1.0f - contrast;
        }

        using (SKPaint paint = new SKPaint { IsAntialias = true })
        {
            float[] colorMatrix = new float[]
            {
                scale, 0, 0, 0, offset,
                0, scale, 0, 0, offset,
                0, 0, scale, 0, offset,
                0, 0, 0, 1, 0
            };
            paint.ColorFilter = SKColorFilter.CreateColorMatrix(colorMatrix);

            float drawScale = Math.Min((float)info.Width / _galleryBitmap.Width, (float)info.Height / _galleryBitmap.Height);
            float x = (info.Width - drawScale * _galleryBitmap.Width) / 2;
            float y = (info.Height - drawScale * _galleryBitmap.Height) / 2;
            SKRect destRect = new SKRect(x, y, x + drawScale * _galleryBitmap.Width, y + drawScale * _galleryBitmap.Height);

            canvas.DrawBitmap(_galleryBitmap, destRect, new SKSamplingOptions(SKFilterMode.Linear), paint);
        }
    }

    private void OnCaptureLiveClicked(object? sender, EventArgs e)
    {
        if (_liveBitmap == null) return;

        SKBitmap? snapshot = null;
        lock (frameLock)
        {
            snapshot = _liveBitmap.Copy();
        }
        if (snapshot == null) return;

        SKBitmap rotatedBitmap = new SKBitmap(snapshot.Height, snapshot.Width);
        using (var canvas = new SKCanvas(rotatedBitmap))
        {
            canvas.Translate(rotatedBitmap.Width, 0);
            canvas.RotateDegrees(90);
            canvas.DrawBitmap(snapshot, 0, 0, new SKSamplingOptions());
        }

        _galleryBitmap?.Dispose();
        _galleryBitmap = rotatedBitmap;

        if (GalleryIntensitySlider != null && CameraIntensitySlider != null)
        {
            GalleryIntensitySlider.Value = CameraIntensitySlider.Value;
        }

        OnTabGalleryClicked(this, EventArgs.Empty);

        if (GalleryCanvas != null)
        {
            GalleryCanvas.InvalidateSurface();
        }

        snapshot.Dispose();
    }

    private async void OnSaveGalleryClicked(object? sender, EventArgs e)
    {
        if (_galleryBitmap == null)
        {
            await DisplayAlert("알림", "먼저 사진을 불러와주세요.", "확인");
            return;
        }

        float intensity = GalleryIntensitySlider != null ? (float)GalleryIntensitySlider.Value : 1.0f;
        await SaveFilteredImageAsync(_galleryBitmap, Math.Min(intensity, MAX_INTENSITY), rotate: false);
    }

    private async Task SaveFilteredImageAsync(SKBitmap? originalBitmap, float intensity, bool rotate)
    {
        if (originalBitmap == null) return;

        try
        {
            string filePath = await Task.Run(() =>
            {
                int width = rotate ? originalBitmap.Height : originalBitmap.Width;
                int height = rotate ? originalBitmap.Width : originalBitmap.Height;

                using var surface = SKSurface.Create(new SKImageInfo(width, height));
                if (surface == null || surface.Canvas == null) return string.Empty;

                var canvas = surface.Canvas;
                canvas.Clear(SKColors.Black);

                if (rotate)
                {
                    canvas.Translate(width, 0);
                    canvas.RotateDegrees(90);
                }

                float scale = intensity;
                float offset = 0f;

                if (intensity < 1.0f)
                {
                    float contrast = 1.0f + (1.0f - intensity) * 3.0f;
                    scale = contrast;
                    offset = 1.0f - contrast;
                }

                using var paint = new SKPaint { IsAntialias = true };
                var colorMatrix = new float[]
                {
                    scale, 0, 0, 0, offset,
                    0, scale, 0, 0, offset,
                    0, 0, scale, 0, offset,
                    0, 0, 0, 1, 0
                };
                paint.ColorFilter = SKColorFilter.CreateColorMatrix(colorMatrix);

                canvas.DrawBitmap(originalBitmap, 0, 0, new SKSamplingOptions(SKFilterMode.Linear), paint);

                using var image = surface.Snapshot();
                using var data = image?.Encode(SKEncodedImageFormat.Jpeg, 100);
                if (data == null) return string.Empty;

                string path = Path.Combine(FileSystem.CacheDirectory, $"ScanResult_{DateTime.Now:yyyyMMdd_HHmmss}.jpg");

                using (var stream = File.OpenWrite(path))
                {
                    data.SaveTo(stream);
                }
                return path;
            });

            if (string.IsNullOrEmpty(filePath)) throw new Exception("이미지 생성에 실패했습니다.");

            await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                await Share.Default.RequestAsync(new ShareFileRequest
                {
                    Title = "결과물 저장 및 공유",
                    File = new ShareFile(filePath)
                });
            });
        }
        catch (Exception ex)
        {
            await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                await DisplayAlert("저장 오류", ex.Message, "확인");
            });
        }
    }
}