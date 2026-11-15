using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using Masterly.NightShift.Dashboard.Resources;

namespace Masterly.NightShift.Core.Tests.Dashboard;

public class DashboardStringsFacts
{
    // Every locale the dashboard ships
    public static readonly TheoryData<string> Locales =
    [
        "ca", "de", "es", "fa", "fr", "nb", "nl", "pt-BR", "pt-PT", "pt", "ru", "sv", "tr-TR", "zh-TW", "zh",
    ];

    // The texts of the Material Design shell, dialogs, snackbars, pagination and command palette
    private static readonly string[] ShellKeys =
    [
        "LayoutPage_SkipToContent", "LayoutPage_Navigation", "LayoutPage_CloseMenu", "LayoutPage_Offline", "LayoutPage_Live_Hint",
        "LayoutPage_Offline_Hint", "LayoutPage_ReadOnly", "LayoutPage_ReadOnly_Hint", "LayoutPage_StorageUnavailable",
        "NavigationMenu_Group_Jobs", "NavigationMenu_Group_Workflows", "NavigationMenu_Group_Observe", "NavigationMenu_Group_System",
        "NavigationMenu_Group_More", "Common_Breadcrumb", "Paginator_Label", "Paginator_RowsPerPage", "Paginator_Range",
        "Paginator_PreviousPage", "Paginator_NextPage", "Paginator_Page", "Common_SelectJob", "Common_SelectAllJobs",
        "RecurringJobsPage_SelectJob", "RecurringJobsPage_SelectAll", "Common_SelectedCount", "Common_ClearSelection",
        "Common_Cancel", "Common_Confirm", "Common_Close", "Dialog_DiscardTitle", "Dialog_DiscardBody", "Dialog_Discard",
        "Dialog_KeepEditing", "Dialog_TypeToConfirm", "Common_DeleteSelected_Title", "Common_DeleteSelected_TitleOne",
        "Common_DeleteAllConfirm_Title", "FailedJobsPage_RequeueAllConfirm_Title", "RecurringJobsPage_RemoveAllConfirm_Title",
        "ServersPage_ClearJobsConfirm_Title", "ServersPage_ClearJobsConfirm_Word", "ServersPage_ResetStorageConfirm_Title",
        "ServersPage_ResetStorageConfirm_Word", "JobDetailsPage_DeleteConfirm_Body", "Common_ShortcutConfirm", "Common_Dismiss",
        "Common_Done", "Common_ActionFailed", "Common_ServerResponded", "Common_NoAnswer", "Common_ViewJob", "Common_ListUpdated",
        "Common_ListUpdatedNew", "HomePage_Activity_Pause", "HomePage_Activity_Summary", "HomePage_RealtimeGraph_Pause",
        "Palette_Label", "Palette_Placeholder", "Palette_Results", "Palette_NothingFound", "Palette_ResultCount",
        "Palette_ResultCountOne", "Palette_Hint", "Palette_HintAnywhere", "Palette_Pages", "Palette_Commands",
        "Shortcuts_Search", "Shortcuts_Go", "Shortcuts_Move", "Shortcuts_Select", "Shortcuts_Open", "Shortcuts_Run",
        "Shortcuts_Delete", "Shortcuts_Help", "Shortcuts_SingleKey", "LayoutPage_Watchlist_Remove",
    ];

    [Theory]
    [MemberData(nameof(Locales))]
    public void EveryShellText_IsTranslated_WithTheSamePlaceholders(string locale)
    {
        ResourceSet? translated = Strings.ResourceManager.GetResourceSet(CultureInfo.GetCultureInfo(locale), createIfNotExists: true, tryParents: false);
        Assert.NotNull(translated);

        Assert.All(ShellKeys, key =>
        {
            string english = Strings.ResourceManager.GetString(key, CultureInfo.InvariantCulture)!;
            string? text = translated.GetString(key);

            Assert.False(string.IsNullOrWhiteSpace(text), $"{key} has no {locale} text");
            Assert.Equal(Placeholders(english), Placeholders(text!));
        });
    }

    [Fact]
    public void TheConfirmationTexts_AreNoLongerShouted()
    {
        string[] texts = [Strings.Common_DeleteConfirm, Strings.Common_DeleteAllConfirm, Strings.FailedJobsPage_RequeueAllConfirm, Strings.RecurringJobsPage_RemoveAllConfirm, Strings.ServersPage_ClearJobsConfirm, Strings.ServersPage_ResetStorageConfirm];

        Assert.All(texts, text => Assert.DoesNotMatch(@"\b[A-Z]{3,}\b", text));
    }

    private static string[] Placeholders(string text)
    {
        return [.. Regex.Matches(text, @"\{\d+\}").Select(static match => match.Value).Order(StringComparer.Ordinal)];
    }
}
