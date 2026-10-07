using Microsoft.Maui.Handlers;
using SkiaSharp;
using System;

#if ANDROID
using AndroidX.Camera.Core;
using AndroidX.Camera.Lifecycle;
using AndroidX.Camera.View;
using AndroidX.Core.Content;
using AndroidX.Lifecycle;
using Java.Util.Concurrent;
#endif

namespace lasthomework;

#if ANDROID
public partial class NativeCameraHandler : ViewHandler<NativeCameraView, PreviewView>
{
    public static IPropertyMapper<NativeCameraView, NativeCameraHandler> PropertyMapper = new PropertyMapper<NativeCameraView, NativeCameraHandler>(ViewMapper) { };

    public NativeCameraHandler() : base(PropertyMapper) { }

    private ProcessCameraProvider? _cameraProvider;
    private IExecutorService? _cameraExecutor;
    private ImageAnalysis? _imageAnalyzer;

    protected override PreviewView CreatePlatformView()
    {
        return new PreviewView(Context);
    }

    protected override void ConnectHandler(PreviewView platformView)
    {
        base.ConnectHandler(platformView);
        StartCamera(platformView);
    }

    protected override void DisconnectHandler(PreviewView platformView)
    {
        _cameraExecutor?.Shutdown();
        _cameraProvider?.UnbindAll();
        base.DisconnectHandler(platformView);
    }

    private void StartCamera(PreviewView previewView)
    {
        var cameraProviderFuture = ProcessCameraProvider.GetInstance(Context);
        cameraProviderFuture.AddListener(new Java.Lang.Runnable(() =>
        {
            _cameraProvider = (ProcessCameraProvider)cameraProviderFuture.Get();
            _cameraExecutor = Executors.NewSingleThreadExecutor();

            var preview = new Preview.Builder().Build();
            preview.SetSurfaceProvider(ContextCompat.GetMainExecutor(Context), previewView.SurfaceProvider);

            _imageAnalyzer = new ImageAnalysis.Builder()
                .SetOutputImageFormat(ImageAnalysis.OutputImageFormatYuv420888)
                .SetBackpressureStrategy(ImageAnalysis.StrategyKeepOnlyLatest)
                .Build();

            _imageAnalyzer.SetAnalyzer(_cameraExecutor, new YuvToSkiaAnalyzer(VirtualView));

            var cameraSelector = CameraSelector.DefaultBackCamera;

            try
            {
                _cameraProvider.UnbindAll();
                var lifecycleOwner = (ILifecycleOwner)Context;
                _cameraProvider.BindToLifecycle(lifecycleOwner, cameraSelector!, preview, _imageAnalyzer!);
            }
            catch (Exception exc)
            {
                System.Diagnostics.Debug.WriteLine($"Camera error: {exc.Message}");
            }

        }), ContextCompat.GetMainExecutor(Context));
    }
}

public class YuvToSkiaAnalyzer : Java.Lang.Object, ImageAnalysis.IAnalyzer
{
    private readonly NativeCameraView _virtualView;

    public YuvToSkiaAnalyzer(NativeCameraView virtualView)
    {
        _virtualView = virtualView;
    }

    public void Analyze(IImageProxy? image)
    {
        if (image == null) return;
        try
        {
            var planes = image.GetPlanes();
            if (planes == null || planes.Length < 3) return;

            var yBuffer = planes[0].Buffer;
            var uBuffer = planes[1].Buffer;
            var vBuffer = planes[2].Buffer;

            int ySize = yBuffer.Remaining();
            int uSize = uBuffer.Remaining();
            int vSize = vBuffer.Remaining();

            byte[] nv21 = new byte[ySize + uSize + vSize];
            yBuffer.Get(nv21, 0, ySize);
            vBuffer.Get(nv21, ySize, vSize);
            uBuffer.Get(nv21, ySize + vSize, uSize);

            Android.Graphics.YuvImage yuvImage = new Android.Graphics.YuvImage(
                nv21,
                Android.Graphics.ImageFormatType.Nv21,
                image.Width,
                image.Height,
                null);

            using var outStream = new System.IO.MemoryStream();
            yuvImage.CompressToJpeg(new Android.Graphics.Rect(0, 0, image.Width, image.Height), 80, outStream);
            outStream.Position = 0;

            var skBitmap = SKBitmap.Decode(outStream);
            if (skBitmap != null)
            {
                _virtualView.RaiseFrameReady(skBitmap);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Analyze Error: {ex.Message}");
        }
        finally
        {
            image.Close();
        }
    }
}
#else
public partial class NativeCameraHandler : ElementHandler<NativeCameraView, object>
{
    public static IPropertyMapper<NativeCameraView, NativeCameraHandler> PropertyMapper = new PropertyMapper<NativeCameraView, NativeCameraHandler>(ElementMapper) { };
    public NativeCameraHandler() : base(PropertyMapper) { }
    protected override object CreatePlatformElement() => new object();
}
#endif