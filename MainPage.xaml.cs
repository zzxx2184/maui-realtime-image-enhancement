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
    private const float MIN_INTENSITY = 0.1f;

    // 자동 밝기 보정 설정
    // 단일 기준점에서 갑자기 켜고 끄는 대신, 어두움·보통·밝음의
    // 소속도(membership)를 계산해 보정값을 부드럽게 결정합니다.
    private const float AUTO_TARGET_LUMINANCE = 145f;
    private const float AUTO_DARK_FULL_LUMINANCE = 80f;
    private const float AUTO_BRIGHT_FULL_LUMINANCE = 215f;
    private const float AUTO_DARK_SAMPLE_LUMINANCE = 90f;
    private const float AUTO_HIGHLIGHT_SAMPLE_LUMINANCE = 225f;
    private const float AUTO_MIN_GAIN = 0.50f;
    private const float AUTO_MAX_GAIN = 2.20f;
    private const float AUTO_SMOOTHING = 0.80f;

    private SKBitmap? _liveBitmap;
    private readonly object frameLock = new object();

    private bool isAutoOptimizationEnabled = true;
    private float currentScaleFactor = 1.0f;

    // 자동 밝기 보정값
    private float _currentAutoGain = 1.0f;
    private float _lastDisplayedAutoGain = 1.0f;

    private SKBitmap? _galleryBitmap;

    private readonly Stopwatch frameStopwatch = new Stopwatch();
    private int frameCount = 0;
    private DateTime lastFpsUpdate = DateTime.Now;

    public MainPage()
    {
        InitializeComponent();
        frameStopwatch.Start();
    }

    // 퍼지 멤버십 함수: 값이 구간 안에서 얼마나 해당 상태에 가까운지 0~1로 반환합니다.
    private static float LeftShoulder(float value, float fullStart, float zeroAt)
    {
        if (value <= fullStart)
            return 1.0f;

        if (value >= zeroAt)
            return 0.0f;

        return (zeroAt - value) / (zeroAt - fullStart);
    }

    private static float RightShoulder(float value, float zeroAt, float fullEnd)
    {
        if (value <= zeroAt)
            return 0.0f;

        if (value >= fullEnd)
            return 1.0f;

        return (value - zeroAt) / (fullEnd - zeroAt);
    }

    private static float Trapezoid(
        float value,
        float left,
        float plateauStart,
        float plateauEnd,
        float right)
    {
        if (value <= left || value >= right)
            return 0.0f;

        if (value >= plateauStart && value <= plateauEnd)
            return 1.0f;

        if (value < plateauStart)
            return (value - left) / (plateauStart - left);

        return (right - value) / (right - plateauEnd);
    }

    private static float CalculateFuzzyGain(
        float averageLuminance,
        float darkPixelRatio,
        float highlightPixelRatio)
    {
        // 어두움: 80 이하에서 최대, 120에서 0
        float darkMembership = LeftShoulder(
            averageLuminance,
            AUTO_DARK_FULL_LUMINANCE,
            120.0f);

        // 보통 밝기: 125~175 구간에서는 항상 1.0(보정하지 않음)
        float normalMembership = Trapezoid(
            averageLuminance,
            90.0f,
            125.0f,
            175.0f,
            195.0f);

        // 밝음: 175부터 서서히 적용, 215 이상에서 최대
        float brightMembership = RightShoulder(
            averageLuminance,
            175.0f,
            AUTO_BRIGHT_FULL_LUMINANCE);

        // 중간 밝기에서는 어두운 픽셀 비율 때문에 밝기가 올라가지 않도록
        // 평균 밝기가 낮을 때만 어두운 픽셀 비율을 보조 입력으로 사용합니다.
        float darkRatioInfluence =
            darkPixelRatio *
            Math.Clamp(
                (130.0f - averageLuminance) / 50.0f,
                0.0f,
                1.0f);

        darkMembership = Math.Clamp(
            Math.Max(darkMembership, darkRatioInfluence),
            0.0f,
            1.0f);

        // 평균 밝기가 중간이어도 하이라이트가 많으면 백화 가능성이 있으므로
        // 밝기가 130 이상일 때만 하이라이트 비율을 밝음 규칙에 반영합니다.
        float highlightRatioInfluence =
            highlightPixelRatio *
            Math.Clamp(
                (averageLuminance - 130.0f) / 60.0f,
                0.0f,
                1.0f) *
            2.0f;

        brightMembership = Math.Clamp(
            Math.Max(brightMembership, highlightRatioInfluence),
            0.0f,
            1.0f);

        float membershipSum =
            darkMembership +
            normalMembership +
            brightMembership;

        if (membershipSum <= 0.0001f)
            return 1.0f;

        // 퍼지 규칙의 출력값
        // IF 어두움 THEN 밝게, IF 보통 THEN 유지, IF 밝음 THEN 어둡게
        float weightedGain =
            darkMembership * 2.15f +
            normalMembership * 1.00f +
            brightMembership * 0.50f;

        return Math.Clamp(
            weightedGain / membershipSum,
            AUTO_MIN_GAIN,
            AUTO_MAX_GAIN);
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
            await DisplayAlert(
                "권한 필요",
                "카메라 권한이 없으면 실시간 스캔을 사용할 수 없습니다.",
                "확인");
        }

        if (CameraIntensitySlider != null)
        {
            // 퍼지 보정값(최대 2.2)이 게이지에 표시될 수 있도록 범위를 명시합니다.
            CameraIntensitySlider.Minimum = MIN_INTENSITY;
            CameraIntensitySlider.Maximum = MAX_INTENSITY;
            CameraIntensitySlider.IsEnabled =
                !isAutoOptimizationEnabled;
        }

        if (GalleryIntensitySlider != null)
        {
            GalleryIntensitySlider.Minimum = MIN_INTENSITY;
            GalleryIntensitySlider.Maximum = MAX_INTENSITY;
        }
    }

    private void OnCameraFrameReady(object? sender, SKBitmap? e)
    {
        if (e == null)
            return;

        lock (frameLock)
        {
            _liveBitmap?.Dispose();
            _liveBitmap = e;
        }

        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (CameraViewContainer != null &&
                CameraViewContainer.IsVisible)
            {
                CameraCanvas?.InvalidateSurface();
            }
        });
    }

    private void OnTabCameraClicked(object? sender, EventArgs e)
    {
        if (CameraViewContainer == null ||
            GalleryViewContainer == null)
        {
            return;
        }

        CameraViewContainer.IsVisible = true;
        GalleryViewContainer.IsVisible = false;

        if (TabCameraButton != null)
        {
            TabCameraButton.BackgroundColor =
                Color.FromArgb("#007AFF");
            TabCameraButton.TextColor = Colors.White;
        }

        if (TabGalleryButton != null)
        {
            TabGalleryButton.BackgroundColor =
                Colors.Transparent;
            TabGalleryButton.TextColor = Colors.Gray;
        }
    }

    private void OnTabGalleryClicked(object? sender, EventArgs e)
    {
        if (CameraViewContainer == null ||
            GalleryViewContainer == null)
        {
            return;
        }

        CameraViewContainer.IsVisible = false;
        GalleryViewContainer.IsVisible = true;

        if (TabGalleryButton != null)
        {
            TabGalleryButton.BackgroundColor =
                Color.FromArgb("#007AFF");
            TabGalleryButton.TextColor = Colors.White;
        }

        if (TabCameraButton != null)
        {
            TabCameraButton.BackgroundColor =
                Colors.Transparent;
            TabCameraButton.TextColor = Colors.Gray;
        }
    }

    private void OnAutoOptButtonClicked(object? sender, EventArgs e)
    {
        if (AutoOptButton == null)
            return;

        isAutoOptimizationEnabled =
            !isAutoOptimizationEnabled;

        if (CameraIntensitySlider != null)
        {
            CameraIntensitySlider.IsEnabled =
                !isAutoOptimizationEnabled;
        }

        if (isAutoOptimizationEnabled)
        {
            _currentAutoGain = 1.0f;
            _lastDisplayedAutoGain = 1.0f;

            if (CameraIntensitySlider != null)
            {
                CameraIntensitySlider.Value = 1.0;
            }

            AutoOptButton.Text = "⚡ 최적화: ON";
            AutoOptButton.BackgroundColor =
                Color.FromArgb("#28A745");
        }
        else
        {
            currentScaleFactor = 1.0f;
            _lastDisplayedAutoGain = 1.0f;

            AutoOptButton.Text = "⚡ 최적화: OFF";
            AutoOptButton.BackgroundColor =
                Color.FromArgb("#DC3545");
        }
    }

    private void OnCameraCanvasPaintSurface(
        object? sender,
        SKPaintSurfaceEventArgs e)
    {
        if (e.Surface == null ||
            e.Surface.Canvas == null)
        {
            return;
        }

        SKImageInfo info = e.Info;
        SKCanvas canvas = e.Surface.Canvas;

        canvas.Clear(SKColors.Black);

        SKBitmap? frameToRender = null;

        lock (frameLock)
        {
            if (_liveBitmap != null)
            {
                frameToRender = _liveBitmap.Copy();
            }
        }

        if (frameToRender == null)
            return;

        long startTime =
            frameStopwatch.ElapsedMilliseconds;

        SKBitmap targetBitmap = frameToRender;
        bool isScaled = false;

        // FPS가 낮을 때 렌더링 해상도 조절
        if (isAutoOptimizationEnabled &&
            currentScaleFactor < 1.0f)
        {
            int scaledWidth =
                Math.Max(1,
                    (int)(frameToRender.Width *
                          currentScaleFactor));

            int scaledHeight =
                Math.Max(1,
                    (int)(frameToRender.Height *
                          currentScaleFactor));

            var resized = frameToRender.Resize(
                new SKImageInfo(
                    scaledWidth,
                    scaledHeight),
                new SKSamplingOptions(
                    SKFilterMode.Linear));

            if (resized != null)
            {
                targetBitmap = resized;
                isScaled = true;
            }
        }

        // 자동 밝기 분석
        if (isAutoOptimizationEnabled)
        {
            int stepX =
                Math.Max(1, targetBitmap.Width / 10);

            int stepY =
                Math.Max(1, targetBitmap.Height / 10);

            long totalLuminance = 0;
            int sampleCount = 0;
            int darkSampleCount = 0;
            int highlightSampleCount = 0;

            for (int x = 0;
                 x < targetBitmap.Width;
                 x += stepX)
            {
                for (int y = 0;
                     y < targetBitmap.Height;
                     y += stepY)
                {
                    var color = targetBitmap.GetPixel(x, y);

                    float luminance =
                        color.Red * 0.299f +
                        color.Green * 0.587f +
                        color.Blue * 0.114f;

                    totalLuminance +=
                        (long)luminance;

                    if (luminance <= AUTO_DARK_SAMPLE_LUMINANCE)
                        darkSampleCount++;

                    if (luminance >= AUTO_HIGHLIGHT_SAMPLE_LUMINANCE)
                        highlightSampleCount++;

                    sampleCount++;
                }
            }

            float avgLuminance =
                sampleCount > 0
                    ? (float)totalLuminance /
                      sampleCount
                    : AUTO_TARGET_LUMINANCE;

            // 퍼지 제어: 밝기 차이를 한 번에 판단하지 않고
            // 어두움·보통·밝음의 정도를 함께 반영해 목표 보정값을 계산합니다.
            float darkPixelRatio =
                sampleCount > 0
                    ? (float)darkSampleCount / sampleCount
                    : 0.0f;

            float highlightPixelRatio =
                sampleCount > 0
                    ? (float)highlightSampleCount / sampleCount
                    : 0.0f;

            float desiredGain =
                CalculateFuzzyGain(
                    avgLuminance,
                    darkPixelRatio,
                    highlightPixelRatio);

            // EMA는 퍼지 결과를 화면에 부드럽게 반영하는 역할만 담당합니다.
            _currentAutoGain =
                _currentAutoGain * AUTO_SMOOTHING +
                desiredGain * (1.0f - AUTO_SMOOTHING);

            // 화면의 슬라이더에는 자동 보정값을 참고용으로 표시합니다.
            if (CameraIntensitySlider != null &&
                Math.Abs(_currentAutoGain - _lastDisplayedAutoGain) > 0.01f)
            {
                float gainForDisplay = _currentAutoGain;
                _lastDisplayedAutoGain = gainForDisplay;

                MainThread.BeginInvokeOnMainThread(() =>
                {
                    if (CameraIntensitySlider == null)
                        return;

                    double displayValue =
                        Math.Clamp(
                            (double)gainForDisplay,
                            CameraIntensitySlider.Minimum,
                            CameraIntensitySlider.Maximum);

                    if (Math.Abs(
                        CameraIntensitySlider.Value -
                        displayValue) > 0.02)
                    {
                        CameraIntensitySlider.Value =
                            displayValue;
                    }
                });
            }
        }

        float scale;
        float offset;

        if (isAutoOptimizationEnabled)
        {
            // 자동 모드
            // 어두운 환경에서는 1보다 커져 밝아짐
            // 밝은 환경에서는 1보다 작아져 어두워짐
            scale = _currentAutoGain;
            offset = 0f;
        }
        else
        {
            // 수동 모드
            float intensity =
                CameraIntensitySlider != null
                    ? (float)CameraIntensitySlider.Value
                    : 1.0f;

            scale = intensity;
            offset = 0f;

            if (intensity < 1.0f)
            {
                float contrast =
                    1.0f +
                    (1.0f - intensity) *
                    3.0f;

                scale = contrast;
                offset = 1.0f - contrast;
            }
        }

        using (SKPaint paint = new SKPaint
        {
            IsAntialias = true
        })
        {
            float[] colorMatrix =
            {
                scale, 0, 0, 0, offset,
                0, scale, 0, 0, offset,
                0, 0, scale, 0, offset,
                0, 0, 0, 1, 0
            };

            paint.ColorFilter =
                SKColorFilter.CreateColorMatrix(
                    colorMatrix);

            canvas.Save();

            canvas.Translate(info.Width, 0);
            canvas.RotateDegrees(90);

            SKRect destRect =
                new SKRect(
                    0,
                    0,
                    info.Height,
                    info.Width);

            canvas.DrawBitmap(
                targetBitmap,
                destRect,
                new SKSamplingOptions(
                    SKFilterMode.Linear),
                paint);

            canvas.Restore();
        }

        if (isScaled)
        {
            targetBitmap.Dispose();
        }

        frameToRender.Dispose();

        long elapsedTime =
            frameStopwatch.ElapsedMilliseconds -
            startTime;

        UpdatePerformanceMetrics(elapsedTime);
    }

    private void UpdatePerformanceMetrics(
        long processTimeMs)
    {
        frameCount++;

        TimeSpan elapsed =
            DateTime.Now - lastFpsUpdate;

        if (elapsed.TotalSeconds < 1.0)
            return;

        double currentFps =
            frameCount / elapsed.TotalSeconds;

        frameCount = 0;
        lastFpsUpdate = DateTime.Now;

        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (FpsLabel != null)
            {
                FpsLabel.Text =
                    $"FPS: {currentFps:F1}";
            }

            if (isAutoOptimizationEnabled)
            {
                if (processTimeMs > 40 ||
                    currentFps < 20.0)
                {
                    currentScaleFactor = 0.5f;
                }
                else if (processTimeMs < 15 &&
                         currentFps >= 45.0)
                {
                    currentScaleFactor = 1.0f;
                }
            }
        });
    }

    private async void OnLoadGalleryClicked(
        object? sender,
        EventArgs e)
    {
        try
        {
            var result =
                await FilePicker.Default.PickAsync(
                    new PickOptions
                    {
                        PickerTitle =
                            "스캔할 사진을 선택하세요",

                        FileTypes =
                            FilePickerFileType.Images
                    });

            if (result != null)
            {
                using var stream =
                    await result.OpenReadAsync();

                _galleryBitmap?.Dispose();

                _galleryBitmap =
                    SKBitmap.Decode(stream);

                if (GalleryCanvas != null)
                {
                    GalleryCanvas.InvalidateSurface();
                }
            }
        }
        catch (Exception ex)
        {
            await DisplayAlert(
                "오류",
                $"사진을 불러오지 못했습니다: {ex.Message}",
                "확인");
        }
    }

    private void OnGallerySliderValueChanged(
        object? sender,
        ValueChangedEventArgs e)
    {
        if (GalleryViewContainer != null &&
            GalleryViewContainer.IsVisible &&
            GalleryCanvas != null)
        {
            GalleryCanvas.InvalidateSurface();
        }
    }

    private void OnGalleryCanvasPaintSurface(
        object? sender,
        SKPaintSurfaceEventArgs e)
    {
        if (_galleryBitmap == null ||
            e.Surface == null ||
            e.Surface.Canvas == null)
        {
            return;
        }

        SKImageInfo info = e.Info;
        SKCanvas canvas = e.Surface.Canvas;

        canvas.Clear(SKColors.Black);

        float intensity =
            GalleryIntensitySlider != null
                ? (float)GalleryIntensitySlider.Value
                : 1.0f;

        float scale = intensity;
        float offset = 0f;

        if (intensity < 1.0f)
        {
            float contrast =
                1.0f +
                (1.0f - intensity) *
                3.0f;

            scale = contrast;
            offset = 1.0f - contrast;
        }

        using (SKPaint paint = new SKPaint
        {
            IsAntialias = true
        })
        {
            float[] colorMatrix =
            {
                scale, 0, 0, 0, offset,
                0, scale, 0, 0, offset,
                0, 0, scale, 0, offset,
                0, 0, 0, 1, 0
            };

            paint.ColorFilter =
                SKColorFilter.CreateColorMatrix(
                    colorMatrix);

            float drawScale =
                Math.Min(
                    (float)info.Width /
                    _galleryBitmap.Width,

                    (float)info.Height /
                    _galleryBitmap.Height);

            float x =
                (info.Width -
                 drawScale *
                 _galleryBitmap.Width) / 2;

            float y =
                (info.Height -
                 drawScale *
                 _galleryBitmap.Height) / 2;

            SKRect destRect =
                new SKRect(
                    x,
                    y,
                    x +
                    drawScale *
                    _galleryBitmap.Width,
                    y +
                    drawScale *
                    _galleryBitmap.Height);

            canvas.DrawBitmap(
                _galleryBitmap,
                destRect,
                new SKSamplingOptions(
                    SKFilterMode.Linear),
                paint);
        }
    }

    private void OnCaptureLiveClicked(
        object? sender,
        EventArgs e)
    {
        if (_liveBitmap == null)
            return;

        SKBitmap? snapshot = null;

        lock (frameLock)
        {
            snapshot = _liveBitmap.Copy();
        }

        if (snapshot == null)
            return;

        SKBitmap rotatedBitmap =
            new SKBitmap(
                snapshot.Height,
                snapshot.Width);

        using (var canvas =
               new SKCanvas(rotatedBitmap))
        {
            canvas.Translate(
                rotatedBitmap.Width,
                0);

            canvas.RotateDegrees(90);

            canvas.DrawBitmap(
                snapshot,
                0,
                0,
                new SKSamplingOptions());
        }

        _galleryBitmap?.Dispose();
        _galleryBitmap = rotatedBitmap;

        if (GalleryIntensitySlider != null &&
            CameraIntensitySlider != null)
        {
            GalleryIntensitySlider.Value =
                CameraIntensitySlider.Value;
        }

        OnTabGalleryClicked(
            this,
            EventArgs.Empty);

        GalleryCanvas?.InvalidateSurface();

        snapshot.Dispose();
    }

    private async void OnSaveGalleryClicked(
        object? sender,
        EventArgs e)
    {
        if (_galleryBitmap == null)
        {
            await DisplayAlert(
                "알림",
                "먼저 사진을 불러와주세요.",
                "확인");

            return;
        }

        float intensity =
            GalleryIntensitySlider != null
                ? (float)GalleryIntensitySlider.Value
                : 1.0f;

        await SaveFilteredImageAsync(
            _galleryBitmap,
            Math.Min(
                intensity,
                MAX_INTENSITY),
            rotate: false);
    }

    private async Task SaveFilteredImageAsync(
        SKBitmap? originalBitmap,
        float intensity,
        bool rotate)
    {
        if (originalBitmap == null)
            return;

        try
        {
            string filePath =
                await Task.Run(() =>
                {
                    int width =
                        rotate
                            ? originalBitmap.Height
                            : originalBitmap.Width;

                    int height =
                        rotate
                            ? originalBitmap.Width
                            : originalBitmap.Height;

                    using var surface =
                        SKSurface.Create(
                            new SKImageInfo(
                                width,
                                height));

                    if (surface == null ||
                        surface.Canvas == null)
                    {
                        return string.Empty;
                    }

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
                        float contrast =
                            1.0f +
                            (1.0f - intensity) *
                            3.0f;

                        scale = contrast;
                        offset = 1.0f - contrast;
                    }

                    using var paint =
                        new SKPaint
                        {
                            IsAntialias = true
                        };

                    float[] colorMatrix =
                    {
                        scale, 0, 0, 0, offset,
                        0, scale, 0, 0, offset,
                        0, 0, scale, 0, offset,
                        0, 0, 0, 1, 0
                    };

                    paint.ColorFilter =
                        SKColorFilter.CreateColorMatrix(
                            colorMatrix);

                    canvas.DrawBitmap(
                        originalBitmap,
                        0,
                        0,
                        new SKSamplingOptions(
                            SKFilterMode.Linear),
                        paint);

                    using var image =
                        surface.Snapshot();

                    using var data =
                        image?.Encode(
                            SKEncodedImageFormat.Jpeg,
                            100);

                    if (data == null)
                        return string.Empty;

                    string path =
                        Path.Combine(
                            FileSystem.CacheDirectory,
                            $"ScanResult_{DateTime.Now:yyyyMMdd_HHmmss}.jpg");

                    using (var stream =
                           File.OpenWrite(path))
                    {
                        data.SaveTo(stream);
                    }

                    return path;
                });

            if (string.IsNullOrEmpty(filePath))
            {
                throw new Exception(
                    "이미지 생성에 실패했습니다.");
            }

            await MainThread.InvokeOnMainThreadAsync(
                async () =>
                {
                    await Share.Default.RequestAsync(
                        new ShareFileRequest
                        {
                            Title =
                                "결과물 저장 및 공유",

                            File =
                                new ShareFile(filePath)
                        });
                });
        }
        catch (Exception ex)
        {
            await MainThread.InvokeOnMainThreadAsync(
                async () =>
                {
                    await DisplayAlert(
                        "저장 오류",
                        ex.Message,
                        "확인");
                });
        }
    }
}
