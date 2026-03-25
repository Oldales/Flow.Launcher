using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.DependencyInjection;
using Flow.Launcher.Core.Plugin;
using Flow.Launcher.Infrastructure.UserSettings;
using Flow.Launcher.ViewModel;

namespace Flow.Launcher;

public partial class PluginSettingsWindow
{
    private readonly Settings _settings;
    private readonly PluginDisplayModes _displayModes = new();

    public PluginSettingsWindow()
    {
        _settings = Ioc.Default.GetRequiredService<Settings>();
        InitializeComponent();
        PluginListBox.DataContext = _displayModes;
    }

    public PluginSettingsWindow(string pluginId)
        : this()
    {
        LoadPlugin(pluginId);
    }

    private void LoadPlugin(string pluginId)
    {
        if (string.IsNullOrWhiteSpace(pluginId))
        {
            App.API.ShowMsgError("Plugin settings", "Invalid plugin id.");
            return;
        }

        var pluginPair = PluginManager.GetPluginForId(pluginId);
        if (pluginPair == null)
        {
            App.API.ShowMsgError("Plugin settings", $"Unable to find plugin: {pluginId}");
            return;
        }

        var pluginSettings = _settings.PluginSettings.GetPluginSettings(pluginId);
        if (pluginSettings == null)
        {
            App.API.ShowMsgError("Plugin settings", $"Unable to load settings for plugin: {pluginPair.Metadata.Name}");
            return;
        }

        var pluginViewModel = new PluginViewModel
        {
            PluginPair = pluginPair,
            PluginSettingsObject = pluginSettings,
            IsExpanded = true,
        };

        PluginListBox.ItemsSource = new List<PluginViewModel> { pluginViewModel };
        PluginListBox.SelectedIndex = -1;
        Title = $"{pluginPair.Metadata.Name} Settings";
    }

    private void OnMinimizeButtonClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void OnMaximizeRestoreButtonClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState switch
        {
            WindowState.Maximized => WindowState.Normal,
            _ => WindowState.Maximized
        };
    }

    private void OnCloseButtonClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnCloseExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        Close();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshMaximizeRestoreButton();
    }

    private void Window_StateChanged(object sender, EventArgs e)
    {
        RefreshMaximizeRestoreButton();
    }

    private void RefreshMaximizeRestoreButton()
    {
        if (WindowState == WindowState.Maximized)
        {
            MaximizeButton.Visibility = Visibility.Hidden;
            RestoreButton.Visibility = Visibility.Visible;
        }
        else
        {
            MaximizeButton.Visibility = Visibility.Visible;
            RestoreButton.Visibility = Visibility.Hidden;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (!App.LoadingOrExiting)
        {
            _settings.Save();
            App.API.SavePluginSettings();
        }

        base.OnClosed(e);
    }

    private sealed class PluginDisplayModes
    {
        public bool IsOnOffSelected => true;
        public bool IsPrioritySelected => false;
        public bool IsSearchDelaySelected => false;
        public bool IsHomeOnOffSelected => false;
    }
}
