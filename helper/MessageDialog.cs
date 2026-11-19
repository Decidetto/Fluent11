using Playnite.SDK;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Fluent11Helper
{
    internal static class MessageDialog
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        public static void Apply(Window window)
        {
            try
            {
                if (!(window.Content is Grid body) || !(window.FindName("StackButtons") is StackPanel buttons))
                {
                    return;
                }

                if (window.FindName("ViewIcon") is UIElement icon)
                {
                    icon.Visibility = Visibility.Collapsed;
                }

                var hasTitle = !string.IsNullOrWhiteSpace(window.Title);
                var title = new TextBlock
                {
                    Text = window.Title,
                    FontWeight = FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(24, 24, 24, 4)
                };
                title.SetResourceReference(TextBlock.FontSizeProperty, "FontSizeLarger");
                title.SetResourceReference(TextBlock.FontFamilyProperty, "FontFamily");
                title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

                body.Children.Remove(buttons);
                buttons.HorizontalAlignment = HorizontalAlignment.Right;
                buttons.Margin = new Thickness(0);
                var first = true;
                foreach (var button in buttons.Children.OfType<Button>())
                {
                    button.MinWidth = 120;
                    button.Margin = new Thickness(first ? 0 : 8, 0, 0, 0);
                    first = false;
                }

                var footer = new Border { Padding = new Thickness(24, 20, 24, 20), BorderThickness = new Thickness(0, 1, 0, 0), Child = buttons };
                footer.SetResourceReference(Border.BackgroundProperty, "DialogFooterBrush");
                footer.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeBrush");

                body.Margin = new Thickness(14, hasTitle ? 0 : 14, 14, 16);

                var dock = new DockPanel { LastChildFill = true };
                DockPanel.SetDock(title, Dock.Top);
                DockPanel.SetDock(footer, Dock.Bottom);
                window.Content = null;
                if (hasTitle)
                {
                    dock.Children.Add(title);
                }

                dock.Children.Add(footer);
                dock.Children.Add(body);
                window.Content = dock;
                window.MinWidth = 420;
                SeamlessWindow.Apply(window);
            }
            catch (Exception e)
            {
                logger.Error(e, "Fluent 11: failed to restyle message box.");
            }
        }
    }
}
