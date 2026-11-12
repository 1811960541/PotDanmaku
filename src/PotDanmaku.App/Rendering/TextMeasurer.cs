using PotDanmaku.Core;
using Vortice.DirectWrite;

namespace PotDanmaku.App.Rendering;

/// <summary>Each background layout build owns its own DirectWrite factory.</summary>
internal sealed class TextMeasurer : IDisposable
{
    private readonly IDWriteFactory factory = DWrite.DWriteCreateFactory<IDWriteFactory>();
    private readonly Dictionary<float, IDWriteTextFormat> formats = [];
    public TextSize Measure(string text, float size)
    {
        if (!formats.TryGetValue(size, out var format))
        {
            format = factory.CreateTextFormat("Microsoft YaHei UI", size); format.WordWrapping = WordWrapping.NoWrap; formats[size] = format;
        }
        using var layout = factory.CreateTextLayout(text, format, 100000, Math.Max(1000, size * 4));
        return new(layout.Metrics.WidthIncludingTrailingWhitespace, layout.Metrics.Height);
    }
    public void Dispose() { foreach (var format in formats.Values) format.Dispose(); factory.Dispose(); }
}
