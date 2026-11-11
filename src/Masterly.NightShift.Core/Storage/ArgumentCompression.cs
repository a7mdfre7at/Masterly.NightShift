using System.IO.Compression;
using System.Text;

namespace Masterly.NightShift;

/// <summary>
/// Compresses the stored arguments of jobs, turned on with <c>UseArgumentCompression</c>: arguments longer than
/// <see cref="MinLength"/> characters are stored as <c>gz:</c> followed by their GZip-compressed UTF-8 bytes in base64.
/// Compressed arguments are always read back, whether compression is on or not.
/// </summary>
internal static class ArgumentCompression
{
    /// <summary>The marker of compressed arguments; plain ones are a JSON array, which starts with <c>[</c>.</summary>
    public const string Prefix = "gz:";

    /// <summary>The length from which arguments are compressed, or <c>null</c> when compression is off.</summary>
    public static int? MinLength
    {
        get;
        set => field = value is < 1 ? throw new ArgumentOutOfRangeException(nameof(value), value, "The length must be at least 1.") : value;
    }

    /// <summary>Compresses serialized arguments when compression is on and they are long enough.</summary>
    public static string Compress(string arguments)
    {
        if (MinLength is not { } minLength || arguments.Length < minLength || arguments.StartsWith(Prefix, StringComparison.Ordinal)) return arguments;

        using MemoryStream output = new();
        using (GZipStream gzip = new(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(Encoding.UTF8.GetBytes(arguments));
        }

        string compressed = Prefix + Convert.ToBase64String(output.GetBuffer(), 0, (int)output.Length);

        // Arguments that don't shrink (already compressed data, short random text) are kept as they are
        return compressed.Length < arguments.Length ? compressed : arguments;
    }

    /// <summary>Reads arguments back, compressed or not.</summary>
    [return: NotNullIfNotNull(nameof(arguments))]
    public static string? Decompress(string? arguments)
    {
        if (arguments == null || !arguments.StartsWith(Prefix, StringComparison.Ordinal)) return arguments;

        byte[] bytes = Convert.FromBase64String(arguments[Prefix.Length..]);
        using GZipStream gzip = new(new MemoryStream(bytes), CompressionMode.Decompress);
        using StreamReader reader = new(gzip, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
