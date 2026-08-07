using System.Reflection;

namespace SAFT.App;

/// <summary>
/// Loads the branding images baked into the exe as embedded resources (see the
/// &lt;EmbeddedResource&gt; items in SAFT.App.csproj) — keeps the published exe a genuinely
/// standalone single file, with no loose image folder needed next to it at runtime.
/// </summary>
internal static class EmbeddedImages
{
    /// <summary>
    /// Loads an embedded image, or a plain placeholder if decoding fails for any reason. A GDI+
    /// codec quirk under an unusual runtime (Wine/Winlator being the known-risky one here) failing
    /// to decode ONE image must never crash the whole app before any window is even shown — that's
    /// a much worse failure mode than one image looking wrong.
    /// </summary>
    public static Image Load(string logicalName)
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream(logicalName)
                ?? throw new InvalidOperationException($"Embedded image '{logicalName}' was not found.");
            // Image.FromStream can lazily read from the stream later (e.g. on first paint), so it isn't
            // safe to use once the stream is disposed — copying into an independent Bitmap here avoids
            // an "invalid parameter"/GDI+ error the first time the image actually gets drawn.
            using var streamed = Image.FromStream(stream);
            return new Bitmap(streamed);
        }
        catch
        {
            return CreatePlaceholder();
        }
    }

    private static Bitmap CreatePlaceholder()
    {
        var bitmap = new Bitmap(64, 64);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.Gainsboro);
        return bitmap;
    }
}
