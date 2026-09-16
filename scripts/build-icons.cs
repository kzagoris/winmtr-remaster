#:package Svg.Skia@3.0.4
#:property PublishAot=false

// Builds the Route Pulse Windows and macOS icons for the Avalonia application.
//
// Run it from the repository root:
//
//     dotnet run scripts/build-icons.cs
//     dotnet run scripts/build-icons.cs -- --dump-svg build/icon-svg
//     dotnet run scripts/build-icons.cs -- --refresh-ico
//
// Existing ICO files are preserved by default: Skia rasterisation can differ
// between hosts. Use --refresh-ico only for an intentional Windows icon update.
// Missing ICO files and the ICNS file are generated on every run.
//
// It writes these four files:
//
//     src/WinMtr.Desktop/Assets/winmtr-route-pulse-light.ico
//     src/WinMtr.Desktop/Assets/winmtr-route-pulse-dark.ico
//     src/WinMtr.Desktop/Assets/winmtr-route-pulse.ico      (executable icon,
//                                                             a copy of light)
//     src/WinMtr.Desktop/Assets/winmtr-route-pulse.icns     (macOS, light artwork)
//
// Why this script exists
// ----------------------
// The icons were hand-made once and could not be made again. The small frames
// also had no antialiasing, and the artwork changed proportion between 64 px
// and 128 px because two different hand-drawn masters were used. This script
// is now the only source of the icons. The hand-drawn masters are kept as a
// design record in docs/design/route-pulse/.
//
// How it makes a frame
// --------------------
// 1. It writes one SVG for the wanted size. The geometry comes from the
//    constants below, which are the 1024 unit space of the detailed master.
// 2. Every weight ramps on a log scale from the optical value at 16 px to the
//    true value at 128 px. Small icons need bolder weights than large ones, so
//    a single set of proportions cannot serve both. The ramp is smooth, so the
//    mark keeps one silhouette at every size.
// 3. Endpoint circles and the probe snap to the pixel grid, so their edges land
//    on whole pixels.
// 4. Detail is removed, not shrunk. Below about 1.5 px a shape stops being
//    small and becomes fog. The inner channel and the probe halo start at
//    24 px. The motion trail starts at 48 px.
// 5. The frame renders at four times the wanted size and then goes down with a
//    cubic filter. The alpha channel then goes through an S-curve, which pushes
//    partial coverage toward clear or solid. Edges become crisp and curves stay
//    smooth. Without this step small frames look muddy; without antialiasing
//    at all they look blocky, which was the earlier defect.
// 6. Frames go into the ICO at 32 bits per pixel with straight alpha. Windows
//    picks the frame itself: Avalonia gives the raw ICO bytes to
//    CreateIconFromResourceEx, so every frame here is really used.
// 7. macOS gets a separate ICNS container of PNG frames, including Retina sizes.

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using SkiaSharp;
using Svg.Skia;

const int Supersample = 4;
const double AlphaCurve = 2.2;   // S-curve strength for coverage sharpening
const int PngFrameFrom = 128;    // frames this size and larger are PNG-compressed

int[] icoSizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
// Keep these separate: ICO dimensions cannot exceed 256. The ic11–ic14
// chunks are Retina representations of 16, 32, 128 and 256 logical pixels;
// ic10 supplies 512 logical pixels at 2x.
(string Type, int Size)[] icnsSizes =
[
    ("icp4", 16), ("icp5", 32), ("icp6", 64),
    ("ic07", 128), ("ic08", 256), ("ic09", 512), ("ic10", 1024),
    ("ic11", 32), ("ic12", 64), ("ic13", 256), ("ic14", 512),
];

var repoRoot = FindRepoRoot();
var assets = Path.Combine(repoRoot, "src", "WinMtr.Desktop", "Assets");

string? dumpSvgDir = null;
var refreshIco = false;
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--dump-svg" && i + 1 < args.Length)
    {
        dumpSvgDir = Path.GetFullPath(args[++i]);
    }
    else if (args[i] == "--refresh-ico")
    {
        refreshIco = true;
    }
}

foreach (var theme in Theme.All)
{
    var frames = new List<(int Size, byte[] Rgba)>();
    foreach (var size in icoSizes)
    {
        var svg = BuildSvg(size, theme);
        if (dumpSvgDir is not null)
        {
            var dir = Path.Combine(dumpSvgDir, theme.Name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, $"{size}.svg"), svg, new UTF8Encoding(false));
        }

        frames.Add((size, Rasterise(svg, size)));
    }

    var ico = BuildIco(frames);
    var path = Path.Combine(assets, $"winmtr-route-pulse-{theme.Name}.ico");
    WriteIco(path, ico, frames.Count, refreshIco);

    if (theme.Name == "light")
    {
        // The executable icon must stay stable, so it follows the light theme.
        var stable = Path.Combine(assets, "winmtr-route-pulse.ico");
        WriteIco(stable, File.ReadAllBytes(path), frames.Count, refreshIco);

        var pngFrames = new Dictionary<int, byte[]>();
        foreach (var size in icnsSizes.Select(frame => frame.Size).Distinct())
        {
            var svg = BuildSvg(size, theme);
            if (dumpSvgDir is not null)
            {
                File.WriteAllText(Path.Combine(dumpSvgDir, theme.Name, $"{size}.svg"), svg, new UTF8Encoding(false));
            }

            pngFrames.Add(size, EncodePng(size, Rasterise(svg, size)));
        }

        var icns = BuildIcns(icnsSizes.Select(frame => (frame.Type, pngFrames[frame.Size])).ToList());
        var macIcon = Path.Combine(assets, "winmtr-route-pulse.icns");
        File.WriteAllBytes(macIcon, icns);
        Console.WriteLine($"{Path.GetFileName(macIcon),-34} {icns.Length,8:N0} bytes  {icnsSizes.Length} frames");
    }
}

// ---------------------------------------------------------------- geometry --

static string BuildSvg(int size, Theme theme)
{
    var pad = Geometry.Padding(size);
    var s = (1 - 2 * pad) * size / Geometry.BoundingBoxSpan;
    var half = size / 2.0;
    var t = Geometry.Ramp(size);

    double Tx(double x) => (x - Geometry.BoundingBoxCentreX) * s + half;
    double Ty(double y) => (y - Geometry.BoundingBoxCentreY) * s + half;

    // Blend the optical small-size weight into the true weight.
    double Weight(double smallFraction, double trueUnits)
        => smallFraction * size * (1 - t) + trueUnits * s * t;

    var casing = Math.Max(Weight(Geometry.SmallCasing, Geometry.CasingWidth), Geometry.MinCasing);
    var nodeR = Weight(Geometry.SmallNodeRadius, Geometry.NodeOuterRadius);
    var holeR = Math.Min(
        Math.Max(Weight(Geometry.SmallHoleRadius, Geometry.NodeInnerRadius), 1.0),
        nodeR - Geometry.MinRing);
    var probeR = Weight(Geometry.SmallProbeRadius, Geometry.ProbeRadius);
    var haloR = Math.Max(Weight(Geometry.SmallHaloRadius, Geometry.HaloRadius), probeR + 0.75);
    var channel = Math.Max(Weight(Geometry.SmallChannel, Geometry.ChannelWidth), Geometry.MinChannel);

    var wantChannel = size >= 24;
    var wantHalo = size >= 24;
    var wantTrail = size >= 48;

    // Snap the endpoints so both edges of every circle land on whole pixels.
    Span<(double X, double Y, double R)> nodes = stackalloc (double, double, double)[3];
    var (apexX, apexR) = (half, SnapRadius(nodeR));
    var (apexY, _) = SnapCircle(Ty(Geometry.Nodes[0].Y), nodeR);
    nodes[0] = (apexX, apexY, apexR);

    var (leftX, leftR) = SnapCircle(Tx(Geometry.Nodes[1].X), nodeR);
    var (leftY, _) = SnapCircle(Ty(Geometry.Nodes[1].Y), nodeR);
    nodes[1] = (leftX, leftY, leftR);
    nodes[2] = (size - leftX, leftY, leftR);   // mirrored, so the base stays symmetric

    var hole = SnapRadius(holeR);

    var sb = new StringBuilder();
    sb.Append(CultureInfo.InvariantCulture,
        $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{size}\" height=\"{size}\" viewBox=\"0 0 {size} {size}\">");

    void Routes(string colour, double width)
    {
        sb.Append(CultureInfo.InvariantCulture,
            $"<g fill=\"none\" stroke=\"{colour}\" stroke-linecap=\"round\" stroke-linejoin=\"round\" stroke-width=\"{width:F4}\">");
        foreach (var (a, b) in Geometry.Routes)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"<path d=\"M{Tx(a.X):F4} {Ty(a.Y):F4} {Tx(b.X):F4} {Ty(b.Y):F4}\"/>");
        }

        sb.Append("</g>");
    }

    Routes(theme.Structure, casing);
    if (wantChannel)
    {
        Routes(theme.Interior, channel);
    }

    sb.Append(CultureInfo.InvariantCulture, $"<g fill=\"{theme.Structure}\">");
    foreach (var (x, y, r) in nodes)
    {
        sb.Append(CultureInfo.InvariantCulture, $"<circle cx=\"{x:F4}\" cy=\"{y:F4}\" r=\"{r:F4}\"/>");
    }

    sb.Append("</g>");

    sb.Append(CultureInfo.InvariantCulture, $"<g fill=\"{theme.Interior}\">");
    foreach (var (x, y, _) in nodes)
    {
        sb.Append(CultureInfo.InvariantCulture, $"<circle cx=\"{x:F4}\" cy=\"{y:F4}\" r=\"{hole:F4}\"/>");
    }

    sb.Append("</g>");

    if (wantTrail)
    {
        var trailWidth = Math.Max(Geometry.TrailWidth * s, Geometry.MinTrail);
        sb.Append(CultureInfo.InvariantCulture,
            $"<g fill=\"none\" stroke=\"{theme.Probe}\" stroke-linecap=\"round\" stroke-width=\"{trailWidth:F4}\">");
        foreach (var (a, b) in Geometry.Trail)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"<path d=\"M{Tx(a.X):F4} {Ty(a.Y):F4} {Tx(b.X):F4} {Ty(b.Y):F4}\"/>");
        }

        sb.Append("</g>");
    }

    var (probeX, snappedProbeR) = SnapCircle(Tx(Geometry.Probe.X), probeR);
    var (probeY, _) = SnapCircle(Ty(Geometry.Probe.Y), probeR);
    if (wantHalo)
    {
        sb.Append(CultureInfo.InvariantCulture,
            $"<circle cx=\"{probeX:F4}\" cy=\"{probeY:F4}\" r=\"{haloR:F4}\" fill=\"{theme.Interior}\"/>");
    }

    sb.Append(CultureInfo.InvariantCulture,
        $"<circle cx=\"{probeX:F4}\" cy=\"{probeY:F4}\" r=\"{snappedProbeR:F4}\" fill=\"{theme.Probe}\"/>");
    sb.Append("</svg>");
    return sb.ToString();
}

static double SnapRadius(double r) => Math.Max(1.0, Math.Round(2 * r)) / 2;

static (double Centre, double Radius) SnapCircle(double centre, double radius)
{
    var r = SnapRadius(radius);
    return (Math.Round(centre - r) + r, r);
}

// --------------------------------------------------------------- rendering --

// Returns straight-alpha RGBA, top row first.
static byte[] Rasterise(string svg, int size)
{
    using var document = new SKSvg();
    using var picture = document.FromSvg(svg)
        ?? throw new InvalidOperationException($"Svg.Skia could not read the {size} px frame.");

    var big = size * Supersample;
    var info = new SKImageInfo(big, big, SKColorType.Rgba8888, SKAlphaType.Premul);
    using var surface = SKSurface.Create(info);
    surface.Canvas.Clear(SKColors.Transparent);
    surface.Canvas.Scale(big / picture.CullRect.Width, big / picture.CullRect.Height);
    surface.Canvas.DrawPicture(picture);
    surface.Canvas.Flush();

    using var full = SKBitmap.FromImage(surface.Snapshot());

    // Downsample while the pixels are premultiplied, so transparent pixels
    // cannot bleed their colour into the edges.
    var target = new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
    using var small = full.Resize(target, new SKSamplingOptions(SKCubicResampler.Mitchell))
        ?? throw new InvalidOperationException($"Could not resize the {size} px frame.");

    // Unpremultiply, because an ICO frame stores straight alpha.
    using var straight = new SKBitmap(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Unpremul));
    if (!small.PeekPixels().ReadPixels(straight.PeekPixels()))
    {
        throw new InvalidOperationException($"Could not unpremultiply the {size} px frame.");
    }

    var curve = new byte[256];
    for (var i = 0; i < 256; i++)
    {
        var x = i / 255.0;
        var y = x is <= 0 or >= 1
            ? x
            : Math.Pow(x, AlphaCurve) / (Math.Pow(x, AlphaCurve) + Math.Pow(1 - x, AlphaCurve));
        curve[i] = (byte)Math.Clamp(Math.Round(y * 255), 0, 255);
    }

    var pixels = straight.Bytes;
    for (var i = 3; i < pixels.Length; i += 4)
    {
        pixels[i] = curve[pixels[i]];
    }

    return pixels;
}

// ------------------------------------------------------------ ICO assembly --

static void WriteIco(string path, byte[] ico, int frameCount, bool refresh)
{
    if (File.Exists(path) && !refresh)
    {
        Console.WriteLine($"{Path.GetFileName(path),-34} preserved (use --refresh-ico to replace)");
        return;
    }

    File.WriteAllBytes(path, ico);
    Console.WriteLine($"{Path.GetFileName(path),-34} {ico.Length,8:N0} bytes  {frameCount} frames");
}

static byte[] BuildIco(List<(int Size, byte[] Rgba)> frames)
{
    var blobs = frames
        .Select(f => f.Size >= PngFrameFrom ? EncodePng(f.Size, f.Rgba) : EncodeBmp(f.Size, f.Rgba))
        .ToList();

    var directory = 6 + 16 * frames.Count;
    using var ms = new MemoryStream();
    using var w = new BinaryWriter(ms);

    w.Write((ushort)0);                  // reserved
    w.Write((ushort)1);                  // type: icon
    w.Write((ushort)frames.Count);

    var offset = directory;
    for (var i = 0; i < frames.Count; i++)
    {
        var size = frames[i].Size;
        w.Write((byte)(size >= 256 ? 0 : size));   // 0 means 256
        w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)0);                // colours in palette: none, it is true colour
        w.Write((byte)0);                // reserved
        w.Write((ushort)1);              // colour planes
        w.Write((ushort)32);             // bits per pixel
        w.Write(blobs[i].Length);
        w.Write(offset);
        offset += blobs[i].Length;
    }

    foreach (var blob in blobs)
    {
        w.Write(blob);
    }

    w.Flush();
    return ms.ToArray();
}

// ----------------------------------------------------------- ICNS assembly --

static byte[] BuildIcns(List<(string Type, byte[] Png)> frames)
{
    // ICNS lengths are big-endian and include their own eight-byte header.
    var length = 8 + frames.Sum(frame => 8 + frame.Png.Length);
    var container = new byte[length];
    Encoding.ASCII.GetBytes("icns", container.AsSpan(0, 4));
    BinaryPrimitives.WriteInt32BigEndian(container.AsSpan(4, 4), length);

    var offset = 8;
    foreach (var (type, png) in frames)
    {
        var chunkLength = 8 + png.Length;
        Encoding.ASCII.GetBytes(type, container.AsSpan(offset, 4));
        BinaryPrimitives.WriteInt32BigEndian(container.AsSpan(offset + 4, 4), chunkLength);
        png.CopyTo(container, offset + 8);
        offset += chunkLength;
    }

    return container;
}

static byte[] EncodePng(int size, byte[] rgba)
{
    var info = new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Unpremul);
    using var bitmap = new SKBitmap(info);
    System.Runtime.InteropServices.Marshal.Copy(rgba, 0, bitmap.GetPixels(), rgba.Length);
    using var image = SKImage.FromBitmap(bitmap);
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    return data.ToArray();
}

// A BMP frame inside an ICO carries a doubled height, bottom-up rows, and an
// AND mask after the colour data. The mask stays empty because the alpha
// channel already describes the shape.
static byte[] EncodeBmp(int size, byte[] rgba)
{
    var maskStride = (size + 31) / 32 * 4;
    var body = new byte[40 + size * size * 4 + maskStride * size];

    BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(0), 40);          // header size
    BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(4), size);        // width
    BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(8), size * 2);    // height, doubled
    BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(12), 1);          // planes
    BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(14), 32);         // bits per pixel
    BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(20), size * size * 4);

    var at = 40;
    for (var y = size - 1; y >= 0; y--)          // bottom-up
    {
        var row = y * size * 4;
        for (var x = 0; x < size; x++)
        {
            var p = row + x * 4;
            body[at++] = rgba[p + 2];            // blue
            body[at++] = rgba[p + 1];            // green
            body[at++] = rgba[p + 0];            // red
            body[at++] = rgba[p + 3];            // alpha
        }
    }

    return body;
}

// ----------------------------------------------------------------- support --

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
    {
        dir = dir.Parent;
    }

    return dir?.FullName
        ?? throw new InvalidOperationException("Run this script from inside the repository.");
}

readonly record struct Point(double X, double Y);

sealed record Theme(string Name, string Structure, string Interior, string Probe)
{
    public static readonly Theme[] All =
    [
        new("light", "#082B57", "#F4F1E8", "#FF241F"),
        new("dark", "#E8EAED", "#202124", "#FF241F"),
    ];
}

static class Geometry
{
    // The detailed master lives in a 1024 unit square. See
    // docs/design/route-pulse/winmtr-route-pulse-light.svg.
    public static readonly Point[] Nodes = [new(512, 210), new(220, 792), new(804, 792)];
    public const double NodeOuterRadius = 142;
    public const double NodeInnerRadius = 76;

    public static readonly (Point A, Point B)[] Routes =
    [
        (new(438, 292), new(284, 650)),
        (new(586, 292), new(740, 650)),
    ];

    public const double CasingWidth = 104;
    public const double ChannelWidth = 30;

    public static readonly (Point A, Point B)[] Trail =
    [
        (new(613, 355), new(625, 382)),
        (new(641, 421), new(653, 448)),
    ];

    public const double TrailWidth = 30;
    public static readonly Point Probe = new(681, 512);
    public const double HaloRadius = 62;
    public const double ProbeRadius = 45;

    // The artwork bounding box in the same unit space.
    public const double BoundingBoxCentreX = 512;
    public const double BoundingBoxCentreY = 501;
    public const double BoundingBoxSpan = 868;

    // Optical weights at 16 px, as a fraction of the frame. They come from the
    // old small master, which carried the right weight; only its aliasing and
    // its abrupt handover to the detailed master were wrong.
    public const double SmallCasing = 0.150;
    public const double SmallNodeRadius = 0.15625;
    public const double SmallHoleRadius = 0.0625;
    public const double SmallProbeRadius = 0.084375;
    public const double SmallHaloRadius = 0.109375;
    public const double SmallChannel = 0.046875;

    public const double MinRing = 1.25;      // least readable endpoint ring
    public const double MinCasing = 2.0;     // least readable route weight
    public const double MinChannel = 1.0;
    public const double MinTrail = 1.0;

    // Padding also ramps, instead of jumping. Small frames need every pixel.
    public static double Padding(int size) => size switch
    {
        <= 24 => 0.0,
        <= 48 => 0.02,
        _ => 0.04,
    };

    // 0 at 16 px, 1 at 128 px and above, interpolated on a log scale.
    public static double Ramp(int size) => Math.Clamp((Math.Log2(size) - 4.0) / 3.0, 0.0, 1.0);
}
