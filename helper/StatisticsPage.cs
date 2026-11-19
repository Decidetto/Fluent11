using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Threading;

namespace Fluent11Helper
{
    internal class StatisticsPage
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private const string PageFile = "Views\\Fluent11StatisticsPage.xaml";

        private readonly IPlayniteAPI api;
        private readonly string themeDir;
        private readonly Func<Fluent11HelperSettings> settings;
        private readonly Action saveSettings;
        private readonly StatsPageModel model = new StatsPageModel();
        private Control view;
        private string searchText = string.Empty;
        private DispatcherTimer searchTimer;
        private List<StatsGame> allGames = new List<StatsGame>();
        private ulong totalPlaytime;

        private string filterKind;
        private Guid filterId;
        private string filterName;

        private Popup flyout;
        private readonly StatsFlyoutModel flyoutModel = new StatsFlyoutModel();
        private Window flyoutWindow;

        public StatisticsPage(IPlayniteAPI api, string themeDir, Func<Fluent11HelperSettings> settings, Action saveSettings)
        {
            this.api = api;
            this.themeDir = themeDir;
            this.settings = settings;
            this.saveSettings = saveSettings;
        }

        public SidebarItem CreateSidebarItem()
        {
            return new SidebarItem
            {
                Title = "Insights",
                Type = SiderbarItemType.View,
                Icon = CreateIcon(),
                Opened = () =>
                {
                    Load();
                    view = view ?? CreateView();
                    CoverFormat.Apply(view);
                    return view;
                }
            };
        }

        private static object CreateIcon()
        {
            var icon = new TextBlock { Text = "\uE8A1", FontSize = 20 };
            icon.SetResourceReference(TextBlock.FontFamilyProperty, "FluentIconFont");
            return icon;
        }

        private Control CreateView()
        {
            var host = PageTools.CreateHost();
            try
            {
                var path = Path.Combine(themeDir, PageFile);
                using (var stream = File.OpenRead(path))
                {
                    host.Content = XamlReader.Load(stream);
                }

                host.DataContext = model;
                host.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(OnClick));

                flyout = (host.Content as FrameworkElement)?.FindName("GameFlyout") as Popup;
                if (flyout != null)
                {
                    flyout.Closed += (s, e) => DetachFlyoutWindow();
                    flyout.DataContext = flyoutModel;
                    flyout.Placement = PlacementMode.Custom;
                    flyout.CustomPopupPlacementCallback = (popupSize, targetSize, offset) =>
                    {
                        var scale = flyout.PlacementTarget is System.Windows.Media.Visual target ? System.Windows.Media.VisualTreeHelper.GetDpi(target).DpiScaleX : 1.0;
                        var acrylic = !flyout.AllowsTransparency;
                        var right = acrylic ? 0 : -8 * scale;
                        var gap = acrylic ? 4 * scale : 0;
                        return new[]
                        {
                            new CustomPopupPlacement(new Point(targetSize.Width - popupSize.Width - right, targetSize.Height + gap), PopupPrimaryAxis.Horizontal),
                            new CustomPopupPlacement(new Point(targetSize.Width - popupSize.Width - right, -popupSize.Height - gap), PopupPrimaryAxis.Horizontal)
                        };
                    };
                    if (flyout.FindName("FlyoutPlay") is ButtonBase play)
                    {
                        play.Click += (s, e) => RunFlyoutAction(game => { if (game.IsInstalled) api.StartGame(game.Id); else api.InstallGame(game.Id); });
                    }

                    if (flyout.FindName("FlyoutLibrary") is ButtonBase library)
                    {
                        library.Click += (s, e) => RunFlyoutAction(game =>
                        {
                            api.MainView.SwitchToLibraryView();
                            api.MainView.SelectGame(game.Id);
                        });
                    }
                    ((host.Content as FrameworkElement)?.FindName("GameList") as UIElement)?.AddHandler(
                        ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((s, e) =>
                        {
                            if (e.VerticalChange != 0 || e.ViewportHeightChange != 0)
                            {
                                CloseFlyout();
                            }
                        }));
                    host.IsVisibleChanged += (s, e) =>
                    {
                        if (!host.IsVisible)
                        {
                            CloseFlyout();
                        }
                    };
                }

                var searchBox = (host.Content as FrameworkElement)?.FindName("SearchBox") as UIElement;
                PageTools.BindPlayniteSettings(host.Content as FrameworkElement);
                searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
                searchTimer.Tick += (s, e) =>
                {
                    searchTimer.Stop();
                    ApplyView();
                };
                if (searchBox != null)
                {
                    searchBox.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((s, e) =>
                    {
                        searchText = (e.OriginalSource as TextBox)?.Text ?? string.Empty;
                        searchTimer.Stop();
                        searchTimer.Start();
                    }));
                }
            }
            catch (Exception e)
            {
                logger.Error(e, "Fluent 11: failed to load statistics page.");
                host.Content = new TextBlock
                {
                    Text = "Insights page could not be loaded. See extensions.log for details.",
                    Margin = new Thickness(24, 64, 24, 0)
                };
            }

            return host;
        }

        #region Daten

        private void Load()
        {
            try
            {
                var includeHidden = settings().StatsIncludeHidden;
                var includeUnplayed = settings().StatsIncludeUnplayed;
                var games = api.Database.Games.Where(a => includeHidden || !a.Hidden).Where(MatchesFilter).ToList();

                var played = games.Where(a => a.Playtime > 0).ToList();
                totalPlaytime = 0;
                foreach (var game in played)
                {
                    totalPlaytime += game.Playtime;
                }

                ulong installSize = 0;
                foreach (var game in games.Where(a => a.IsInstalled))
                {
                    installSize += game.InstallSize ?? 0;
                }

                model.Total = games.Count;
                model.Installed = games.Count(a => a.IsInstalled);
                model.Favorites = games.Count(a => a.Favorite);
                model.Hidden = games.Count(a => a.Hidden);
                model.PlayedCount = played.Count;
                model.PlaytimeText = GameText.Hours(totalPlaytime);
                model.AverageText = played.Count > 0 ? GameText.Time(totalPlaytime / (ulong)played.Count) : "-";
                model.InstallSizeText = FormatSize(installSize);
                model.InstalledPercent = Percent(model.Installed, games.Count);

                var maxPlaytime = played.Count > 0 ? played.Max(a => a.Playtime) : 1;
                allGames = (includeUnplayed ? games : played).Select(a => new StatsGame(a, maxPlaytime, GameText.CoverPath(api, a))).ToList();
                ApplyView(false);

                model.Completion = Bars(games, a => a.CompletionStatus?.Name ?? "No status");
                model.Sources = Bars(games, a => a.Source?.Name ?? "Playnite");

                model.FilterText = filterKind == null ? null : filterKind + ": " + filterName;
                model.IsFilterActive = filterKind != null || includeHidden || includeUnplayed;
                model.Notify(null);
            }
            catch (Exception e)
            {
                logger.Error(e, "Fluent 11: failed to compute statistics.");
            }
        }

        private void ApplyView(bool notify = true)
        {
            var options = settings();
            var search = searchText.Trim();
            IEnumerable<StatsGame> games = string.IsNullOrEmpty(search)
                ? allGames
                : allGames.Where(a => a.Name?.IndexOf(search, StringComparison.CurrentCultureIgnoreCase) >= 0);

            Func<StatsGame, object> key;
            switch (options.StatsSort)
            {
                case StatsSort.LastPlayed: key = a => a.LastActivity ?? DateTime.MinValue; break;
                case StatsSort.Name: key = a => a.SortName; break;
                default: key = a => a.Playtime; break;
            }

            var sorted = options.StatsSortDescending ? games.OrderByDescending(key) : games.OrderBy(key);
            model.Games = sorted.ThenBy(a => a.SortName).ToList();
            model.SortText = SortNames[options.StatsSort] + (options.StatsSortDescending ? " ↓" : " ↑");
            if (notify)
            {
                model.Notify(nameof(StatsPageModel.Games));
                model.Notify(nameof(StatsPageModel.SortText));
            }
        }

        private static readonly Dictionary<StatsSort, string> SortNames = new Dictionary<StatsSort, string>
        {
            { StatsSort.Playtime, "Time played" },
            { StatsSort.LastPlayed, "Last played" },
            { StatsSort.Name, "Name" }
        };

        private bool MatchesFilter(Game game)
        {
            switch (filterKind)
            {
                case "Platform": return game.PlatformIds?.Contains(filterId) == true;
                case "Source": return game.SourceId == filterId;
                default: return true;
            }
        }

        private static List<StatsBar> Bars(List<Game> games, Func<Game, string> name)
        {
            return games.GroupBy(name)
                .Select(a => new StatsBar(a.Key, a.Count(), Percent(a.Count(), games.Count)))
                .OrderByDescending(a => a.Count).ThenBy(a => a.Name)
                .ToList();
        }

        private static double Percent(int value, int total)
        {
            return total > 0 ? 100.0 * value / total : 0;
        }

        private static string FormatSize(ulong bytes)
        {
            const double GB = 1024.0 * 1024 * 1024;
            return bytes >= GB * 1024 ? $"{bytes / GB / 1024:N1} TB" : $"{bytes / GB:N0} GB";
        }

        #endregion

        #region Bedienung

        private void OnClick(object sender, RoutedEventArgs e)
        {
            if (!(e.OriginalSource is ButtonBase button))
            {
                return;
            }

            switch (button.Tag as string)
            {
                case "StatsFilterMenu":
                    ShowFilterMenu(button);
                    e.Handled = true;
                    break;
                case "StatsSortMenu":
                    ShowSortMenu(button);
                    e.Handled = true;
                    break;
                case "StatsOpenGame":
                    ShowFlyout(button);
                    e.Handled = true;
                    break;
            }
        }

        private void ShowFlyout(FrameworkElement row)
        {
            var game = row.DataContext as StatsGame;
            if (flyout == null || game == null)
            {
                return;
            }

            flyout.IsOpen = false;
            if (!FillFlyout(game))
            {
                return;
            }

            flyout.PlacementTarget = row;
            UpdateFlyoutLook();
            AcrylicPopups.WatchPopup(flyout);
            flyout.IsOpen = true;
            AttachFlyoutWindow(Window.GetWindow(row));
        }

        private void UpdateFlyoutLook()
        {
            if (!(flyout.FindName("FlyoutShadow") is Border shadow) || !(flyout.FindName("FlyoutCard") is Border card))
            {
                return;
            }

            if (!flyout.AllowsTransparency)
            {
                shadow.Visibility = Visibility.Collapsed;
                card.Margin = new Thickness(0);
                card.SetResourceReference(Border.BorderBrushProperty, "PopupAcrylicBorderBrush");
                card.SetResourceReference(Border.BackgroundProperty, "PopupAcrylicBrush");
            }
            else
            {
                shadow.ClearValue(UIElement.VisibilityProperty);
                card.ClearValue(FrameworkElement.MarginProperty);
                card.ClearValue(Border.BorderBrushProperty);
                card.ClearValue(Border.BackgroundProperty);
            }
        }

        private bool FillFlyout(StatsGame row)
        {
            var game = api.Database.Games.Get(row.GameId);
            if (game == null)
            {
                return false;
            }

            var m = flyoutModel;
            m.Game = row;
            m.GameId = game.Id;
            m.IsInstalled = game.IsInstalled;
            m.Name = game.Name;
            m.SubText = game.Source?.Name ?? "Playnite";

            if (game.Playtime > 0)
            {
                m.PlaytimeText = GameText.Time(game.Playtime);
                var share = totalPlaytime > 0 ? 100.0 * game.Playtime / totalPlaytime : 0;
                m.ShareText = (share > 0 && share < 1 ? "< 1" : Math.Round(share).ToString(CultureInfo.CurrentCulture)) + " % of total";
                var rank = allGames.Count(a => a.Playtime > game.Playtime) + 1;
                m.RankText = "#" + rank;
            }
            else
            {
                m.PlaytimeText = StatsGame.NotPlayedText;
                m.ShareText = null;
                m.RankText = null;
            }

            m.SessionsText = game.PlayCount > 0 ? game.PlayCount.ToString("N0", CultureInfo.CurrentCulture) : "-";
            m.AverageText = game.PlayCount > 0 ? GameText.Time(game.Playtime / game.PlayCount) : "-";
            m.LastPlayedText = game.LastActivity?.ToString("d", CultureInfo.CurrentCulture) ?? "-";
            m.AddedText = game.Added?.ToString("d", CultureInfo.CurrentCulture) ?? "-";
            m.StatusText = game.CompletionStatus?.Name ?? "-";
            m.SizeText = !game.IsInstalled ? "Not installed" : game.InstallSize > 0 ? FormatGameSize(game.InstallSize.Value) : "-";
            m.ActionText = game.IsInstalled ? "Play" : "Install";

            ReadTimeToBeat(game, m);
            ReadAchievements(game, m);
            m.Notify(null);
            return true;
        }

        private void RunFlyoutAction(Action<Game> action)
        {
            var game = api.Database.Games.Get(flyoutModel.GameId);
            CloseFlyout();
            if (game == null)
            {
                return;
            }

            try
            {
                action(game);
            }
            catch (Exception e)
            {
                logger.Error(e, "Fluent 11: insights flyout action failed.");
            }
        }

        private void ReadTimeToBeat(Game game, StatsFlyoutModel m)
        {
            m.HasTimeToBeat = false;
            try
            {
                var times = HltbData.Times(HltbData.CachedEntry(HltbData.FindDatabase(api), game.Id));
                var timeToBeat = HltbData.Seconds(times, "TimeToBeat");
                if (timeToBeat <= 0)
                {
                    return;
                }

                var played = (long)Math.Min(game.Playtime, (ulong)long.MaxValue);
                var percent = Math.Min(100.0, 100.0 * played / timeToBeat);
                m.HasTimeToBeat = true;
                m.TimeToBeatPercent = percent;
                m.TimeToBeatPercentText = Math.Floor(percent) + " %";
                m.TimeToBeatText = GameText.RoundedHours(played) + " of " + GameText.RoundedHours(timeToBeat);
            }
            catch (Exception e)
            {
                logger.Debug($"Fluent 11: no time to beat for insights flyout: {e.Message}");
            }
        }

        private void ReadAchievements(Game game, StatsFlyoutModel m)
        {
            m.HasAchievements = false;
            if (!AchievementsPage.TryGetGameProgress(api, game.Id, out var unlocked, out var total) || total <= 0)
            {
                return;
            }

            var percent = Math.Min(100.0, 100.0 * unlocked / total);
            m.HasAchievements = true;
            m.AchievementsPercent = percent;
            m.AchievementsPercentText = Math.Floor(percent) + " %";
            m.AchievementsText = $"{unlocked} of {total} unlocked";
        }

        private static string FormatGameSize(ulong bytes)
        {
            return bytes < 1073741824 ? $"{Math.Round(bytes / 1048576.0)} MB" : $"{Math.Round(bytes / 1073741824.0)} GB";
        }

        private void CloseFlyout()
        {
            if (flyout?.IsOpen == true)
            {
                flyout.IsOpen = false;
            }
        }

        private void AttachFlyoutWindow(Window window)
        {
            DetachFlyoutWindow();
            flyoutWindow = window;
            if (window == null)
            {
                return;
            }

            window.PreviewMouseDown += OnWindowMouseDown;
            window.PreviewKeyDown += OnWindowKeyDown;
            window.Deactivated += OnWindowChanged;
            window.LocationChanged += OnWindowChanged;
            window.SizeChanged += OnWindowChanged;
        }

        private void DetachFlyoutWindow()
        {
            if (flyoutWindow == null)
            {
                return;
            }

            flyoutWindow.PreviewMouseDown -= OnWindowMouseDown;
            flyoutWindow.PreviewKeyDown -= OnWindowKeyDown;
            flyoutWindow.Deactivated -= OnWindowChanged;
            flyoutWindow.LocationChanged -= OnWindowChanged;
            flyoutWindow.SizeChanged -= OnWindowChanged;
            flyoutWindow = null;
        }

        private void OnWindowMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (flyout.Child?.IsMouseOver == true)
            {
                return;
            }

            if ((flyout.PlacementTarget as UIElement)?.IsMouseOver == true)
            {
                e.Handled = true;
            }

            CloseFlyout();
        }

        private void OnWindowKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                CloseFlyout();
                e.Handled = true;
            }
        }

        private void OnWindowChanged(object sender, EventArgs e)
        {
            CloseFlyout();
        }

        private void ShowSortMenu(UIElement target)
        {
            var options = settings();
            var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.Bottom };
            foreach (var sort in SortNames)
            {
                var value = sort.Key;
                menu.Items.Add(SortOption(sort.Value, options.StatsSort == value, () => options.StatsSort = value));
            }

            menu.Items.Add(new Separator());
            menu.Items.Add(SortOption("Ascending", !options.StatsSortDescending, () => options.StatsSortDescending = false));
            menu.Items.Add(SortOption("Descending", options.StatsSortDescending, () => options.StatsSortDescending = true));
            PageTools.OpenBarMenu(menu, target);
        }

        private MenuItem SortOption(string header, bool isChecked, Action apply)
        {
            var item = MenuPolish.Radio(new MenuItem { Header = header, IsCheckable = true, IsChecked = isChecked });
            item.Click += (s, a) =>
            {
                apply();
                saveSettings();
                ApplyView();
            };
            return item;
        }

        private void ShowFilterMenu(UIElement target)
        {
            var includeHidden = settings().StatsIncludeHidden;
            var games = api.Database.Games.Where(a => includeHidden || !a.Hidden).ToList();
            var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.Bottom };

            menu.Items.Add(Option("All games", filterKind == null, null, Guid.Empty, null));
            menu.Items.Add(new Separator());
            menu.Items.Add(Submenu("Platform", games.SelectMany(a => a.PlatformIds ?? new List<Guid>()), id => api.Database.Platforms.Get(id)?.Name));
            menu.Items.Add(Submenu("Source", games.Where(a => a.SourceId != Guid.Empty).Select(a => a.SourceId), id => api.Database.Sources.Get(id)?.Name));
            menu.Items.Add(new Separator());
            var hidden = new MenuItem { Header = "Include hidden games", IsCheckable = true, IsChecked = includeHidden };
            hidden.Click += (s, a) =>
            {
                settings().StatsIncludeHidden = !includeHidden;
                saveSettings();
                Load();
            };
            menu.Items.Add(hidden);
            var includeUnplayed = settings().StatsIncludeUnplayed;
            var unplayed = new MenuItem { Header = "Include unplayed games", IsCheckable = true, IsChecked = includeUnplayed };
            unplayed.Click += (s, a) =>
            {
                settings().StatsIncludeUnplayed = !includeUnplayed;
                saveSettings();
                Load();
            };
            menu.Items.Add(unplayed);
            PageTools.OpenBarMenu(menu, target);
        }

        private MenuItem Submenu(string kind, IEnumerable<Guid> ids, Func<Guid, string> name)
        {
            var submenu = new MenuItem { Header = kind };
            foreach (var entry in ids.Distinct().Select(a => new { Id = a, Name = name(a) })
                .Where(a => !string.IsNullOrEmpty(a.Name)).OrderBy(a => a.Name))
            {
                submenu.Items.Add(Option(entry.Name, filterKind == kind && filterId == entry.Id, kind, entry.Id, entry.Name));
            }

            submenu.IsEnabled = submenu.Items.Count > 0;
            return submenu;
        }

        private MenuItem Option(string header, bool isChecked, string kind, Guid id, string name)
        {
            var item = MenuPolish.Radio(new MenuItem { Header = header, IsCheckable = true, IsChecked = isChecked });
            item.Click += (s, a) =>
            {
                filterKind = kind;
                filterId = id;
                filterName = name;
                Load();
            };
            return item;
        }

        #endregion
    }

    public class StatsFlyoutModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        public StatsGame Game { get; set; }
        public Guid GameId { get; set; }
        public bool IsInstalled { get; set; }
        public string Name { get; set; }
        public string SubText { get; set; }
        public string PlaytimeText { get; set; }
        public string ShareText { get; set; }
        public string RankText { get; set; }
        public string SessionsText { get; set; }
        public string AverageText { get; set; }
        public string LastPlayedText { get; set; }
        public string AddedText { get; set; }
        public string StatusText { get; set; }
        public string SizeText { get; set; }
        public bool HasTimeToBeat { get; set; }
        public double TimeToBeatPercent { get; set; }
        public string TimeToBeatPercentText { get; set; }
        public string TimeToBeatText { get; set; }
        public bool HasAchievements { get; set; }
        public double AchievementsPercent { get; set; }
        public string AchievementsPercentText { get; set; }
        public string AchievementsText { get; set; }
        public string ActionText { get; set; }

        public void Notify(string property)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        }
    }

    public class StatsPageModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        public List<StatsGame> Games { get; set; } = new List<StatsGame>();
        public List<StatsBar> Completion { get; set; } = new List<StatsBar>();
        public List<StatsBar> Sources { get; set; } = new List<StatsBar>();
        public string FilterText { get; set; }
        public string SortText { get; set; }
        public bool IsFilterActive { get; set; }

        public int Total { get; set; }
        public int Installed { get; set; }
        public double InstalledPercent { get; set; }
        public int Favorites { get; set; }
        public int Hidden { get; set; }
        public int PlayedCount { get; set; }
        public string PlaytimeText { get; set; }
        public string AverageText { get; set; }
        public string InstallSizeText { get; set; }

        public void Notify(string property)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        }
    }

    public class StatsGame
    {
        public Guid GameId { get; }
        public string Name { get; }
        public string SortName { get; }
        public ulong Playtime { get; }
        public string Cover { get; }
        public string Info { get; }
        public string PlaytimeText { get; }
        public double Share { get; }
        public DateTime? LastActivity { get; }

        public const string NotPlayedText = "Not played";

        public StatsGame(Game game, ulong maxPlaytime, string cover)
        {
            GameId = game.Id;
            Name = game.Name;
            SortName = string.IsNullOrEmpty(game.SortingName) ? game.Name : game.SortingName;
            Playtime = game.Playtime;
            Cover = cover;
            Info = game.CompletionStatus?.Name ?? string.Empty;
            PlaytimeText = game.Playtime > 0 ? GameText.Time(game.Playtime) : NotPlayedText;
            Share = maxPlaytime > 0 ? 100.0 * game.Playtime / maxPlaytime : 0;
            LastActivity = game.LastActivity;
        }
    }

    public class StatsBar
    {
        public string Name { get; }
        public int Count { get; }
        public double Percent { get; }

        public StatsBar(string name, int count, double percent)
        {
            Name = name;
            Count = count;
            Percent = percent;
        }
    }
}
