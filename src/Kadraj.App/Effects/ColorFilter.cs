namespace Kadraj.Effects;

/// <summary>
/// A colour look baked into per-channel lookup tables plus a saturation factor,
/// so applying it costs three table reads and a little arithmetic per pixel.
/// </summary>
public sealed class ColorFilter
{
    public byte[] R { get; } = new byte[256];
    public byte[] G { get; } = new byte[256];
    public byte[] B { get; } = new byte[256];

    /// <summary>Saturation × 256 (256 = unchanged, 0 = greyscale).</summary>
    public int Saturation { get; }
    public bool IsIdentity { get; }

    public static readonly (string Id, string En, string Tr)[] Presets =
    [
        ("normal", "Normal", "Normal"),
        ("vivid", "Vivid", "Canlı"),
        ("warm", "Warm", "Sıcak"),
        ("cool", "Cool", "Soğuk"),
        ("cinematic", "Cinematic", "Sinematik"),
        ("vintage", "Vintage", "Vintage"),
        ("mono", "B&W", "S/B"),
        ("noir", "Noir", "Noir"),
    ];

    ColorFilter(Func<double, double> r, Func<double, double> g, Func<double, double> b, double saturation, EffectSettings s)
    {
        double brightness = s.Brightness * 0.25;
        double contrast = 1 + s.Contrast * 0.6;
        double Adjust(double v) => Math.Clamp((v - 0.5) * contrast + 0.5 + brightness, 0, 1);

        bool identity = true;
        for (int i = 0; i < 256; i++)
        {
            double x = i / 255.0;
            R[i] = ToByte(Adjust(r(x)));
            G[i] = ToByte(Adjust(g(x)));
            B[i] = ToByte(Adjust(b(x)));
            identity &= R[i] == i && G[i] == i && B[i] == i;
        }

        Saturation = (int)Math.Round(Math.Clamp(saturation * (1 + s.Saturation), 0, 3) * 256);
        IsIdentity = identity && Saturation == 256;
    }

    public static ColorFilter Create(EffectSettings s)
    {
        static double Id(double x) => x;
        // Blend towards a smoothstep S-curve: more contrast, soft ends.
        static double S(double x, double amount) => x + (x * x * (3 - 2 * x) - x) * amount;

        return s.Filter switch
        {
            "vivid" => new(x => S(x, 0.35), x => S(x, 0.35), x => S(x, 0.35), 1.35, s),
            "warm" => new(x => x * 1.06 + 0.02, x => x * 1.01, x => x * 0.88, 1.05, s),
            "cool" => new(x => x * 0.9, x => x * 0.98 + 0.01, x => x * 1.06 + 0.03, 0.95, s),
            // Teal shadows, warm highlights
            "cinematic" => new(
                x => S(x, 0.4) + 0.08 * (S(x, 0.4) - 0.5),
                x => S(x, 0.4) + 0.02 * (S(x, 0.4) - 0.5),
                x => S(x, 0.4) - 0.08 * (S(x, 0.4) - 0.5) + 0.02,
                0.85, s),
            // Faded blacks, soft highlights, warm paper tone
            "vintage" => new(
                x => (0.08 + x * 0.84) * 1.04 + 0.02,
                x => 0.08 + x * 0.84,
                x => (0.08 + x * 0.84) * 0.88 + 0.04,
                0.7, s),
            "mono" => new(Id, Id, Id, 0, s),
            "noir" => new(x => S(x, 0.9), x => S(x, 0.9), x => S(x, 0.9), 0, s),
            _ => new(Id, Id, Id, 1, s),
        };
    }

    static byte ToByte(double v) => (byte)Math.Clamp((int)Math.Round(v * 255), 0, 255);
}
