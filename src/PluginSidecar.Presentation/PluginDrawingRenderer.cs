using System.Security.Cryptography;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Sdk.Abi100.Data;
using PluginSidecar.Application;
using SkiaSharp;

namespace PluginSidecar.Presentation;

/// <summary>Owned encoded pixels; contains no Host UI or guest blob references.</summary>
public sealed record PluginRasterImage(int Width, int Height, ReadOnlyMemory<byte> Png);

/// <summary>Rasterizes a drawing snapshot inside the Sidecar. The blob reader must return an owned copy.</summary>
public sealed class PluginDrawingRenderer(Func<PluginIdentity, BlobRef, byte[]> readBlob)
{
    private const long ImagePixelLimit = 4 * 1024 * 1024;
    private readonly Func<PluginIdentity, BlobRef, byte[]> _readBlob = readBlob ?? throw new ArgumentNullException(nameof(readBlob));

    public PluginRasterImage Render(PluginDrawing drawing, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(drawing);
        token.ThrowIfCancellationRequested();
        if (!double.IsFinite(drawing.Width) || !double.IsFinite(drawing.Height)
            || drawing.Width is < 1 or > 2048 || drawing.Height is < 1 or > 2048
            || drawing.Commands.IsDefault || drawing.Commands.Length > 1024)
            throw new InvalidDataException("Invalid drawing surface.");
        foreach (var command in drawing.Commands) Validate(command);
        int width = (int)Math.Ceiling(drawing.Width), height = (int)Math.Ceiling(drawing.Height);
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        Dictionary<BlobRef, SKBitmap> images = [];
        long cachedPixels = 0;
        SKTypeface? typeface = null;
        try
        {
            foreach (var command in drawing.Commands)
            {
                token.ThrowIfCancellationRequested();
                var v = command.Coordinates.Select(n => (float)n).ToArray();
                using var paint = new SKPaint { Color = SKColor.Parse(command.Color), IsAntialias = true };
                switch (command.Kind)
                {
                    case DrawCommandKind.Line:
                        paint.StrokeWidth = v.Length == 5 ? Math.Clamp(v[4], .1f, 128) : 1;
                        canvas.DrawLine(v[0], v[1], v[2], v[3], paint);
                        break;
                    case DrawCommandKind.Rectangle:
                        canvas.DrawRect(v[0], v[1], v[2], v[3], paint);
                        break;
                    case DrawCommandKind.Ellipse:
                        canvas.DrawOval(SKRect.Create(v[0], v[1], v[2], v[3]), paint);
                        break;
                    case DrawCommandKind.Clip:
                        canvas.ClipRect(SKRect.Create(v[0], v[1], v[2], v[3]));
                        break;
                    case DrawCommandKind.Transform:
                        canvas.Concat(new SKMatrix(v[0], v[2], v[4], v[1], v[3], v[5], 0, 0, 1));
                        if (canvas.TotalMatrix.Values.Any(value => !float.IsFinite(value)))
                            throw new InvalidDataException("Drawing transform overflow.");
                        break;
                    case DrawCommandKind.Text:
                        using (var font = new SKFont(typeface ??= Typeface(token), Math.Clamp(v[2], 1, 128)))
                            canvas.DrawText(command.Text, v[0], v[1], font, paint);
                        break;
                    case DrawCommandKind.Path:
                        using (var path = new SKPath())
                        {
                            path.MoveTo(v[0], v[1]);
                            for (int i = 2; i < v.Length; i += 2) path.LineTo(v[i], v[i + 1]);
                            paint.Style = SKPaintStyle.Stroke; paint.StrokeWidth = 1;
                            canvas.DrawPath(path, paint);
                        }
                        break;
                    case DrawCommandKind.Image:
                        canvas.DrawBitmap(Image(command.Image!), SKRect.Create(v[0], v[1], v[2], v[3]), paint);
                        break;
                }
            }
            token.ThrowIfCancellationRequested();
            using var snapshot = SKImage.FromBitmap(bitmap);
            using var encoded = snapshot.Encode(SKEncodedImageFormat.Png, 100);
            if (encoded is null || encoded.Size is <= 0 or > 16 * 1024 * 1024)
                throw new InvalidDataException("Invalid encoded drawing surface.");
            token.ThrowIfCancellationRequested();
            return new(width, height, encoded.ToArray());
        }
        finally { typeface?.Dispose(); ClearImages(); }

        SKBitmap Image(BlobRef reference)
        {
            if (images.TryGetValue(reference, out var cached)) return cached;
            byte[] bytes = _readBlob(drawing.Owner, reference);
            try
            {
                token.ThrowIfCancellationRequested();
                if (bytes.Length > 32 * 1024 * 1024) throw new InvalidDataException("Drawing image exceeds blob budget.");
                using var data = SKData.CreateCopy(bytes);
                using var codec = SKCodec.Create(data);
                if (codec is null || codec.Info.Width is < 1 or > 4096 || codec.Info.Height is < 1 or > 4096
                    || (long)codec.Info.Width * codec.Info.Height > ImagePixelLimit)
                    throw new InvalidDataException("Invalid drawing image dimensions.");
                long pixels = (long)codec.Info.Width * codec.Info.Height;
                if (cachedPixels + pixels > ImagePixelLimit) ClearImages();
                var image = new SKBitmap(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
                try
                {
                    if (codec.GetPixels(image.Info, image.GetPixels()) != SKCodecResult.Success)
                        throw new InvalidDataException("Incomplete drawing image.");
                    token.ThrowIfCancellationRequested();
                    images.Add(reference, image); cachedPixels += pixels;
                    return image;
                }
                catch { image.Dispose(); throw; }
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        void ClearImages()
        {
            foreach (var image in images.Values) image.Dispose();
            images.Clear(); cachedPixels = 0;
        }
    }

    private static SKTypeface Typeface(CancellationToken token)
    {
        // Some system defaults resolve to a font format unsupported by Skia and
        // yield a non-null face with zero glyphs. Do not silently draw empty text.
        var face = SKTypeface.FromFamilyName(null);
        if (face is { GlyphCount: > 0 }) return face;
        face?.Dispose();
        foreach (string family in new[] { "Segoe UI", "Arial", "DejaVu Sans", "Noto Sans", "Liberation Sans" }
            .Concat(SKFontManager.Default.FontFamilies).Distinct(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            face = SKTypeface.FromFamilyName(family);
            if (face is { GlyphCount: > 0 }) return face;
            face?.Dispose();
        }
        throw new InvalidDataException("No usable system font for drawing text.");
    }

    private static void Validate(DrawCommand command)
    {
        if (command is null || command.Coordinates.IsDefault) throw new InvalidDataException("Missing drawing command.");
        int count = command.Coordinates.Length;
        bool valid = command.Kind switch
        {
            DrawCommandKind.Line => count is 4 or 5,
            DrawCommandKind.Rectangle or DrawCommandKind.Ellipse or DrawCommandKind.Clip => count == 4,
            DrawCommandKind.Image => count == 4 && command.Image is not null,
            DrawCommandKind.Transform => count == 6,
            DrawCommandKind.Text => count == 3 && command.Text is { Length: <= 4096 },
            DrawCommandKind.Path => count is >= 4 and <= 4096 && count % 2 == 0,
            _ => false
        };
        if (!valid || command.Coordinates.Any(value => !double.IsFinite(value) || Math.Abs(value) > 1000000)
            || command.Color is not { Length: 7 or 9 } || command.Color[0] != '#'
            || command.Color.AsSpan(1).ContainsAnyExcept("0123456789abcdefABCDEF"))
            throw new InvalidDataException("Invalid drawing command.");
    }
}
