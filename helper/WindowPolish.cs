using Playnite.SDK;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;

namespace Fluent11Helper
{
    internal static class WindowPolish
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        public static void Apply(Window window)
        {
            var name = window.GetType().Name;

            MarkPrimaryByName(window, "ButtonOK");
            if (name == "MetadataDownloadWindow" || name == "FirstTimeStartupWindow")
            {
                MarkPrimaryByName(window, "ButtonNext", "ButtonDownload", "ButtonFinish");
            }

            if (name == "UpdateWindow")
            {
                ApplyUpdateWindow(window);
                return;
            }

            if (name == "EmulatedGameImportWindow")
            {
                window.Dispatcher.BeginInvoke(new Action(() => MarkPrimary(window, "ScanCommmand", "ImportCommand")), DispatcherPriority.Loaded);
                return;
            }

            if (name == "ItemSelectionWithSearchWindow")
            {
                ApplySearchSelection(window);
                return;
            }

            if (name == "ImageSelectionWindow")
            {
                ApplyImageSelection(window);
                return;
            }

            if (name == "MetadataComparisonWindow")
            {
                window.Dispatcher.BeginInvoke(new Action(() => ApplyMetadataComparison(window)), DispatcherPriority.Loaded);
                return;
            }

            if (name == "DatabaseFieldsManagerWindow")
            {
                window.Dispatcher.BeginInvoke(new Action(() => ApplyLibraryManager(window)), DispatcherPriority.Loaded);
                return;
            }

            if (name != "SettingsWindow" && name != "AddonsWindow")
            {
                return;
            }

            window.Title = string.Empty;
            if (name == "AddonsWindow")
            {
                window.Dispatcher.BeginInvoke(new Action(() =>
                {
                    window.MinWidth = 800;
                    window.MinHeight = 620;
                }), DispatcherPriority.Normal);
                AddonUpdateBadge.Watch(window);
            }

            window.Dispatcher.BeginInvoke(new Action(() => Restyle(window)), DispatcherPriority.Loaded);
        }

        private static void Restyle(Window window)
        {
            try
            {
                if (!(window.Content is DockPanel dock))
                {
                    return;
                }

                var borders = dock.Children.OfType<Border>().ToList();
                var footer = borders.FirstOrDefault(a => DockPanel.GetDock(a) == Dock.Bottom);
                var nav = borders.FirstOrDefault(a => a.Child is TreeView);
                var content = borders.FirstOrDefault(a => a.Child is ContentControl);
                if (footer == null || nav == null || content == null)
                {
                    logger.Warn($"Fluent 11: unexpected layout of {window.GetType().Name}, left unchanged.");
                    return;
                }

                if (footer.Child is Panel bar)
                {
                    foreach (var button in bar.Children.OfType<Button>().Where(a => !a.IsCancel && !a.IsDefault && a.Tag == null))
                    {
                        button.Tag = "Fluent11Primary";
                    }
                }

                nav.BorderThickness = new Thickness(0);
                content.SetResourceReference(Border.BackgroundProperty, "ContentLayerBrush");
                content.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeBrush");
                content.BorderThickness = new Thickness(1, 1, 0, 0);
                content.CornerRadius = new CornerRadius(8, 0, 0, 0);

                var header = new TextBlock
                {
                    FontSize = 28,
                    FontWeight = FontWeights.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(20, 20, 20, 4)
                };
                header.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
                DockPanel.SetDock(header, Dock.Top);

                var page = content.Child;
                content.Child = null;
                var panel = new DockPanel();
                panel.Children.Add(header);
                panel.Children.Add(page);
                content.Child = panel;

                var tree = (TreeView)nav.Child;
                DisableSeparators(tree);
                tree.SelectedItemChanged += (s, e) => UpdateHeader(header, tree);
                UpdateHeader(header, tree);
            }
            catch (Exception e)
            {
                logger.Error(e, "Fluent 11: failed to restyle settings window.");
            }
        }

        private static void ApplyLibraryManager(Window window)
        {
            try
            {
                var tabs = (window.Content as Grid)?.Children.OfType<TabControl>().FirstOrDefault();
                if (tabs == null
                    || !(tabs.Template?.FindName("ContentPanel", tabs) is Border content)
                    || !(tabs.Template.FindName("HeaderBorder", tabs) is Border header))
                {
                    logger.Warn("Fluent 11: unexpected layout of the library manager, left unchanged.");
                    return;
                }

                header.Background = Brushes.Transparent;
                header.BorderThickness = new Thickness(0);
                content.SetResourceReference(Border.BackgroundProperty, "ContentLayerBrush");
                content.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeBrush");
                content.BorderThickness = new Thickness(1, 1, 0, 0);
                content.CornerRadius = new CornerRadius(8, 0, 0, 0);
            }
            catch (Exception e)
            {
                logger.Error(e, "Fluent 11: failed to restyle the library manager.");
            }
        }

        private static void ApplyUpdateWindow(Window window)
        {
            try
            {
                if (!(window.FindName("ButtonInstall") is Button install) || !(window.FindName("ButtonClose") is Button close)
                    || !(install.Parent is DockPanel bar) || !(bar.Parent is Border footer))
                {
                    logger.Warn("Fluent 11: unexpected layout of the update window, left unchanged.");
                    return;
                }

                if (install.Tag == null)
                {
                    install.Tag = "Fluent11Primary";
                }

                footer.SetResourceReference(Border.BackgroundProperty, "DialogFooterBrush");
                footer.Padding = new Thickness(24, 20, 24, 20);
                bar.Margin = new Thickness(0);
                close.Margin = new Thickness(0);
                install.Margin = new Thickness(0, 0, 8, 0);
            }
            catch (Exception e)
            {
                logger.Error(e, "Fluent 11: failed to restyle update window.");
            }
        }

        private const string SearchItemTemplate = @"
<DataTemplate xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"" xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml"">
    <StackPanel Background=""Transparent"" Margin=""0,3"">
        <TextBlock Text=""{Binding Name}"" FontWeight=""SemiBold"" TextWrapping=""Wrap"" />
        <TextBlock x:Name=""Desc"" Text=""{Binding Description}"" FontSize=""12"" Margin=""0,2,0,0"" TextWrapping=""Wrap""
                   Foreground=""{DynamicResource TextBrushDarker}"" />
        <StackPanel.InputBindings>
            <MouseBinding MouseAction=""LeftDoubleClick""
                          Command=""{Binding DataContext.ItemDoubleClickCommand, RelativeSource={RelativeSource AncestorType=Window}}"" />
        </StackPanel.InputBindings>
    </StackPanel>
    <DataTemplate.Triggers>
        <DataTrigger Binding=""{Binding Description}"" Value="""">
            <Setter TargetName=""Desc"" Property=""Visibility"" Value=""Collapsed"" />
        </DataTrigger>
        <DataTrigger Binding=""{Binding Description}"" Value=""{x:Null}"">
            <Setter TargetName=""Desc"" Property=""Visibility"" Value=""Collapsed"" />
        </DataTrigger>
    </DataTemplate.Triggers>
</DataTemplate>";

        internal const string SearchButtonStyle = @"
<Style TargetType=""Button"" xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"" xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml"">
    <Setter Property=""OverridesDefaultStyle"" Value=""True"" />
    <Setter Property=""FocusVisualStyle"" Value=""{x:Null}"" />
    <Setter Property=""Focusable"" Value=""False"" />
    <Setter Property=""Width"" Value=""28"" />
    <Setter Property=""Height"" Value=""24"" />
    <Setter Property=""Background"" Value=""Transparent"" />
    <Setter Property=""Foreground"" Value=""{DynamicResource TextBrushDarker}"" />
    <Setter Property=""Template"">
        <Setter.Value>
            <ControlTemplate TargetType=""Button"">
                <Border Background=""{TemplateBinding Background}"" CornerRadius=""{DynamicResource ControlCornerRadius}"">
                    <TextBlock Text=""&#xE721;"" FontFamily=""{DynamicResource FluentIconFont}"" FontSize=""13""
                               Foreground=""{TemplateBinding Foreground}"" HorizontalAlignment=""Center"" VerticalAlignment=""Center"" />
                </Border>
            </ControlTemplate>
        </Setter.Value>
    </Setter>
    <Style.Triggers>
        <Trigger Property=""IsMouseOver"" Value=""True"">
            <Setter Property=""Background"" Value=""{DynamicResource SubtleHoverBrush}"" />
            <Setter Property=""Foreground"" Value=""{DynamicResource TextBrush}"" />
        </Trigger>
        <Trigger Property=""IsPressed"" Value=""True"">
            <Setter Property=""Background"" Value=""{DynamicResource SubtlePressedBrush}"" />
        </Trigger>
    </Style.Triggers>
</Style>";

        private static void ApplySearchSelection(Window window)
        {
            try
            {
                if (!(window.FindName("ListSearch") is ListBox list) || !(window.FindName("TextSearch") is TextBox search)
                    || !(window.FindName("ButtonSearch") is Button searchButton) || !(window.FindName("ButtonOK") is Button ok)
                    || !(window.FindName("ButtonCancel") is Button cancel) || !(list.Parent is Grid grid) || grid.RowDefinitions.Count != 3
                    || !(search.Parent is DockPanel searchDock) || !(searchDock.Parent is Border searchBorder)
                    || !(ok.Parent is DockPanel footer) || footer.Parent != grid)
                {
                    logger.Warn("Fluent 11: unexpected layout of the item selection window, left unchanged.");
                    return;
                }

                window.MinWidth = 480;
                window.MinHeight = 400;

                var oldWidth = window.ActualWidth;
                window.Width = 640;
                if (oldWidth > 640 && !double.IsNaN(window.Left))
                {
                    window.Left += (oldWidth - 640) / 2;
                }

                grid.RowDefinitions[0].Height = GridLength.Auto;
                grid.RowDefinitions[1].Height = new GridLength(1, GridUnitType.Star);
                Grid.SetRow(searchBorder, 0);
                Grid.SetRow(list, 1);

                searchDock.Children.Clear();
                var searchHost = new Grid();
                search.Margin = new Thickness(0);
                search.Padding = new Thickness(search.Padding.Left, search.Padding.Top, 36, search.Padding.Bottom);
                searchButton.Style = (Style)XamlReader.Parse(SearchButtonStyle);
                searchButton.Padding = new Thickness(0);
                searchButton.Margin = new Thickness(0, 0, 4, 0);
                searchButton.HorizontalAlignment = HorizontalAlignment.Right;
                searchButton.VerticalAlignment = VerticalAlignment.Center;
                searchButton.ToolTip = searchButton.Content;
                searchButton.Content = null;
                searchHost.Children.Add(search);
                searchHost.Children.Add(searchButton);
                searchBorder.Child = searchHost;
                searchBorder.Padding = new Thickness(24, 16, 24, 12);

                list.Margin = new Thickness(12, 0, 12, 12);
                list.ItemTemplate = (DataTemplate)XamlReader.Parse(SearchItemTemplate);

                LayerAndFooter(grid, 2, footer, ok, cancel);
            }
            catch (Exception e)
            {
                logger.Error(e, "Fluent 11: failed to restyle item selection window.");
            }
        }

        private static void MarkPrimaryByName(Window window, params string[] names)
        {
            foreach (var name in names)
            {
                if (window.FindName(name) is Button button && button.Tag == null)
                {
                    button.Tag = "Fluent11Primary";
                }
            }
        }

        private static void MarkPrimary(DependencyObject root, params string[] commands)
        {
            foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            {
                if (child is Button button && button.Tag == null
                    && commands.Contains(BindingOperations.GetBinding(button, ButtonBase.CommandProperty)?.Path?.Path))
                {
                    button.Tag = "Fluent11Primary";
                }

                MarkPrimary(child, commands);
            }
        }

        private static void LayerAndFooter(Grid grid, int footerRow, FrameworkElement footer, Button ok, Button cancel)
        {
            grid.Margin = new Thickness(0);
            var layer = new Border();
            layer.SetResourceReference(Border.BackgroundProperty, "ContentLayerBrush");
            Grid.SetRowSpan(layer, footerRow);
            var line = new Border { BorderThickness = new Thickness(0, 1, 0, 0) };
            line.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeBrush");
            Grid.SetRow(line, footerRow);
            grid.Children.Insert(0, layer);
            grid.Children.Insert(1, line);

            footer.Margin = new Thickness(0);
            cancel.Margin = new Thickness(0, 20, 24, 20);
            ok.Margin = new Thickness(0, 20, 8, 20);
            if (ok.Tag == null)
            {
                ok.Tag = "Fluent11Primary";
            }
        }

        private const string ImageItemStyle = @"
<Style TargetType=""ListBoxItem"" xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"" xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml"">
    <Setter Property=""OverridesDefaultStyle"" Value=""True"" />
    <Setter Property=""FocusVisualStyle"" Value=""{x:Null}"" />
    <Setter Property=""Margin"" Value=""2"" />
    <Setter Property=""Padding"" Value=""4"" />
    <Setter Property=""VerticalAlignment"" Value=""Top"" />
    <Setter Property=""Template"">
        <Setter.Value>
            <ControlTemplate TargetType=""ListBoxItem"">
                <Border x:Name=""Bd"" CornerRadius=""8"" BorderThickness=""2"" BorderBrush=""Transparent"" Background=""Transparent""
                        Padding=""{TemplateBinding Padding}"">
                    <ContentPresenter />
                </Border>
                <ControlTemplate.Triggers>
                    <Trigger Property=""IsMouseOver"" Value=""True"">
                        <Setter TargetName=""Bd"" Property=""Background"" Value=""{DynamicResource SubtleHoverBrush}"" />
                    </Trigger>
                    <Trigger Property=""IsSelected"" Value=""True"">
                        <Setter TargetName=""Bd"" Property=""BorderBrush"" Value=""{DynamicResource AccentBrush}"" />
                    </Trigger>
                </ControlTemplate.Triggers>
            </ControlTemplate>
        </Setter.Value>
    </Setter>
</Style>";

        private const string ImageItemTemplate = @"
<DataTemplate xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"" xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml"">
    <StackPanel Background=""Transparent"">
        <Grid HorizontalAlignment=""Left"">
            <Image x:Name=""Img"" Source=""{Binding Path}"" Stretch=""Uniform"" Visibility=""Hidden""
                   MaxWidth=""{Binding DataContext.ItemWidth, RelativeSource={RelativeSource AncestorType=Window}}""
                   MaxHeight=""{Binding DataContext.ItemHeight, RelativeSource={RelativeSource AncestorType=Window}}"" />
            <Border Width=""{Binding ActualWidth, ElementName=Img}"" Height=""{Binding ActualHeight, ElementName=Img}""
                    CornerRadius=""{DynamicResource CardCornerRadius}"" RenderOptions.BitmapScalingMode=""Fant"">
                <Border.Background>
                    <ImageBrush ImageSource=""{Binding Source, ElementName=Img}"" Stretch=""Fill"" />
                </Border.Background>
            </Border>
        </Grid>
        <TextBlock x:Name=""Desc"" Text=""{Binding Description}"" FontSize=""12"" Margin=""2,6,2,0"" TextTrimming=""CharacterEllipsis""
                   Foreground=""{DynamicResource TextBrushDarker}"" />
        <StackPanel.InputBindings>
            <MouseBinding MouseAction=""LeftDoubleClick""
                          Command=""{Binding DataContext.ItemDoubleClickCommand, RelativeSource={RelativeSource AncestorType=Window}}"" />
        </StackPanel.InputBindings>
    </StackPanel>
    <DataTemplate.Triggers>
        <DataTrigger Binding=""{Binding Description}"" Value="""">
            <Setter TargetName=""Desc"" Property=""Visibility"" Value=""Collapsed"" />
        </DataTrigger>
        <DataTrigger Binding=""{Binding Description}"" Value=""{x:Null}"">
            <Setter TargetName=""Desc"" Property=""Visibility"" Value=""Collapsed"" />
        </DataTrigger>
    </DataTemplate.Triggers>
</DataTemplate>";

        private static void ApplyImageSelection(Window window)
        {
            try
            {
                if (!(window.FindName("ButtonOK") is Button ok) || !(window.FindName("ButtonCancel") is Button cancel)
                    || !(ok.Parent is DockPanel footer) || !(footer.Parent is Grid grid) || grid.RowDefinitions.Count != 2)
                {
                    logger.Warn("Fluent 11: unexpected layout of the image selection window, left unchanged.");
                    return;
                }

                var list = grid.Children.OfType<ListBox>().FirstOrDefault();
                if (list == null)
                {
                    logger.Warn("Fluent 11: unexpected layout of the image selection window, left unchanged.");
                    return;
                }

                window.MinWidth = 600;
                window.MinHeight = 480;
                list.Margin = new Thickness(18, 16, 18, 8);
                list.ItemContainerStyle = (Style)XamlReader.Parse(ImageItemStyle);
                list.ItemTemplate = (DataTemplate)XamlReader.Parse(ImageItemTemplate);
                LayerAndFooter(grid, 1, footer, ok, cancel);
            }
            catch (Exception e)
            {
                logger.Error(e, "Fluent 11: failed to restyle image selection window.");
            }
        }

        private static void ApplyMetadataComparison(Window window)
        {
            try
            {
                if (!(window.FindName("ButtonOK") is Button ok) || !(window.FindName("ButtonCancel") is Button cancel)
                    || !(ok.Parent is DockPanel bar) || !(bar.Parent is Border footer) || !(footer.Parent is DockPanel dock))
                {
                    logger.Warn("Fluent 11: unexpected layout of the metadata comparison window, left unchanged.");
                    return;
                }

                var scroll = dock.Children.OfType<ScrollViewer>().FirstOrDefault();
                if (scroll == null || !(scroll.Content is Grid table))
                {
                    logger.Warn("Fluent 11: unexpected layout of the metadata comparison window, left unchanged.");
                    return;
                }

                window.MinWidth = 640;
                window.MinHeight = 480;

                dock.Children.Remove(scroll);
                var layer = new Border { Child = scroll };
                layer.SetResourceReference(Border.BackgroundProperty, "ContentLayerBrush");
                dock.Children.Add(layer);

                table.Margin = new Thickness(19, 12, 19, 12);
                MergeHeaderRows(table);
                foreach (var text in table.Children.OfType<TextBlock>().Where(a => Grid.GetColumn(a) == 0))
                {
                    text.VerticalAlignment = VerticalAlignment.Top;
                    text.Margin = new Thickness(5, 8, 12, 5);
                }

                foreach (var radio in table.Children.OfType<RadioButton>())
                {
                    if (radio.Content is TextBlock text && text.TextWrapping == TextWrapping.Wrap)
                    {
                        radio.VerticalContentAlignment = VerticalAlignment.Top;
                        text.Margin = new Thickness(text.Margin.Left, 0, text.Margin.Right, text.Margin.Bottom);
                    }
                    else if (radio.Content is Panel)
                    {
                        radio.VerticalContentAlignment = VerticalAlignment.Top;
                    }
                }

                foreach (var image in table.Children.OfType<RadioButton>().Select(a => a.Content).OfType<Panel>()
                    .SelectMany(a => a.Children.OfType<Image>()).Where(a => a.Height > 64))
                {
                    image.SizeChanged += (s, e) => RoundCorners((Image)s);
                    RoundCorners(image);
                }

                footer.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeBrush");
                footer.Padding = new Thickness(24, 20, 24, 20);
                bar.Margin = new Thickness(0);
                cancel.Margin = new Thickness(0);
                ok.Margin = new Thickness(0, 0, 8, 0);
                if (ok.Tag == null)
                {
                    ok.Tag = "Fluent11Primary";
                }
            }
            catch (Exception e)
            {
                logger.Error(e, "Fluent 11: failed to restyle metadata comparison window.");
            }
        }

        private const string LinkButtonStyle = @"
<Style TargetType=""Button"" xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"">
    <Setter Property=""OverridesDefaultStyle"" Value=""True"" />
    <Setter Property=""FocusVisualStyle"" Value=""{DynamicResource FluentFocusVisual}"" />
    <Setter Property=""Background"" Value=""Transparent"" />
    <Setter Property=""Foreground"" Value=""{DynamicResource AccentBrush}"" />
    <Setter Property=""Padding"" Value=""8,4"" />
    <Setter Property=""Template"">
        <Setter.Value>
            <ControlTemplate TargetType=""Button"">
                <Border Background=""{TemplateBinding Background}"" Padding=""{TemplateBinding Padding}"" CornerRadius=""{DynamicResource ControlCornerRadius}"">
                    <ContentPresenter RecognizesAccessKey=""True"" />
                </Border>
            </ControlTemplate>
        </Setter.Value>
    </Setter>
    <Style.Triggers>
        <Trigger Property=""IsMouseOver"" Value=""True"">
            <Setter Property=""Background"" Value=""{DynamicResource SubtleHoverBrush}"" />
        </Trigger>
        <Trigger Property=""IsPressed"" Value=""True"">
            <Setter Property=""Background"" Value=""{DynamicResource SubtlePressedBrush}"" />
        </Trigger>
    </Style.Triggers>
</Style>";

        private static void MergeHeaderRows(Grid table)
        {
            var children = table.Children;
            if (children.Count < 6 || !(children[1] is TextBlock current) || !(children[2] is TextBlock added)
                || !(children[4] is Button selectCurrent) || !(children[5] is Button selectNew))
            {
                logger.Warn("Fluent 11: unexpected header of the metadata comparison window, left unchanged.");
                return;
            }

            var style = (Style)XamlReader.Parse(LinkButtonStyle);
            children.RemoveRange(1, 5);
            children.Insert(1, Header(current, selectCurrent, style));
            children.Insert(2, Header(added, selectNew, style));
            children.Insert(3, new Canvas());
            children.Insert(4, new Canvas());
            children.Insert(5, new Canvas());
        }

        private static StackPanel Header(TextBlock text, Button button, Style style)
        {
            text.FontWeight = FontWeights.SemiBold;
            button.Style = style;
            button.Margin = new Thickness(4, 0, 0, 0);
            button.VerticalAlignment = VerticalAlignment.Center;
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            panel.Children.Add(text);
            panel.Children.Add(button);
            return panel;
        }

        private static void RoundCorners(Image image)
        {
            var size = image.RenderSize;
            if (size.Width <= 0 || size.Height <= 0)
            {
                return;
            }

            var radius = Application.Current.TryFindResource("ControlCornerRadius") is CornerRadius corner ? corner.TopLeft : 4;
            var clip = new RectangleGeometry(new Rect(size), radius, radius);
            clip.Freeze();
            image.Clip = clip;
        }

        private static string fullscreenNote;

        public static bool TryRestyleFullscreenNote(TextBlock text)
        {
            if (fullscreenNote == null)
            {
                fullscreenNote = Application.Current?.TryFindResource("LOCFullscreenSettingsDesktopNotif") as string ?? string.Empty;
            }

            if (fullscreenNote.Length == 0 || text.Text != fullscreenNote || !(text.Parent is Border border))
            {
                return false;
            }

            try
            {
                border.BorderThickness = new Thickness(1);
                border.SetResourceReference(Border.CornerRadiusProperty, "ControlCornerRadius");
                border.SetResourceReference(Border.BackgroundProperty, "SurfaceCardBrush");
                border.SetResourceReference(Border.BorderBrushProperty, "CardStrokeBrush");
                border.Padding = new Thickness(16, 12, 16, 12);

                var icon = new TextBlock { Text = "\uE946", FontSize = 16, Margin = new Thickness(0, 2, 12, 0), VerticalAlignment = VerticalAlignment.Top };
                icon.SetResourceReference(TextBlock.FontFamilyProperty, "FluentIconFont");
                icon.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
                DockPanel.SetDock(icon, Dock.Left);

                border.Child = null;
                text.Margin = new Thickness(0);
                var row = new DockPanel();
                row.Children.Add(icon);
                row.Children.Add(text);
                border.Child = row;
                return true;
            }
            catch (Exception e)
            {
                logger.Error(e, "Fluent 11: failed to restyle the fullscreen settings note.");
                return false;
            }
        }

        private static void UpdateHeader(TextBlock header, TreeView tree)
        {
            header.Inlines.Clear();
            var item = Container(tree, tree.SelectedItem);
            var current = Text(item);
            if (string.IsNullOrEmpty(current))
            {
                header.Visibility = Visibility.Collapsed;
                return;
            }

            header.Visibility = Visibility.Visible;
            var parent = Text(ItemsControl.ItemsControlFromItemContainer(item) as TreeViewItem);
            if (!string.IsNullOrEmpty(parent))
            {
                var parentRun = new Run(parent);
                parentRun.SetResourceReference(TextElement.ForegroundProperty, "TextBrushDarker");
                header.Inlines.Add(parentRun);
                var chevron = new Run("    ") { FontSize = 16, BaselineAlignment = BaselineAlignment.Center };
                chevron.SetResourceReference(TextElement.FontFamilyProperty, "FluentIconFont");
                chevron.SetResourceReference(TextElement.ForegroundProperty, "TextBrushDarker");
                header.Inlines.Add(chevron);
            }

            header.Inlines.Add(new Run(current));
        }

        private static void DisableSeparators(TreeView tree)
        {
            foreach (var item in tree.Items)
            {
                var container = item as TreeViewItem ?? tree.ItemContainerGenerator.ContainerFromItem(item) as TreeViewItem;
                if (container != null && (item is Separator || container.Header is Separator))
                {
                    container.Focusable = false;
                    container.IsHitTestVisible = false;
                }
            }
        }

        private static TreeViewItem Container(ItemsControl parent, object item)
        {
            if (item == null)
            {
                return null;
            }

            if (item is TreeViewItem direct)
            {
                return direct;
            }

            if (parent.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem found)
            {
                return found;
            }

            foreach (var child in parent.Items.Cast<object>()
                .Select(a => a as TreeViewItem ?? parent.ItemContainerGenerator.ContainerFromItem(a) as TreeViewItem)
                .Where(a => a != null))
            {
                var nested = Container(child, item);
                if (nested != null)
                {
                    return nested;
                }
            }

            return null;
        }

        private static string Text(TreeViewItem item)
        {
            if (item == null)
            {
                return null;
            }

            var header = item.Header;
            if (header is Separator)
            {
                return null;
            }

            if (header is string text)
            {
                return string.IsNullOrEmpty(item.HeaderStringFormat) ? text : string.Format(item.HeaderStringFormat, text);
            }

            if (header != null && !string.IsNullOrEmpty(item.HeaderStringFormat))
            {
                return string.Format(item.HeaderStringFormat, header);
            }

            var path = (ItemsControl.ItemsControlFromItemContainer(item) as ItemsControl)?.DisplayMemberPath;
            object value = header;
            foreach (var part in (path ?? string.Empty).Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries))
            {
                value = Reflect.GetMember(value, part);
            }

            return value as string ?? value?.ToString();
        }
    }
}
