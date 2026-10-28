using Avalonia.Controls;
using Avalonia.Interactivity;
using Scrappy.App.ViewModels;

namespace Scrappy.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
    }

    private SettingsViewModel? ViewModel => DataContext as SettingsViewModel;

    private void OnSaveProfile(object? sender, RoutedEventArgs e) => ViewModel?.SaveProfile();

    private void OnApplyProfile(object? sender, RoutedEventArgs e) => ViewModel?.ApplySelectedProfile();

    private void OnDeleteProfile(object? sender, RoutedEventArgs e) => ViewModel?.DeleteSelectedProfile();

    private void OnReset(object? sender, RoutedEventArgs e) => ViewModel?.ResetAll();
}
