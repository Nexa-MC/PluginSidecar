using System.Collections.Immutable;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Hosting;
using Nexa.Plugin.Sdk.Abi100;
using Nexa.Plugin.Sdk.Abi100.Data;
using PluginSidecar.Application;
using PluginSidecar.Packages;
using PluginSidecar.Presentation;
using PluginSidecar.WasmRuntime;
using SkiaSharp;

internal static partial class PresentationTests
{
    private static async Task DrawingRendererChecks()
    {
        var owner = new PluginIdentity("example.desktop", "1.0.0", Guid.NewGuid());
        var surface = new DrawingSurfaceRef(new("surface"), owner.LoadInstanceId, new("one"));
        PluginDrawing Drawing(params DrawCommand[] commands) => new(owner, new("example.desktop:canvas"), surface, 16, 16, commands.ToImmutableArray());
        static DrawCommand Draw(DrawCommandKind kind, string color, params double[] coordinates) => new(kind, coordinates.ToImmutableArray(), color, null, null);
        var renderer = new PluginDrawingRenderer((_, _) => throw new InvalidOperationException("Unexpected blob read."));
        using (var bitmap = Decode(renderer.Render(Drawing())))
        { Check(bitmap.Width == 16 && bitmap.Height == 16); Check(bitmap.GetPixel(8, 8).Alpha == 0); }
        using (var bitmap = Decode(renderer.Render(Drawing(
            Draw(DrawCommandKind.Rectangle, "#FF0000", 0, 0, 16, 16),
            Draw(DrawCommandKind.Clip, "#000000", 4, 4, 8, 8),
            Draw(DrawCommandKind.Transform, "#000000", 1, 0, 0, 1, 4, 4),
            Draw(DrawCommandKind.Rectangle, "#0000FF", 0, 0, 16, 16)))))
        { Check(bitmap.GetPixel(1, 1) == SKColors.Red); Check(bitmap.GetPixel(8, 8) == SKColors.Blue); Check(bitmap.GetPixel(14, 14) == SKColors.Red); }
        using (var bitmap = Decode(renderer.Render(Drawing(Draw(DrawCommandKind.Rectangle, "#80FF0000", 0, 0, 16, 16)))))
        { Check(bitmap.GetPixel(8, 8).Alpha == 128); Check(bitmap.GetPixel(8, 8).Red == 255); }
        foreach (var command in new[]
        {
            Draw(DrawCommandKind.Line, "#00FF00", 1, 8, 14, 8, 3),
            Draw(DrawCommandKind.Ellipse, "#00FF00", 2, 2, 12, 12),
            Draw(DrawCommandKind.Path, "#00FF00", 1, 1, 14, 14, 1, 14),
            new DrawCommand(DrawCommandKind.Text, [0, 13, 12], "#FFFFFF", "Ab", null)
        })
        {
            using var bitmap = Decode(renderer.Render(Drawing(command)));
            Check(bitmap.Pixels.Any(pixel => pixel.Alpha != 0), "Drawing kind produces pixels: " + command.Kind);
        }
        var fractional = renderer.Render(Drawing() with { Width = 1.1, Height = 2.2 });
        Check(fractional.Width == 2 && fractional.Height == 3);
        foreach (double dimension in new[] { 0, -1, 2049, double.NaN, double.PositiveInfinity })
            Reject(() => renderer.Render(Drawing() with { Width = dimension }));
        foreach (var invalid in new[]
        {
            Draw(DrawCommandKind.Line, "#FFFFFF", 0, 0),
            Draw(DrawCommandKind.Path, "#FFFFFF", 1, 2, 3, 4, 5),
            Draw(DrawCommandKind.Rectangle, "red", 0, 0, 1, 1),
            Draw(DrawCommandKind.Rectangle, "#GGGGGG", 0, 0, 1, 1),
            Draw(DrawCommandKind.Rectangle, "#FFFFFF", 0, 0, double.NaN, 1),
            Draw(DrawCommandKind.Rectangle, "#FFFFFF", 0, 0, 1000001, 1),
            Draw((DrawCommandKind)999, "#FFFFFF", 0, 0, 1, 1),
            Draw(DrawCommandKind.Image, "#FFFFFF", 0, 0, 1, 1),
            new DrawCommand(DrawCommandKind.Text, [1, 2, 12], "#FFFFFF", new string('x', 4097), null)
        }) Reject(() => renderer.Render(Drawing(invalid)));
        Reject(() => renderer.Render(Drawing(Enumerable.Repeat(Draw(DrawCommandKind.Clip, "#000000", 0, 0, 16, 16), 1025).ToArray())));
        Reject(() => renderer.Render(Drawing(Enumerable.Repeat(Draw(DrawCommandKind.Transform, "#000000", 1000000, 0, 0, 1000000, 0, 0), 8).ToArray())));
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel(); bool stopped = false;
            try { renderer.Render(Drawing(), cancelled.Token); } catch (OperationCanceledException) { stopped = true; }
            Check(stopped);
        }

        byte[] png = Png(2, 2, SKColors.Green);
        var blob = new BlobRef(Guid.NewGuid(), owner.LoadInstanceId, png.Length, "test");
        DrawCommand image = new(DrawCommandKind.Image, [0, 0, 16, 16], "#FFFFFF", null, blob);
        List<byte[]> copies = []; int reads = 0;
        var withImages = new PluginDrawingRenderer((identity, reference) =>
        { Check(identity == owner && reference == blob); reads++; byte[] copy = png.ToArray(); copies.Add(copy); return copy; });
        using (var bitmap = Decode(withImages.Render(Drawing(image, image)))) Check(bitmap.GetPixel(8, 8) == SKColors.Green);
        Check(reads == 1 && copies.Single().All(value => value == 0));
        // Cache lifetime is one render; a retired blob must be read and rejected on the next render.
        withImages.Render(Drawing(image)); Check(reads == 2 && copies.All(copy => copy.All(value => value == 0)));
        foreach (byte[] invalid in new[] { new byte[] { 1, 2, 3 }, png[..40], Png(4097, 1, SKColors.Red), Png(2049, 2048, SKColors.Red) })
        {
            byte[] copy = invalid.ToArray();
            Reject(() => new PluginDrawingRenderer((_, _) => copy).Render(Drawing(image)));
            Check(copy.All(value => value == 0));
        }
        using (var cancelled = new CancellationTokenSource())
        {
            byte[] copy = png.ToArray(); bool stopped = false;
            try { new PluginDrawingRenderer((_, _) => { cancelled.Cancel(); return copy; }).Render(Drawing(image), cancelled.Token); }
            catch (OperationCanceledException) { stopped = true; }
            Check(stopped && copy.All(value => value == 0));
        }
        // Two individually valid images exceed the cache together. Revisit the first
        // to verify eviction without rejecting an otherwise valid command sequence.
        byte[] large = Png(1500, 1500, SKColors.Blue); int largeReads = 0;
        var otherBlob = blob with { Token = Guid.NewGuid() };
        var eviction = new PluginDrawingRenderer((_, _) => { largeReads++; return large.ToArray(); });
        eviction.Render(Drawing(image, image with { Image = otherBlob }, image)); Check(largeReads == 3);

        string sample = Environment.GetEnvironmentVariable("NEXA_WASM_SAMPLE") ?? throw new InvalidOperationException("Guest fixture required.");
        string root = Path.Combine(Path.GetTempPath(), "sidecar-drawing-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var app = new PluginApplication(root, (wasm, _) => ValueTask.FromResult<IWasmPluginMachine>(new WasmPluginMachine(wasm.Span)), [],
                new Allow(), (_, _) => { }, (_, _) => { }, PluginViewCompiler.ValidatePage, PluginViewCompiler.ValidateComponent);
            using var package = VerifiedPluginPackage.Parse(await File.ReadAllBytesAsync(Path.Combine(Path.GetDirectoryName(sample)!, "desktop.zip")), "example.desktop", "1.0.0");
            var loaded = await app.LoadAsync(package, package.Manifest.RequestedPermissions, default); Check(loaded.State == LoadState.Active);
            var owned = app.AddBlob(loaded.Identity, png);
            var drawing = Drawing(image with { Image = owned }) with { Owner = loaded.Identity };
            var ownedRenderer = new PluginDrawingRenderer(app.CopyBlob);
            ownedRenderer.Render(drawing);
            Reject(() => ownedRenderer.Render(drawing with { Owner = loaded.Identity with { LoadInstanceId = Guid.NewGuid() } }));
            app.ReleaseBlob(loaded.Identity, owned); Reject(() => ownedRenderer.Render(drawing));
            owned = app.AddBlob(loaded.Identity, png); drawing = drawing with { Commands = [image with { Image = owned }] };
            await app.StopAsync(loaded.Identity.PluginId); Reject(() => ownedRenderer.Render(drawing));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        Console.WriteLine("PASS: Sidecar raster command order, clipping, transforms, alpha, text/path/image pixels, decode budgets, cache eviction, blob clearing and real load retirement.");

        static SKBitmap Decode(PluginRasterImage image)
        {
            var bitmap = SKBitmap.Decode(image.Png.ToArray()) ?? throw new InvalidDataException("Invalid raster PNG.");
            Check(bitmap.Width == image.Width && bitmap.Height == image.Height); return bitmap;
        }
        static byte[] Png(int width, int height, SKColor color)
        {
            using var bitmap = new SKBitmap(width, height); using var canvas = new SKCanvas(bitmap); canvas.Clear(color);
            using var image = SKImage.FromBitmap(bitmap); using var data = image.Encode(SKEncodedImageFormat.Png, 100); return data.ToArray();
        }
    }
}
