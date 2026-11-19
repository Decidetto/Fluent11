using Playnite.SDK;
using Playnite.SDK.Data;
using Playnite.SDK.Plugins;
using System;
using System.Collections;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Threading;

namespace Fluent11Helper
{
    internal static class SettingsPage
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private const string PageFile = "Views\\Fluent11SettingsPage.xaml";
        private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(400);

        private static Fluent11HelperSettingsViewModel model;
        private static string themeDir;
        private static SidebarItem item;
        private static Control view;
        private static DispatcherTimer saveTimer;
        private static Fluent11HelperSettings watched;
        private static string savedJson;
        private static bool open;

        public static SidebarItem CreateSidebarItem(Fluent11HelperSettingsViewModel settingsModel, string theme)
        {
            model = settingsModel;
            themeDir = theme;
            item = item ?? new SidebarItem
            {
                Title = "Fluent 11",
                Type = SiderbarItemType.View,
                Visible = false,
                Opened = OnOpened,
                Closed = OnClosed
            };
            return item;
        }

        public static void Open()
        {
            if (item != null && view != null && open)
            {
                return;
            }

            if (!Activate())
            {
                API.Instance?.MainView?.OpenPluginSettings(Fluent11Helper.PluginId);
            }
        }

        private static bool Activate()
        {
            try
            {
                if (item == null || !(Reflect.GetMember(Reflect.GetMember(PlayniteInternals.App, "MainModel"), "SidebarItems") is IEnumerable entries))
                {
                    return false;
                }

                foreach (var entry in entries)
                {
                    if (ReferenceEquals(Reflect.GetMember(entry, "SideItem"), item) && Reflect.GetMember(entry, "Command") is ICommand command)
                    {
                        if (command.CanExecute(null))
                        {
                            command.Execute(null);
                        }

                        return true;
                    }
                }
            }
            catch (Exception e)
            {
                logger.Error(e, "Fluent 11: failed to open the settings page.");
            }

            return false;
        }

        private static Control OnOpened()
        {
            try
            {
                if (view == null)
                {
                    var host = PageTools.CreateHost();
                    using (var stream = File.OpenRead(Path.Combine(themeDir, PageFile)))
                    {
                        host.Content = XamlReader.Load(stream);
                    }

                    if ((host.Content as FrameworkElement)?.FindName("SettingsHost") is ContentControl slot)
                    {
                        slot.Content = new Fluent11HelperSettingsView();
                    }

                    host.DataContext = model;
                    view = host;
                    model.PropertyChanged += OnModelChanged;
                }

                model.BeginEdit();
                savedJson = SavedState();
                Watch(model.Settings);
                open = true;
                model.Settings.SettingsPageOpen = true;
                return view;
            }
            catch (Exception e)
            {
                logger.Error(e, "Fluent 11: failed to show the settings page.");
                return null;
            }
        }

        private static void OnClosed()
        {
            open = false;
            model.Settings.SettingsPageOpen = false;
            saveTimer?.Stop();
            SaveIfChanged();
            Watch(null);
        }

        private static void OnModelChanged(object sender, PropertyChangedEventArgs e)
        {
            if (open && e.PropertyName == nameof(Fluent11HelperSettingsViewModel.Settings))
            {
                Watch(model.Settings);
                model.Settings.SettingsPageOpen = true;
            }
        }

        private static void Watch(Fluent11HelperSettings settings)
        {
            if (watched != null)
            {
                watched.PropertyChanged -= OnSettingChanged;
            }

            watched = settings;
            if (watched != null)
            {
                watched.PropertyChanged += OnSettingChanged;
            }
        }

        private static void OnSettingChanged(object sender, PropertyChangedEventArgs e)
        {
            if (saveTimer == null)
            {
                saveTimer = new DispatcherTimer { Interval = SaveDelay };
                saveTimer.Tick += (s, a) =>
                {
                    saveTimer.Stop();
                    SaveIfChanged();
                };
            }

            saveTimer.Stop();
            saveTimer.Start();
        }

        public static void Restart()
        {
            try
            {
                saveTimer?.Stop();
                SaveIfChanged();
                var restart = PlayniteInternals.RestartCommand;
                if (restart != null && restart.CanExecute(null))
                {
                    restart.Execute(null);
                }
            }
            catch (Exception e)
            {
                logger.Error(e, "Fluent 11: failed to restart Playnite.");
            }
        }

        private static string SavedState()
        {
            return Serialization.ToJson(model.Settings) + "|" + model.Settings.ShowHltbEntry;
        }

        private static void SaveIfChanged()
        {
            try
            {
                var json = SavedState();
                if (json == savedJson)
                {
                    return;
                }

                savedJson = json;
                model.EndEdit(false);
                model.BeginEdit();
                savedJson = SavedState();
            }
            catch (Exception e)
            {
                logger.Error(e, "Fluent 11: failed to save settings from the settings page.");
            }
        }
    }
}
