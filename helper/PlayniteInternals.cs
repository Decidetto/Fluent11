using Playnite.SDK;
using System;
using System.Collections;
using System.Reflection;
using System.Windows.Input;

namespace Fluent11Helper
{
    internal static class PlayniteInternals
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        public static object App
        {
            get
            {
                try
                {
                    var appType = Type.GetType("Playnite.PlayniteApplication, Playnite");
                    return appType?.GetProperty("Current", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                }
                catch (Exception e)
                {
                    logger.Error(e, "Fluent 11: Playnite application object not found.");
                    return null;
                }
            }
        }

        public static object AppSettings => Reflect.GetMember(App, "AppSettings");

        public static ICommand RestartCommand => Reflect.GetMember(Reflect.GetMember(App, "MainModel"), "RestartApp") as ICommand;

        public static string CurrentThemeDirectory
        {
            get
            {
                try
                {
                    var theme = Type.GetType("Playnite.ThemeManager, Playnite")?.GetProperty("CurrentTheme", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                    return Reflect.GetMember(theme, "DirectoryPath") as string;
                }
                catch (Exception e)
                {
                    logger.Error(e, "Fluent 11: current theme folder not found.");
                    return null;
                }
            }
        }

        public static object FindSettingsRoot(string sourceName)
        {
            try
            {
                var extensions = Reflect.GetMember(App, "Extensions");
                if (!(Reflect.GetMember(extensions, "SettingsSupportList") is IEnumerable list))
                {
                    return null;
                }

                foreach (var entry in list)
                {
                    if (Reflect.GetMember(entry, "SourceName") as string != sourceName)
                    {
                        continue;
                    }

                    var root = Reflect.GetMember(entry, "Source");
                    var path = Reflect.GetMember(entry, "SettingsRoot") as string ?? string.Empty;
                    foreach (var part in path.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        root = Reflect.GetMember(root, part);
                    }

                    return root;
                }
            }
            catch (Exception e)
            {
                logger.Error(e, $"Fluent 11: failed to find the data of {sourceName}.");
            }

            return null;
        }
    }
}
