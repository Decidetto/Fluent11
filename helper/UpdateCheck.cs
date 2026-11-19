using Playnite.SDK;
using Playnite.SDK.Data;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using System.Windows;

namespace Fluent11Helper
{
    internal static class UpdateCheck
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        private const string ApiUrl = "https://api.github.com/repos/Arstidir/Fluent11/releases/latest";
        private const string ReleasesUrl = "https://github.com/Arstidir/Fluent11/releases/latest";
        private const string MessageId = "Fluent11Update";
        private static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(10);
        private const int TimeoutMs = 10000;

        public static void Run(IPlayniteAPI api)
        {
            Task.Run(async () =>
            {
                await Task.Delay(StartDelay);
                Check(api);
            });
        }

        private static void Check(IPlayniteAPI api)
        {
            try
            {
                var installed = Normalize(typeof(UpdateCheck).Assembly.GetName().Version);
                var request = (HttpWebRequest)WebRequest.Create(ApiUrl);
                request.UserAgent = "Fluent11Helper";
                request.Accept = "application/vnd.github+json";
                request.Timeout = TimeoutMs;
                request.ReadWriteTimeout = TimeoutMs;

                string json;
                using (var response = request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream()))
                {
                    json = reader.ReadToEnd();
                }

                var release = Serialization.FromJson<Dictionary<string, object>>(json);
                var tag = release != null && release.TryGetValue("tag_name", out var tagValue) ? tagValue as string : null;
                if (!Version.TryParse(tag?.TrimStart('v', 'V'), out var parsed))
                {
                    logger.Warn($"Fluent 11 update check: release tag '{tag}' is not a version.");
                    return;
                }

                var latest = Normalize(parsed);
                logger.Info($"Fluent 11 update check: latest {latest}, installed {installed}.");
                if (latest <= installed)
                {
                    return;
                }

                var url = release.TryGetValue("html_url", out var urlValue) && urlValue is string link && link.StartsWith("https://github.com/")
                    ? link
                    : ReleasesUrl;
                var text = $"Fluent 11 {latest} is available (installed: {installed}). Click to open the release page.";
                Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                    api.Notifications.Add(new NotificationMessage(MessageId, text, NotificationType.Info, () => Open(url)))));
            }
            catch (Exception e)
            {
                logger.Warn($"Fluent 11 update check failed: {e.Message}");
            }
        }

        private static Version Normalize(Version version)
        {
            return new Version(version.Major, version.Minor, Math.Max(version.Build, 0));
        }

        private static void Open(string url)
        {
            try
            {
                Process.Start(url);
            }
            catch (Exception e)
            {
                logger.Error(e, "Fluent 11: failed to open the release page.");
            }
        }
    }
}
