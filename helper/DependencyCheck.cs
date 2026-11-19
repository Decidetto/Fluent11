using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;

namespace Fluent11Helper
{
    internal static class DependencyCheck
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        private static int checkedCount;
        private static int missingCount;

        public static void Run(IPlayniteAPI api)
        {
            checkedCount = 0;
            missingCount = 0;
            try
            {
                CheckPlaynite(api);
                CheckWpf();
                CheckAchievements(api);
                CheckHowLongToBeat(api);
                CheckScreenshotUtilities(api);
                CheckSteam(api);
                CheckGameActivity(api);
                logger.Info($"Fluent 11 check: {checkedCount} dependencies checked, {missingCount} missing.");
                typesByPrefix.Clear();
            }
            catch (Exception e)
            {
                logger.Error(e, "Fluent 11 check failed.");
                typesByPrefix.Clear();
            }
        }

        private static void Check(string area, string what, string affects, Func<bool> present)
        {
            checkedCount++;
            bool ok;
            try
            {
                ok = present();
            }
            catch
            {
                ok = false;
            }

            if (!ok)
            {
                missingCount++;
                logger.Warn($"Fluent 11 check: {area}: {what} not found ({affects}).");
            }
        }

        private static bool Has(Type type, string member) => type?.GetMember(member, Any).Length > 0
            || (type != null && Reflect.HasMember(type, member));

        private static bool Has(object target, string member) => target != null && Has(target.GetType(), member);

        private static bool ScrollOptions(string name)
        {
            var options = Reflect.GetMember(PlayniteInternals.AppSettings, name);
            return Has(options, "Sensitivity") && Has(options, "Speed") && Has(options, "SmoothEnabled");
        }

        private static Type PropertyType(Type type, string name) => type?.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.PropertyType;

        private static Type ElementType(Type collection) => collection == null ? null
            : new[] { collection }.Concat(collection.GetInterfaces())
                .FirstOrDefault(a => a.IsGenericType && a.GetGenericTypeDefinition() == typeof(IEnumerable<>))?.GetGenericArguments()[0];

        private static bool HasAll(Type type, params string[] members) => type != null && members.All(a => Has(type, a));

        private static bool HasGuidMethod(Type type, string name) => type?.GetMethods()
            .Any(a => a.Name == name && a.GetParameters().Length == 1 && a.GetParameters()[0].ParameterType == typeof(Guid)) == true;

        private static bool HasText(string key) => Application.Current?.TryFindResource(key) is string;

        private static readonly Dictionary<string, List<Type>> typesByPrefix = new Dictionary<string, List<Type>>();

        private static Type FindType(string assemblyPrefix, string name) => Types(assemblyPrefix).FirstOrDefault(a => a.Name == name);

        private static List<Type> Types(string assemblyPrefix)
        {
            if (!typesByPrefix.TryGetValue(assemblyPrefix, out var types))
            {
                types = new List<Type>();
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name.StartsWith(assemblyPrefix, StringComparison.Ordinal)))
                {
                    try
                    {
                        types.AddRange(assembly.GetTypes());
                    }
                    catch (ReflectionTypeLoadException e)
                    {
                        types.AddRange(e.Types.Where(a => a != null));
                    }
                }

                typesByPrefix[assemblyPrefix] = types;
            }

            return types;
        }

        private static void CheckWpf()
        {
            const string area = "WPF";
            Check(area, "ContextMenu._parentPopup", "acrylic right-click menus", () => AcrylicPopups.ParentPopupField != null);
            Check(area, "ContextMenu.HookupParentPopup", "acrylic on the first right-click", () => AcrylicPopups.HookupParentPopupMethod != null);
        }

        private static void CheckPlaynite(IPlayniteAPI api)
        {
            const string area = "Playnite";
            var app = PlayniteInternals.App;
            Check(area, "PlayniteApplication.Current", "several features", () => app != null);
            var settings = PlayniteInternals.AppSettings;
            Check(area, "AppSettings", "several features", () => settings != null);
            if (settings != null)
            {
                Check(area, "AppSettings.GridItemWidth", "poster tile height follows zoom", () => Has(settings, "GridItemWidth"));
                Check(area, "AppSettings.GridItemWidth (writable)/GridItemSpacing", "grid Fit window",
                    () => settings.GetType().GetProperty("GridItemWidth")?.CanWrite == true && Reflect.GetMember(settings, "GridItemSpacing") is int);
                Check(area, "AppSettings.GridItemWidthRatio/GridItemHeightRatio", "cover format on own pages", () => Has(settings, "GridItemWidthRatio") && Has(settings, "GridItemHeightRatio"));
                Check(area, "AppSettings.FilterSettings.FilterChanged", "grid overlay closes on search and filter",
                    () => Reflect.GetMember(settings, "FilterSettings")?.GetType().GetEvent("FilterChanged") != null);
                Check(area, "AppSettings.NotificationPanelVisible", "notification panel closing", () => Has(settings, "NotificationPanelVisible"));
                Check(area, "AppSettings.ShowTopPanelSearchBox/ShowPanelSeparators", "search box and separator on own pages",
                    () => Has(settings, "ShowTopPanelSearchBox") && Has(settings, "ShowPanelSeparators"));
                Check(area, "AppSettings.ShowSidebar/SidebarPosition", "game menu: sidebar entries hidden (settings in the sidebar)",
                    () => Has(settings, "ShowSidebar") && Has(settings, "SidebarPosition"));
                Check(area, "AppSettings.DetailsVisibility.CommunityScore", "Steam rating follows Playnite's Community score setting",
                    () => Has(Reflect.GetMember(settings, "DetailsVisibility"), "CommunityScore"));
            }

            Check(area, "MainModel.SidebarItems (SideItem, Command)", "Fluent 11 entry opens the settings page",
                () => Reflect.GetMember(Reflect.GetMember(app, "MainModel"), "SidebarItems") is System.Collections.IEnumerable entries
                    && entries.Cast<object>().Any(a => Has(a, "SideItem") && Has(a, "Command")));
            Check(area, "MainModel.RestartApp", "restart button on the settings page",
                () => PlayniteInternals.RestartCommand != null);
            Check(area, "Extensions.SettingsSupportList", "achievements page and flyout data",
                () => Has(Reflect.GetMember(app, "Extensions"), "SettingsSupportList"));
            Check(area, "ThemeManager.CurrentTheme.DirectoryPath", "theme folder of the active theme",
                () => !string.IsNullOrEmpty(PlayniteInternals.CurrentThemeDirectory));
            Check(area, "ImageSourceManager.Cache.TryRemove", "default cover follows light/dark",
                () => Type.GetType("Playnite.ImageSourceManager, Playnite")?.GetField("Cache", BindingFlags.Public | BindingFlags.Static)?.FieldType
                    .GetMethods().Any(a => a.Name == "TryRemove" && a.GetParameters().Length == 1) == true);

            foreach (var name in new[] { "GameEditWindow", "MessageBoxWindow", "ProgressWindow", "UpdateWindow", "SearchWindow", "AddonsWindow",
                "EmulatedGameImportWindow", "FirstTimeStartupWindow", "ImageSelectionWindow", "ItemSelectionWithSearchWindow",
                "MetadataComparisonWindow", "MetadataDownloadWindow", "DatabaseFieldsManagerWindow", "LibraryGridView", "LibraryDetailsView", "GamesGridView" })
            {
                Check(area, $"type {name}", "window and view adjustments", () => FindType("Playnite", name) != null);
            }

            Check(area, "GameEditWindow." + global::Fluent11Helper.Fluent11Helper.ActionListName, "edit game: actions as cards",
                () => FindType("Playnite", "GameEditWindow")?.GetField(global::Fluent11Helper.Fluent11Helper.ActionListName,
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance) != null);

            Check(area, "AddonsViewModel." + Fluent11HelperSettingsViewModel.RestartFlag, "restart prompt after settings that need one",
                () => FindType("Playnite", "AddonsViewModel")?.GetProperty(Fluent11HelperSettingsViewModel.RestartFlag, BindingFlags.Public | BindingFlags.Instance)?.CanWrite == true);

            foreach (var name in new[] { "MainMenu", "GameMenu", "TrayContextMenu" })
            {
                Check(area, $"type {name}", "menu adjustments", () => FindType("Playnite", name) != null);
            }

            Check(area, "SearchItemWrapper.Item", "search: icons for game actions", () => Reflect.HasMember(FindType("Playnite", "SearchItemWrapper"), "Item"));
            foreach (var key in new[] { "LOCEditGame", "LOCSetGameCategory", "LOCSetCompletionStatus" })
            {
                Check(area, $"text {key}", "game menu and search icons", () => HasText(key));
            }

            Check(area, "CompletionStatuses.GetSettings().PlayedStatus", "time to beat: Almost done with a renamed Played status",
                () => api.Database.CompletionStatuses?.GetType().GetMethod("GetSettings", Type.EmptyTypes)?.ReturnType is Type statusSettings
                    && Has(statusSettings, "PlayedStatus"));
            Check(area, "EmulatedGamesImportViewModel.ScanCommmand/ImportCommand", "emulator import window: main button",
                () => HasAll(FindType("Playnite", "EmulatedGamesImportViewModel"), "ScanCommmand", "ImportCommand"));

            Check(area, "ScrollViewerBehaviours.ScrollDataProperty/ScrollAnimationData.Animation", "smooth scrolling eases out",
                () => SmoothScroll.ScrollDataProperty != null && SmoothScroll.AnimationProperty != null);
            Check(area, "ScrollViewerBehaviours/ScrollAnimationData (offset, sensitivity, owner, target)", "smooth scrolling with a trackpad",
                () => SmoothScroll.TrackpadSupported);
            Check(area, "ScrollViewerBehaviours.Speed/CustomScrollEnabled, AppSettings.DetailsViewList/DetailsScrollOptions", "smooth scrolling on own pages",
                () => SmoothScroll.PagesSupported && ScrollOptions("DetailsViewListScrollOptions") && ScrollOptions("DetailsViewDetailsScrollOptions"));
            Check(area, "type TopPanel", "top bar menus centered below the bar", () => FindType("Playnite", "TopPanel") != null);
            Check(area, "type Sidebar", "main menu opens beside the sidebar", () => FindType("Playnite", "Sidebar") != null);
            Check(area, "VirtualizingWrapPanel.sizeOfFirstItem", "grid Fit window: all tiles after maximizing or restoring",
                () => AutoColumns.SizeOfFirstItemField(FindType("VirtualizingWrapPanel", "VirtualizingWrapPanel")) != null);
            Check(area, "MessageBoxWindow.DisplayIcon", "message box icons", () => Has(FindType("Playnite", "MessageBoxWindow"), "DisplayIcon"));
            Check(area, "WindowBase.ShowMinimizeButton/ShowMaximizeButton", "achievements window title bar",
                () => Has(FindType("Playnite", "WindowBase"), "ShowMinimizeButton") && Has(FindType("Playnite", "WindowBase"), "ShowMaximizeButton"));
            var addons = FindType("Playnite", "AddonsViewModel");
            Check(area, "AddonsViewModel.UpdateAddonCount/UpdateAddonList/IsUpdateListLoading", "add-on update badge",
                () => Has(addons, "UpdateAddonCount") && Has(addons, "UpdateAddonList") && Has(addons, "IsUpdateListLoading"));
            Check(area, "HtmlPanel.Text", "changelog text color",
                () => Has(FindType("HtmlRenderer", "HtmlPanel") ?? FindType("TheArtOfDev", "HtmlPanel"), "Text"));

            Check(area, "text LOCFullscreenSettingsDesktopNotif (Playnite 10.63+)", "settings: fullscreen note as info bar", () => HasText("LOCFullscreenSettingsDesktopNotif"));
            foreach (var key in new[] { "LOCMenuAddons", "LOCMenuPlayniteSettingsTitle", "LOCRemoveFavoriteGame", "LOCUninstallGame" })
            {
                Check(area, $"text {key}", "game menu adjustments", () => HasText(key));
            }
        }

        private static void CheckAchievements(IPlayniteAPI api)
        {
            const string area = "Playnite Achievements";
            var plugin = AchievementsPage.FindPlayniteAchievements(api);
            if (plugin == null)
            {
                return;
            }

            var assembly = plugin.GetType().Assembly;
            var service = Reflect.GetMember(plugin, "ThemeIntegrationService");
            Check(area, "ThemeIntegrationService.EnsureAllGamesThemeDataLoaded", "achievements page stays empty",
                () => service?.GetType().GetMethod("EnsureAllGamesThemeDataLoaded") != null);
            Check(area, "EnsureAllGamesThemeDataLoaded(forceRefresh)", "achievements page updates after a restart only",
                () => service?.GetType().GetMethod("EnsureAllGamesThemeDataLoaded")?.GetParameters().Any(a => a.Name == "forceRefresh" && a.ParameterType == typeof(bool)) == true);
            Check(area, "RefreshRuntime.CacheInvalidated", "achievements page updates after a restart only",
                () => Reflect.GetMember(plugin, "RefreshRuntime")?.GetType().GetEvent("CacheInvalidated") != null);
            var dataService = Reflect.GetMember(plugin, "AchievementDataService");
            Check(area, "AchievementDataService.GetCachedSummaryDataForTheme.GlobalUnlockCountsByDate", "achievements page: unlocks chart",
                () => AchievementsPage.FindUnlockSummaryMethod(dataService)?.ReturnType is Type summary && Has(summary, "GlobalUnlockCountsByDate"));
            var calculate = assembly.GetType("PlayniteAchievements.Models.Achievements.Scoring.AchievementLevelCalculator")?.GetMethods().FirstOrDefault(a => a.Name == "CalculateModern");
            Check(area, "AchievementLevelCalculator.CalculateModern", "achievements page: next rank", () => calculate != null);
            if (calculate != null)
            {
                Check(area, "level snapshot Rank/NextRank/RankEndLevel/IsMaxLevel", "achievements page: rank and next rank",
                    () => HasAll(calculate.ReturnType, "Rank", "NextRank", "RankEndLevel", "IsMaxLevel"));
            }
            Check(area, "AchievementRankPresentation.FormatRank", "achievements page: rank name",
                () => assembly.GetType("PlayniteAchievements.Models.Achievements.Scoring.AchievementRankPresentation")?.GetMethod("FormatRank", new[] { typeof(string) }) != null);

            var root = PlayniteInternals.FindSettingsRoot("PlayniteAchievements");
            Check(area, "theme data (PluginSettings)", "achievements page and flyout", () => root != null);
            if (root != null)
            {
                foreach (var name in new[] { "GameSummariesDesc", "TotalOverall", "CollectorScore", "CollectorRank", "AllAchievementsRarityAsc", "RarestRecentUnlocksTop10" })
                {
                    Check(area, $"theme data {name}", "achievements page", () => Has(root, name));
                }

                var rootType = root.GetType();
                Check(area, "game summary members", "achievements page: game list, sorting and progress",
                    () => HasAll(ElementType(PropertyType(rootType, "GameSummariesDesc")),
                        "GameId", "Name", "SortingName", "Progress", "IsCompleted", "LastUnlockDate", "UnlockedCount", "AchievementCount"));
                Check(area, "TotalOverall Unlocked/Total", "achievements page: progress ring",
                    () => HasAll(PropertyType(rootType, "TotalOverall"), "Unlocked", "Total"));
                Check(area, "achievement Unlocked/RaritySortValue", "achievements page: rarest unlocks",
                    () => HasAll(ElementType(PropertyType(rootType, "AllAchievementsRarityAsc")), "Unlocked", "RaritySortValue"));
            }

            var topItem = plugin.GetTopPanelItems()?.FirstOrDefault();
            if (topItem != null)
            {
                Check(area, "top bar button icon as Path", "Fluent trophy in the top bar", () => topItem.Icon is System.Windows.Shapes.Path);
            }

            Check(area, "PlayniteAchievementsPlugin.OpenViewAchievementsWindow", "click on a game and View all in game details open its achievements",
                () => AchievementsPage.FindOpenWindowMethod(plugin) != null);
            Check(area, "resources PlayAch.Brush.Rarity.*", "rarity colors on achievements page and window",
                () => new[] { "Common", "Uncommon", "Rare", "UltraRare" }.All(a => Application.Current?.TryFindResource("PlayAch.Brush.Rarity." + a) is System.Windows.Media.Brush));

            var pluginType = assembly.GetType("PlayniteAchievements.PlayniteAchievementsPlugin");
            Check(area, "PlayniteAchievementsPlugin.Instance/OpenManageAchievementsView", "achievements window: edit achievements",
                () => pluginType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static) != null && pluginType.GetMethod("OpenManageAchievementsView") != null);
            Check(area, "type ViewAchievementsControl", "achievements window restyle", () => FindType("PlayniteAchievements", "ViewAchievementsControl") != null);
            Check(area, "RefreshProgressControl and its view model members", "refresh progress window restyle",
                () => FindType("PlayniteAchievements", AchievementsRefreshDialog.ControlName) != null
                    && AchievementsRefreshDialog.ModelMembers.All(a => Has(FindType("PlayniteAchievements", "RefreshProgressViewModel"), a)));

            var model = assembly.GetType("PlayniteAchievements.ViewModels.ViewAchievementsViewModel");
            Check(area, "ViewAchievementsViewModel", "achievements window", () => model != null);
            if (model != null)
            {
                Check(area, "ViewAchievementsViewModel.RefreshGameCommand", "achievements window: refresh", () => Has(model, "RefreshGameCommand"));
                Check(area, "ViewAchievementsViewModel.SortDataGrid/ResetSortToDefault/CurrentSortPath", "achievements window: sort",
                    () => model.GetMethod("SortDataGrid", new[] { typeof(string), typeof(ListSortDirection) }) != null
                        && model.GetMethod("ResetSortToDefault", Type.EmptyTypes) != null && Has(model, "CurrentSortPath"));
                Check(area, "ViewAchievementsViewModel.AchievementsControlBar", "achievements window: filter", () => Has(model, "AchievementsControlBar"));
                var barItem = ElementType(PropertyType(PropertyType(model, "AchievementsControlBar"), "Items"));
                Check(area, "AchievementsControlBar.Items filter toggles (Icon/IsChecked/Content/EffectiveIsVisible)", "achievements window: filter and tabs",
                    () => Has(barItem, "EffectiveIsVisible")
                        && Types("PlayniteAchievements").Any(a => barItem.IsAssignableFrom(a) && HasAll(a, "Icon", "Content")
                            && a.GetProperty("IsChecked")?.CanWrite == true));
                Check(area, "ViewAchievementsViewModel.SummaryItems members", "achievements window: header and tab counts",
                    () => HasAll(ElementType(PropertyType(model, "SummaryItems")),
                        "GameCoverPath", "PlaytimeSeconds", "UnlockedAchievements", "TotalAchievements", "LastUnlockLocal"));
                Check(area, "ViewAchievementsViewModel.GameId or _gameId", "achievements window: edit achievements",
                    () => Has(model, "GameId") || Has(model, "_gameId"));
            }
        }

        private static void CheckHowLongToBeat(IPlayniteAPI api)
        {
            const string area = "HowLongToBeat";
            var plugin = HltbData.FindPlugin(api);
            if (plugin == null)
            {
                return;
            }

            var database = HltbData.FindDatabase(plugin);
            Check(area, "PluginDatabase", "time to beat page, window and flyout", () => database != null);
            if (database != null)
            {
                var type = database.GetType();
                Check(area, "GetAllCache", "time to beat page stays empty", () => type.GetMethod("GetAllCache", Type.EmptyTypes) != null);
                var get = type.GetMethods().FirstOrDefault(a => a.Name == "Get" && a.GetParameters().Length == 3 && a.GetParameters()[0].ParameterType == typeof(Guid));
                Check(area, "Get(Guid, bool, bool)", "time to beat window and flyout", () => get != null);

                var entryType = get?.ReturnType;
                var dataType = entryType?.GetMethod("GetData", Type.EmptyTypes)?.ReturnType;
                var timesType = dataType?.GetProperty("GameHltbData")?.PropertyType;
                Check(area, "entry HasData/HasDataEmpty/Id/GetData", "time to beat data",
                    () => Has(entryType, "HasData") && Has(entryType, "HasDataEmpty") && Has(entryType, "Id") && dataType != null);
                Check(area, "GameHltbData times", "time to beat values",
                    () => new[] { "MainStory", "MainExtra", "Completionist", "Solo", "CoOp", "Vs", "TimeToBeat", "GameType" }.All(a => Has(timesType, a)));

                Check(area, "SetThemesResources/GameContext", "times update without changing game",
                    () => type.GetMethod("SetThemesResources", new[] { typeof(Game) }) != null && Has(type, "GameContext"));
                Check(area, "DatabaseItemUpdated/DatabaseItemCollectionChanged", "times update without changing game",
                    () => type.GetEvent("DatabaseItemUpdated") != null && type.GetEvent("DatabaseItemCollectionChanged") != null);
                Check(area, "HowLongToBeatApi.SearchData/AddOrUpdate", "time to beat window: search for a game",
                    () => Has(type, "HowLongToBeatApi")
                        && FindType("HowLongToBeat", "HowLongToBeatApi")?.GetMethods().Any(a => a.Name == "SearchData") == true
                        && type.GetMethods().Any(a => a.Name == "AddOrUpdate"));
                Check(area, "SearchData result DateLastRefresh", "time to beat window: date of a new search result",
                    () => Has(FindType("HowLongToBeat", "HowLongToBeatApi")?.GetMethods()
                        .FirstOrDefault(a => a.Name == "SearchData" && a.GetParameters().Length >= 1 && a.GetParameters()[0].ParameterType == typeof(Game))?.ReturnType,
                        "DateLastRefresh"));
                Check(area, "Refresh(Guid)/Remove(Guid)", "time to beat window: refresh and delete",
                    () => HasGuidMethod(type, "Refresh") && HasGuidMethod(type, "Remove"));
            }

            Check(area, "HowLongToBeatSelect and its elements", "selection window restyle",
                () => HasAll(FindType("HowLongToBeat", HltbSelectDialog.ControlName), HltbSelectDialog.ElementNames));
            Check(area, "PluginSettingsViewModel/SidebarItem", "sidebar entry setting", () => Has(plugin, "PluginSettingsViewModel") && Has(plugin, "SidebarItem"));
            Check(area, "Settings.EnableIntegrationButtonSide", "sidebar entry setting",
                () => Has(PropertyType(Reflect.GetMember(plugin, "PluginSettingsViewModel")?.GetType(), "Settings"), "EnableIntegrationButtonSide"));
            Check(area, "texts LOCHowLongToBeat/LOCHowLongToBeatPluginView", "game menu entry", () => HasText("LOCHowLongToBeat") && HasText("LOCHowLongToBeatPluginView"));
        }

        private static void CheckSteam(IPlayniteAPI api)
        {
            var steam = api.Addons.Plugins.FirstOrDefault(a => a.GetType().Assembly.GetName().Name == "SteamLibrary");
            if (steam == null)
            {
                return;
            }

            Check("Steam", "friends button with icon ECF9", "Steam friends icon in the top bar",
                () => steam.GetTopPanelItems()?.Any(a => a?.Icon is TextBlock icon && (icon.Text == AddonIcons.SteamFriendsGlyph || icon.Text == "\uE902")) == true);
        }

        private static void CheckGameActivity(IPlayniteAPI api)
        {
            var plugin = api.Addons.Plugins.FirstOrDefault(a => a.GetType().Assembly.GetName().Name == "GameActivity");
            if (plugin == null)
            {
                return;
            }

            Check("GameActivity", "sidebar button with icon E97F", "GameActivity icon in sidebar and top bar",
                () => plugin.GetSidebarItems()?.Any(a => a?.Icon is TextBlock icon
                    && (icon.Text == AddonIcons.GameActivityGlyph || icon.Text == AddonIcons.GameActivityFluentGlyph)) == true);
            Check("GameActivity", "menu texts LOCGameActivity / LOCGameActivityViewGameActivity", "GameActivity icons in the game menu",
                () => new[] { "LOCGameActivity", "LOCGameActivityViewGameActivity" }.All(a => Application.Current?.TryFindResource(a) is string));
        }

        private static void CheckScreenshotUtilities(IPlayniteAPI api)
        {
            const string area = "Screenshot Utilities";
            var plugin = api.Addons.Plugins.FirstOrDefault(a => a.GetType().Assembly.GetName().Name == "ScreenshotUtilities");
            if (plugin == null)
            {
                return;
            }

            var assembly = plugin.GetType().Assembly;
            var settings = Reflect.GetMember(Reflect.GetMember(plugin, "Settings"), "Settings");
            Check(area, "Settings.Settings", "screenshot strip", () => settings != null);
            Check(area, "CurrentScreenshotGroups/ProviderSettings", "screenshot strip",
                () => Has(settings, "CurrentScreenshotGroups") && Has(settings, "ProviderSettings"));
            Check(area, "ScreenshotGroups.CreateGroupsFromFiles", "screenshot strip",
                () => assembly.GetType("KNARZhelper.ScreenshotsCommon.Models.ScreenshotGroups")?.GetMethod("CreateGroupsFromFiles", Any) != null);
            var group = assembly.GetType("KNARZhelper.ScreenshotsCommon.Models.ScreenshotGroup");
            Check(area, "ScreenshotGroup Name/Provider.Name/Screenshots", "screenshot strip and viewer title",
                () => HasAll(group, "Name", "Screenshots") && Has(PropertyType(group, "Provider"), "Name"));
            Check(area, "Screenshot DisplayThumbnail/IsVideo/DisplayPath/VideoPath", "screenshot strip and viewer",
                () => HasAll(assembly.GetType("KNARZhelper.ScreenshotsCommon.Models.Screenshot"), "DisplayThumbnail", "IsVideo", "DisplayPath", "VideoPath"));
            Check(area, "ProviderSettings.SortOrder", "screenshot strip: order of the groups",
                () => Has(assembly.GetType("ScreenshotUtilities.Models.ProviderSettings"), "SortOrder"));
            var guardType = api.Addons.Plugins.FirstOrDefault(a => a.GetType().FullName == ScreenshotUtilitiesGuard.PluginTypeName)?.GetType();
            Check(area, $"{ScreenshotUtilitiesGuard.PluginTypeName}.Timer/_lastSelectedGame", "crash guard",
                () => guardType?.GetProperty("Timer", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) != null
                    && guardType.GetField("_lastSelectedGame", BindingFlags.Instance | BindingFlags.NonPublic) != null);
        }
    }
}
