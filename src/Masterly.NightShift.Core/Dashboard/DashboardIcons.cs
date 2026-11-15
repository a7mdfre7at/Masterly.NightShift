using System.Collections.Frozen;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Masterly.NightShift.Dashboard;

/// <summary>
/// The dashboard's icons: a sprite of Bootstrap Icons (<c>Content/icons.svg</c>, one <c>&lt;symbol id="bi-name"&gt;</c>
/// per icon the dashboard uses), served at <see cref="Path"/> and referenced with <c>&lt;use href&gt;</c>.
/// </summary>
internal static partial class DashboardIcons
{
    internal const string ResourceFileName = "icons.svg";

    // The icon of a menu item that has none, or one the sprite doesn't hold
    internal const string DefaultMenuIcon = "chevron-right";

    private static readonly Lazy<FrozenSet<string>> SpriteNames = new(ReadSpriteNames);

    /// <summary>
    /// The Glyphicon names the dashboard used before Bootstrap 5, with the Bootstrap Icon that replaced each, so a
    /// <see cref="MenuItem.Icon"/> written for an earlier version still shows an icon.
    /// </summary>
    internal static readonly FrozenDictionary<string, string> GlyphiconNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["adjust"] = "circle-half",
        ["alert"] = "exclamation-triangle-fill",
        ["align-left"] = "text-left",
        ["blackboard"] = "easel-fill",
        ["book"] = "book-fill",
        ["calendar"] = "calendar3",
        ["certificate"] = "sun-fill",
        ["chevron-right"] = "chevron-right",
        ["comment"] = "chat-left-text-fill",
        ["console"] = "terminal-fill",
        ["copy"] = "copy",
        ["download-alt"] = "download",
        ["exclamation-sign"] = "exclamation-circle-fill",
        ["eye-open"] = "eye-fill",
        ["fire"] = "fire",
        ["flag"] = "flag-fill",
        ["flash"] = "lightning-fill",
        ["folder-open"] = "folder2-open",
        ["fullscreen"] = "arrows-fullscreen",
        ["hdd"] = "hdd-fill",
        ["home"] = "house-door-fill",
        ["info-sign"] = "info-circle-fill",
        ["link"] = "link-45deg",
        ["list-alt"] = "card-list",
        ["log-out"] = "box-arrow-left",
        ["menu-hamburger"] = "list",
        ["ok"] = "check-lg",
        ["ok-circle"] = "check-circle",
        ["pause"] = "pause-fill",
        ["pencil"] = "pencil-fill",
        ["play"] = "play-fill",
        ["play-circle"] = "play-circle",
        ["plus"] = "plus-lg",
        ["question-sign"] = "question-circle-fill",
        ["random"] = "shuffle",
        ["refresh"] = "arrow-clockwise",
        ["remove"] = "x-lg",
        ["remove-sign"] = "x-circle-fill",
        ["repeat"] = "arrow-repeat",
        ["resize-horizontal"] = "arrows",
        ["save"] = "box-arrow-down",
        ["search"] = "search",
        ["star-empty"] = "moon-stars",
        ["stats"] = "bar-chart-fill",
        ["tag"] = "tag-fill",
        ["tasks"] = "list-task",
        ["th-large"] = "grid-fill",
        ["time"] = "clock",
        ["trash"] = "trash-fill",
        ["upload"] = "upload",
        ["warning-sign"] = "exclamation-triangle-fill",
        ["zoom-in"] = "zoom-in",
        ["zoom-out"] = "zoom-out",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// The path of the sprite, with a number that changes with every build of this assembly, so browsers that
    /// cached it for a year fetch it again after an upgrade.
    /// </summary>
    internal static string Path { get; } = $"/icons{DashboardRoutes.CombineResourceHash(DashboardRoutes.InitialResourceHash, typeof(DashboardIcons).Assembly.ManifestModule.ModuleVersionId, ResourceName)}";

    internal static string ResourceName => DashboardRoutes.GetContentResourceName("icons", ResourceFileName);

    /// <summary>The names of the icons in the sprite, without the <c>bi-</c> prefix.</summary>
    internal static IReadOnlySet<string> Names => SpriteNames.Value;

    /// <summary>
    /// The sprite's name for an icon: a Bootstrap Icons name the sprite holds, or a Glyphicon name it replaced;
    /// <c>null</c> for any other name.
    /// </summary>
    internal static string? Resolve(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        string trimmed = name.StartsWith("bi-", StringComparison.Ordinal) ? name[3..] : name;
        if (SpriteNames.Value.Contains(trimmed))
        {
            return trimmed;
        }

        string glyphicon = trimmed.StartsWith("glyphicon-", StringComparison.Ordinal) ? trimmed["glyphicon-".Length..] : trimmed;
        return GlyphiconNames.TryGetValue(glyphicon, out string? replacement) ? replacement : null;
    }

    private static FrozenSet<string> ReadSpriteNames()
    {
        Assembly assembly = typeof(DashboardIcons).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The dashboard's icon sprite '{ResourceName}' is missing from {assembly.GetName().Name}.");
        using StreamReader reader = new(stream);
        string sprite = reader.ReadToEnd();

        return SymbolId().Matches(sprite).Select(static x => x.Groups[1].Value).ToFrozenSet(StringComparer.Ordinal);
    }

    [GeneratedRegex("<symbol id=\"bi-([a-z0-9-]+)\"", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SymbolId();
}
