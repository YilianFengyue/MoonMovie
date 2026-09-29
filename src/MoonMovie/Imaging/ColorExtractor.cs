using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI;

namespace MoonMovie.Imaging;

/// <summary>Cheap colour analysis on a thumbnail-sized decode of a cached image file.</summary>
public static class ColorExtractor
{
    /// <summary>
    /// A deep, low-luminance tone of the image's dominant hue, suitable for tinting a dark canvas.
    /// </summary>
    public static async Task<Color?> AmbientToneAsync(string file)
    {
        var pixels = await DecodeAsync(file, 48, 27);
        if (pixels is null)
        {
            return null;
        }

        // Hue histogram weighted by saturation and mid-luminance, so skies and skin tones beat black bars.
        const int bins = 18;
        var weight = new double[bins];
        var sumR = new double[bins];
        var sumG = new double[bins];
        var sumB = new double[bins];

        for (var i = 0; i < pixels.Length; i += 4)
        {
            double b = pixels[i] / 255.0, g = pixels[i + 1] / 255.0, r = pixels[i + 2] / 255.0;
            var (h, s, l) = ToHsl(r, g, b);
            var w = s * (1 - Math.Abs(l - 0.5) * 2) + 0.02;
            var bin = (int)(h / 360.0 * bins) % bins;
            weight[bin] += w;
            sumR[bin] += r * w;
            sumG[bin] += g * w;
            sumB[bin] += b * w;
        }

        var best = Array.IndexOf(weight, weight.Max());
        if (weight[best] <= 0)
        {
            return null;
        }

        var (hue, sat, _) = ToHsl(sumR[best] / weight[best], sumG[best] / weight[best], sumB[best] / weight[best]);
        return FromHsl(hue, Math.Min(sat, 0.55) * 0.9, 0.17);
    }

    /// <summary>Alpha-weighted mean luminance (0..1); used to reject dark title logos on dark art.</summary>
    public static async Task<double?> OpaqueLuminanceAsync(string file)
    {
        var pixels = await DecodeAsync(file, 64, 24);
        if (pixels is null)
        {
            return null;
        }

        double lum = 0, alpha = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var a = pixels[i + 3] / 255.0;
            if (a < 0.2)
            {
                continue;
            }

            // Straight (non-premultiplied) channels.
            lum += (0.0722 * pixels[i] + 0.7152 * pixels[i + 1] + 0.2126 * pixels[i + 2]) / 255.0 * a;
            alpha += a;
        }

        return alpha < 1 ? null : lum / alpha;
    }

    private static async Task<byte[]?> DecodeAsync(string file, uint width, uint height)
    {
        try
        {
            using var stream = File.OpenRead(file);
            using var ras = stream.AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(ras);
            var data = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Straight,
                new BitmapTransform
                {
                    ScaledWidth = width,
                    ScaledHeight = height,
                    InterpolationMode = BitmapInterpolationMode.Linear,
                },
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage);
            return data.DetachPixelData();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static (double H, double S, double L) ToHsl(double r, double g, double b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        if (max - min < 1e-6)
        {
            return (0, 0, l);
        }

        var d = max - min;
        var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h;
        if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
        else if (max == g) h = (b - r) / d + 2;
        else h = (r - g) / d + 4;
        return (h * 60, s, l);
    }

    private static Color FromHsl(double h, double s, double l)
    {
        double Hue(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 0.5) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }

        var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var p = 2 * l - q;
        var hk = h / 360;
        return Color.FromArgb(255,
            (byte)Math.Round(Hue(p, q, hk + 1.0 / 3) * 255),
            (byte)Math.Round(Hue(p, q, hk) * 255),
            (byte)Math.Round(Hue(p, q, hk - 1.0 / 3) * 255));
    }
}
