using System.Numerics;
using PotDanmaku.Core;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;
using D2DAlpha = Vortice.DCommon.AlphaMode;
using DxgiAlpha = Vortice.DXGI.AlphaMode;

namespace PotDanmaku.App.Rendering;

/// <summary>All GPU resources are created, used and disposed on one rendering thread.</summary>
internal sealed class GpuRenderer : IDisposable
{
    private ID3D11Device? device;
    private ID3D11DeviceContext? immediate;
    private IDXGISwapChain1? swapChain;
    private ID2D1Factory1? factory;
    private ID2D1Device? d2dDevice;
    private ID2D1DeviceContext? context;
    private ID2D1Bitmap1? targetBitmap;
    private IDCompositionDevice? composition;
    private IDCompositionTarget? compositionTarget;
    private IDCompositionVisual? visual;
    private IDWriteFactory? textFactory;
    private IDWriteTextFormat? textFormat;
    private readonly Dictionary<string, (IDWriteTextLayout Layout, LinkedListNode<string> Node)> layouts = new(StringComparer.Ordinal);
    private readonly LinkedList<string> lru = new();
    private ID2D1SolidColorBrush? foreground, outline;
    private int width, height;
    private float fontSize;
    public bool TryGetPresentationStatistics(out Vortice.DXGI.FrameStatistics stats) => swapChain!.GetFrameStatistics(out stats).Success;

    public GpuRenderer(nint hwnd, int initialWidth, int initialHeight)
    {
        try
        {
            Vortice.Direct3D11.D3D11.D3D11CreateDevice(nint.Zero, DriverType.Hardware,
                DeviceCreationFlags.BgraSupport, [Vortice.Direct3D.FeatureLevel.Level_11_0],
                out device, out immediate).CheckError();
            using var dxgiDevice = device!.QueryInterface<IDXGIDevice>();
            using var dxgiFactory = Vortice.DXGI.DXGI.CreateDXGIFactory1<IDXGIFactory2>();
            swapChain = dxgiFactory.CreateSwapChainForComposition(device, new SwapChainDescription1
            {
                Width = (uint)initialWidth, Height = (uint)initialHeight,
                Format = Format.B8G8R8A8_UNorm, BufferCount = 2,
                BufferUsage = Usage.RenderTargetOutput, SampleDescription = new(1, 0),
                Scaling = Scaling.Stretch, SwapEffect = SwapEffect.FlipSequential,
                AlphaMode = DxgiAlpha.Premultiplied
            });
            using (var latency = dxgiDevice.QueryInterface<IDXGIDevice1>()) latency.MaximumFrameLatency = 1;
            factory = D2D1.D2D1CreateFactory<ID2D1Factory1>(Vortice.Direct2D1.FactoryType.SingleThreaded);
            d2dDevice = factory.CreateDevice(dxgiDevice);
            context = d2dDevice.CreateDeviceContext(DeviceContextOptions.None);
            context.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;
            textFactory = DWrite.DWriteCreateFactory<IDWriteFactory>();
            foreground = context.CreateSolidColorBrush(new Color4(1, 1, 1, 1));
            outline = context.CreateSolidColorBrush(new Color4(0, 0, 0, 0.85f));
            composition = DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice);
            composition.CreateTargetForHwnd(hwnd, true, out compositionTarget).CheckError();
            visual = composition.CreateVisual();
            visual.SetContent(swapChain).CheckError();
            compositionTarget.SetRoot(visual).CheckError();
            composition.Commit().CheckError();
            width = initialWidth; height = initialHeight;
            CreateTarget();
        }
        catch { Dispose(); throw; }
    }

    private void CreateTarget()
    {
        using var surface = swapChain!.GetBuffer<IDXGISurface>(0);
        targetBitmap = context!.CreateBitmapFromDxgiSurface(surface, new BitmapProperties1
        {
            PixelFormat = new(Format.B8G8R8A8_UNorm, D2DAlpha.Premultiplied),
            DpiX = 96, DpiY = 96, BitmapOptions = BitmapOptions.Target | BitmapOptions.CannotDraw
        });
        context.Target = targetBitmap;
    }

    public void Resize(int w, int h)
    {
        if (w == width && h == height) return;
        context!.Target = null;
        targetBitmap?.Dispose(); targetBitmap = null;
        swapChain!.ResizeBuffers(2, (uint)w, (uint)h, Format.Unknown, SwapChainFlags.None).CheckError();
        width = w; height = h;
        CreateTarget();
    }

    private IDWriteTextLayout GetText(string value, float size)
    {
        if (textFormat == null || Math.Abs(fontSize - size) > 0.01)
        {
            foreach (var entry in layouts.Values) entry.Layout.Dispose(); layouts.Clear(); lru.Clear();
            textFormat?.Dispose(); fontSize = size;
            textFormat = textFactory!.CreateTextFormat("Microsoft YaHei UI", size); textFormat.WordWrapping = WordWrapping.NoWrap;
        }
        if (layouts.TryGetValue(value, out var hit)) { lru.Remove(hit.Node); lru.AddLast(hit.Node); return hit.Layout; }
        if (layouts.Count >= 2048)
        {
            var oldest = lru.First!; layouts[oldest.Value].Layout.Dispose(); layouts.Remove(oldest.Value); lru.RemoveFirst();
        }
        var layout = textFactory!.CreateTextLayout(value, textFormat, 100000, Math.Max(size * 4, 1000));
        layouts[value] = (layout, lru.AddLast(value)); return layout;
    }

    public void Draw(IReadOnlyList<ScheduledItem> active, double mediaMs, float size, float opacity, float stroke, bool preserveColor)
    {
        context!.BeginDraw();
        context.Clear(new Color4(0, 0, 0, 0));
        context.Transform = Matrix3x2.CreateScale(height / 1080f);
        foreach (var item in active)
        {
            var layout = GetText(item.Item.Text, size);
            var color = preserveColor ? item.Item.Color : DanmakuColor.White with { A = item.Item.Color.A };
            foreground!.Color = new Color4(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f);
            foreground.Opacity = opacity; outline!.Opacity = opacity * color.A / 255f;
            var position = new Vector2(item.X(mediaMs), item.Y);
            if (stroke > 0)
                foreach (var d in OutlineDirections) context.DrawTextLayout(position + d * stroke, layout, outline, DrawTextOptions.NoSnap);
            context.DrawTextLayout(position, layout, foreground, DrawTextOptions.NoSnap);
        }
        context.EndDraw().CheckError();
        swapChain!.Present(1, PresentFlags.None).CheckError();
    }

    private static readonly Vector2[] OutlineDirections = [new(-1, 0), new(1, 0), new(0, -1), new(0, 1)];

    public void Dispose()
    {
        foreach (var entry in layouts.Values) entry.Layout.Dispose(); layouts.Clear(); lru.Clear();
        textFormat?.Dispose(); foreground?.Dispose(); outline?.Dispose();
        if (context != null) context.Target = null;
        targetBitmap?.Dispose(); context?.Dispose(); d2dDevice?.Dispose(); factory?.Dispose();
        visual?.Dispose(); compositionTarget?.Dispose(); composition?.Dispose();
        swapChain?.Dispose(); immediate?.Dispose(); device?.Dispose(); textFactory?.Dispose();
    }
}
