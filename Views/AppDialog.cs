using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FluentWindow = Wpf.Ui.Controls.FluentWindow;
using SymbolIcon = Wpf.Ui.Controls.SymbolIcon;
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;
using ControlAppearance = Wpf.Ui.Controls.ControlAppearance;

namespace FrostLock.Views;

/// <summary>Theme-aware replacement for the system MessageBox used throughout FrostLock.</summary>
internal static class AppDialog
{
    public static MessageBoxResult Show(
        Window? owner,
        string message,
        string title,
        MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage image = MessageBoxImage.None)
    {
        var dialog = new DialogWindow(message, title, buttons, image);
        if (owner is { IsLoaded: true }) dialog.Owner = owner;
        var result = dialog.ShowDialog();
        if (buttons == MessageBoxButton.OK) return result == true ? MessageBoxResult.OK : MessageBoxResult.None;
        return result == true ? MessageBoxResult.Yes : MessageBoxResult.No;
    }

    private sealed class DialogWindow : FluentWindow
    {
        public DialogWindow(string message, string title, MessageBoxButton buttons, MessageBoxImage image)
        {
            Title = title;
            Width = 500;
            MinHeight = 220;
            MaxHeight = 540;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            WindowCornerPreference = Wpf.Ui.Controls.WindowCornerPreference.Round;
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
            FontSize = 14;

            var layout = new Grid { Margin = new Thickness(24, 22, 24, 20) };
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var heading = new TextBlock
            {
                Text = title,
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 16)
            };
            heading.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
            layout.Children.Add(heading);

            var content = new Grid { Margin = new Thickness(0, 0, 0, 20) };
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var symbol = GetSymbol(image);
            var icon = new SymbolIcon
            {
                Symbol = symbol,
                FontSize = 28,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 1, 16, 0)
            };
            if (image == MessageBoxImage.Warning || image == MessageBoxImage.Error)
                icon.Foreground = new SolidColorBrush(image == MessageBoxImage.Warning
                    ? Color.FromRgb(232, 170, 54)
                    : Color.FromRgb(222, 82, 82));
            else
                icon.SetResourceReference(Control.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
            content.Children.Add(icon);

            var body = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 21,
                MaxWidth = 400,
                VerticalAlignment = VerticalAlignment.Center
            };
            body.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
            var scroll = new ScrollViewer
            {
                Content = body,
                MaxHeight = 300,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            Grid.SetColumn(scroll, 1);
            content.Children.Add(scroll);
            Grid.SetRow(content, 1);
            layout.Children.Add(content);

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            if (buttons == MessageBoxButton.YesNo)
            {
                actions.Children.Add(MakeButton("No", isPrimary: false, result: false));
                actions.Children.Add(MakeButton("Yes", isPrimary: true, result: true));
            }
            else
            {
                actions.Children.Add(MakeButton("OK", isPrimary: true, result: true));
            }
            Grid.SetRow(actions, 2);
            layout.Children.Add(actions);

            Content = new Border
            {
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1),
                Child = layout
            };
            ((Border)Content).SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
            ((Border)Content).SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
            Loaded += (_, _) => body.Focus();
            PreviewKeyDown += (_, e) =>
            {
                if (e.Key != System.Windows.Input.Key.Escape) return;
                DialogResult = false;
                e.Handled = true;
            };
        }

        private Button MakeButton(string label, bool isPrimary, bool result)
        {
            Button button = isPrimary
                ? new Wpf.Ui.Controls.Button { Appearance = ControlAppearance.Primary }
                : new Wpf.Ui.Controls.Button { Appearance = ControlAppearance.Secondary };
            button.Content = label;
            button.MinWidth = 94;
            button.MinHeight = 38;
            button.Margin = new Thickness(8, 0, 0, 0);
            button.Click += (_, _) =>
            {
                DialogResult = result;
            };
            return button;
        }

        private static SymbolRegular GetSymbol(MessageBoxImage image) => image switch
        {
            MessageBoxImage.Warning => SymbolRegular.Warning24,
            MessageBoxImage.Error => SymbolRegular.ErrorCircle24,
            MessageBoxImage.Question => SymbolRegular.QuestionCircle24,
            _ => SymbolRegular.Info24
        };
    }
}
