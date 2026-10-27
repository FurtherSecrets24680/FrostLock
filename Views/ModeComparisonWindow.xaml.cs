using System.Windows;
using System.Windows.Input;
using FrostLock.Services;

namespace FrostLock.Views;

/// <summary>A native WPF modal comparison of the Strict and Hard input blockers.</summary>
public partial class ModeComparisonWindow : Wpf.Ui.Controls.FluentWindow
{
    public ModeComparisonWindow()
    {
        InitializeComponent();

        var icon = IconFactory.WindowIconSource();
        Icon = icon;
        if (icon is not null)
            DialogTitleBar.Icon = new Wpf.Ui.Controls.ImageIcon { Source = icon };

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
