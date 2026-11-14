using System.IO.Compression;
using System.Text;

namespace Masterly.NightShift;

/// <summary>
/// Compresses the stored arguments of jobs, turned on with <c>UseArgumentCompression</c>: arguments longer than
/// <see cref="MinLength"/> characters are stored as <c>gz:</c> followed by their GZip-compressed UTF-8 bytes in base64.
/// Compressed arguments are always read back, whether compression is on or not.
/// </summary>
/// <remarks>
/// Servers of earlier versions can't read compressed arguments, so they are written only at the newest data
/// compatibility level (<see cref="RequiredLevel"/>): a lower level says older servers still share the storage.
/// </remarks>
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

    /// <summary>The data compatibility level from which arguments are compressed.</summary>
    public const CompatibilityLevel RequiredLevel = CompatibilityLevel.Version_180;

    /// <summary>
    /// Compresses serialized arguments when compression is on, the data compatibility level is at least
    /// <see cref="RequiredLevel"/>, and they are long enough.
    /// </summary>
    public static string Compress(string arguments)
    {
        if (MinLength is not { } minLength ||
            arguments.Length < minLength ||
            !GlobalConfiguration.HasCompatibilityLevel(RequiredLevel) ||
            arguments.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return arguments;
        }

        using MemoryStream output = new();
        using (GZipStream gzip = new(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(Encoding.UTF8.GetBytes(arguments));
        }

        string compressed = Prefix + Convert.ToBase64String(output.GetBuffer(), 0, (int)output.Length);

        // Arguments that don't shrink (already compressed data, short random text) are kept as they are
        return compressed.Length < arguments.Length ? compressed : arguments;
    }

    /// <summary>
    /// The longest text compressed arguments are read back as: 64 MB of characters. A few kilobytes of
    /// <c>gz:</c> data can expand much further, and running out of memory can't be caught where the job is loaded,
    /// so longer arguments throw <see cref="InvalidDataException"/> instead, which fails the job's load.
    /// </summary>
    public const int MaxDecompressedLength = 64 * 1024 * 1024;

    /// <summary>Reads arguments back, compressed or not.</summary>
    /// <exception cref="InvalidDataException">The arguments expand past <see cref="MaxDecompressedLength"/> characters.</exception>
    [return: NotNullIfNotNull(nameof(arguments))]
    public static string? Decompress(string? arguments) => Decompress(arguments, MaxDecompressedLength);

    /// <summary>Reads arguments back, compressed or not, refusing compressed ones longer than <paramref name="maxLength"/> characters.</summary>
    /// <exception cref="InvalidDataException">The arguments expand past <paramref name="maxLength"/> characters.</exception>
    [return: NotNullIfNotNull(nameof(arguments))]
    internal static string? Decompress(string? arguments, int maxLength)
    {
        if (arguments == null || !arguments.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return arguments;
        }

        byte[] bytes = Convert.FromBase64String(arguments[Prefix.Length..]);
        using GZipStream gzip = new(new MemoryStream(bytes), CompressionMode.Decompress);
        using StreamReader reader = new(gzip, Encoding.UTF8);

        StringBuilder text = new();
        char[] buffer = new char[16 * 1024];
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (read > maxLength - text.Length)
            {
                throw new InvalidDataException(
                    $"The compressed arguments expand past {maxLength} characters, the longest text they are read back as.");
            }

            text.Append(buffer, 0, read);
        }

        return text.ToString();
    }
}
