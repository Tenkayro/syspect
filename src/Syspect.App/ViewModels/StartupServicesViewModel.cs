using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Syspect.App.Services;
using Syspect.Core.Models;
using Syspect.Core.Services;

namespace Syspect.App.ViewModels;

public abstract class IconItemViewModelBase : ObservableObject
{
    private ImageSource? _icon;

    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (SetProperty(ref _icon, value))
            {
                OnPropertyChanged(nameof(HasIcon));
                OnPropertyChanged(nameof(ShowFallbackGlyph));
            }
        }
    }

    public bool HasIcon => Icon is not null;
    public bool ShowFallbackGlyph => Icon is null;
    public abstract string FallbackGlyph { get; }
}

public sealed class StartupItemViewModel(StartupItem model) : IconItemViewModelBase
{
    public StartupItem Model { get; } = model;
    public string Name => Model.Name;
    public string Command => Model.Command;
    public string LocationLabel => Model.LocationLabel;
    public bool IsEnabled => Model.IsEnabled;
    public string StatusLabel => Model.StatusLabel;
    public bool CanToggle => Model.CanToggle;
    public string? ExecutablePath => Model.ExecutablePath;
    public string? PackageFamilyName => Model.PackageFamilyName;
    public override string FallbackGlyph => Model.Source == StartupSource.PackagedUwpApp ? "\uE71D" : "\uE756";
}

public sealed class ServiceEntryViewModel(ServiceEntry model) : IconItemViewModelBase
{
    public ServiceEntry Model { get; } = model;
    public string DisplayName => Model.DisplayName;
    public string Name => Model.Name;
    public string CategoryNote => Model.CategoryNote;
    public SafetyTier SafetyTier => Model.SafetyTier;
    public string SafetyBadge => Model.SafetyBadge;
    public string StartModeDisplay => Model.StartModeDisplay;
    public string State => Model.State;
    public bool CanModify => Model.CanModify;
    public bool IsRunning => Model.IsRunning;
    public string? ResolvedExecutablePath { get; } = StartupAndServiceAuditService.ResolveExecutablePath(model.ImagePath);
    public override string FallbackGlyph => "\uE713";
}

public sealed class ScheduledTaskViewModel(ScheduledTaskEntry model) : IconItemViewModelBase
{
    public ScheduledTaskEntry Model { get; } = model;
    public string FullPath => Model.FullPath;
    public string TaskName => Model.TaskName;
    public string Action => Model.Action;
    public string Note => Model.Note;
    public bool IsMissingTarget => Model.IsMissingTarget;
    public bool IsCleanupCandidate => Model.IsCleanupCandidate;
    public bool Enabled => Model.Enabled;
    public string StatusLabel => Model.StatusLabel;
    public string TargetPath => Model.TargetPath;
    public override string FallbackGlyph => Model.IsMissingTarget ? "\uE7BA" : "\uE823";
}

public sealed class StartupServicesViewModel : ObservableObject
{
    private readonly AppServices _services = AppServices.Current;

    private List<StartupItemViewModel> _allStartup = [];
    private List<ServiceEntryViewModel> _allServices = [];
    private List<ScheduledTaskViewModel> _allTasks = [];
    private CancellationTokenSource? _iconLoadCts;

    private bool _isLoading;
    private int _selectedTabIndex;
    private string _searchText = string.Empty;
    private bool _onlyActionable;
    private string _bannerMessage = string.Empty;
    private InfoBarSeverity _bannerSeverity = InfoBarSeverity.Informational;
    private bool _isBannerOpen;

    public ObservableCollection<StartupItemViewModel> FilteredStartup { get; } = [];
    public ObservableCollection<ServiceEntryViewModel> FilteredServices { get; } = [];
    public ObservableCollection<ScheduledTaskViewModel> FilteredTasks { get; } = [];

    public bool IsLoading { get => _isLoading; set => SetProperty(ref _isLoading, value); }

    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set
        {
            if (SetProperty(ref _selectedTabIndex, value))
            {
                OnPropertyChanged(nameof(IsStartupTab));
                OnPropertyChanged(nameof(IsServicesTab));
                OnPropertyChanged(nameof(IsTasksTab));
                ApplyFilters();
            }
        }
    }

    public bool IsStartupTab => SelectedTabIndex == 0;
    public bool IsServicesTab => SelectedTabIndex == 1;
    public bool IsTasksTab => SelectedTabIndex == 2;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value)) ApplyFilters();
        }
    }

    public bool OnlyActionable
    {
        get => _onlyActionable;
        set
        {
            if (SetProperty(ref _onlyActionable, value)) ApplyFilters();
        }
    }

    public string BannerMessage { get => _bannerMessage; set => SetProperty(ref _bannerMessage, value); }
    public InfoBarSeverity BannerSeverity { get => _bannerSeverity; set => SetProperty(ref _bannerSeverity, value); }
    public bool IsBannerOpen { get => _isBannerOpen; set => SetProperty(ref _isBannerOpen, value); }

    public async Task LoadAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        try
        {
            var t1 = _services.StartupAndServices.GetStartupItemsAsync();
            var t2 = _services.StartupAndServices.GetServicesAsync();
            var t3 = _services.StartupAndServices.GetScheduledTasksAsync();

            await Task.WhenAll(t1, t2, t3);

            _allStartup = t1.Result.Select(i => new StartupItemViewModel(i)).ToList();
            _allServices = t2.Result.Select(s => new ServiceEntryViewModel(s)).ToList();
            _allTasks = t3.Result.Select(t => new ScheduledTaskViewModel(t)).ToList();
            ApplyFilters();

            _ = LoadIconsAsync(_allStartup, _allServices, _allTasks);
        }
        catch (Exception ex)
        {
            ShowBanner($"Failed to load entries: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task ToggleStartupAsync(StartupItem item)
    {
        var (ok, msg) = await _services.StartupAndServices.SetStartupItemEnabledAsync(item, !item.IsEnabled);
        ShowBanner(msg, ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        if (ok)
        {
            _allStartup = (await _services.StartupAndServices.GetStartupItemsAsync())
                .Select(i => new StartupItemViewModel(i))
                .ToList();
            ApplyFilters();
            _ = LoadIconsAsync(startups: _allStartup);
        }
    }

    public async Task ChangeServiceStartModeAsync(ServiceEntry entry, string scMode)
    {
        var (ok, msg) = await _services.StartupAndServices.SetServiceStartModeAsync(entry.Name, scMode);
        ShowBanner(msg, ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        if (ok)
        {
            _allServices = (await _services.StartupAndServices.GetServicesAsync())
                .Select(s => new ServiceEntryViewModel(s))
                .ToList();
            ApplyFilters();
            _ = LoadIconsAsync(services: _allServices);
        }
    }

    public async Task ToggleServiceRunningAsync(ServiceEntry entry)
    {
        var (ok, msg) = await _services.StartupAndServices.SetServiceRunningStateAsync(entry.Name, !entry.IsRunning);
        ShowBanner(msg, ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        if (ok)
        {
            _allServices = (await _services.StartupAndServices.GetServicesAsync())
                .Select(s => new ServiceEntryViewModel(s))
                .ToList();
            ApplyFilters();
            _ = LoadIconsAsync(services: _allServices);
        }
    }

    public async Task ToggleTaskEnabledAsync(ScheduledTaskEntry task)
    {
        var (ok, msg) = await _services.StartupAndServices.SetTaskEnabledAsync(task.FullPath, !task.Enabled);
        ShowBanner(msg, ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        if (ok)
        {
            _allTasks = (await _services.StartupAndServices.GetScheduledTasksAsync())
                .Select(t => new ScheduledTaskViewModel(t))
                .ToList();
            ApplyFilters();
            _ = LoadIconsAsync(tasks: _allTasks);
        }
    }

    public async Task DeleteTaskAsync(ScheduledTaskEntry task)
    {
        var (ok, msg) = await _services.StartupAndServices.DeleteTaskAsync(task.FullPath);
        ShowBanner(msg, ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        if (ok)
        {
            _allTasks = (await _services.StartupAndServices.GetScheduledTasksAsync())
                .Select(t => new ScheduledTaskViewModel(t))
                .ToList();
            ApplyFilters();
            _ = LoadIconsAsync(tasks: _allTasks);
        }
    }

    private async Task LoadIconsAsync(
        IEnumerable<StartupItemViewModel>? startups = null,
        IEnumerable<ServiceEntryViewModel>? services = null,
        IEnumerable<ScheduledTaskViewModel>? tasks = null)
    {
        _iconLoadCts?.Cancel();
        _iconLoadCts?.Dispose();
        var cts = new CancellationTokenSource();
        _iconLoadCts = cts;

        var targets = new List<IconLoadTarget>();

        if (startups is not null)
        {
            foreach (var item in startups)
            {
                var key = !string.IsNullOrWhiteSpace(item.ExecutablePath)
                    ? $"file:{item.ExecutablePath}"
                    : !string.IsNullOrWhiteSpace(item.PackageFamilyName)
                        ? $"uwp:{item.PackageFamilyName}"
                        : string.Empty;

                if (!string.IsNullOrEmpty(key))
                {
                    var captured = item;
                    targets.Add(new IconLoadTarget(key, item.ExecutablePath, item.PackageFamilyName, bmp => captured.Icon = bmp));
                }
            }
        }

        if (services is not null)
        {
            foreach (var svc in services)
            {
                if (!string.IsNullOrWhiteSpace(svc.ResolvedExecutablePath))
                {
                    var captured = svc;
                    targets.Add(new IconLoadTarget(
                        $"file:{svc.ResolvedExecutablePath}",
                        svc.ResolvedExecutablePath,
                        null,
                        bmp => captured.Icon = bmp));
                }
            }
        }

        if (tasks is not null)
        {
            foreach (var task in tasks)
            {
                if (!task.IsMissingTarget && !string.IsNullOrWhiteSpace(task.TargetPath) && File.Exists(task.TargetPath))
                {
                    var captured = task;
                    targets.Add(new IconLoadTarget(
                        $"file:{task.TargetPath}",
                        task.TargetPath,
                        null,
                        bmp => captured.Icon = bmp));
                }
            }
        }

        try
        {
            await _services.Icons.PopulateIconsAsync(targets, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer load.
        }
    }

    private void ApplyFilters()
    {
        var q = SearchText.Trim();

        FilteredStartup.Clear();
        foreach (var item in _allStartup.Where(i =>
            (!OnlyActionable || i.IsEnabled) &&
            (string.IsNullOrWhiteSpace(q) || i.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || i.Command.Contains(q, StringComparison.OrdinalIgnoreCase))))
        {
            FilteredStartup.Add(item);
        }

        FilteredServices.Clear();
        foreach (var svc in _allServices.Where(s =>
            (!OnlyActionable || s.SafetyTier is SafetyTier.CleanupCandidate or SafetyTier.Caution || s.IsRunning) &&
            (string.IsNullOrWhiteSpace(q) || s.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) || s.Name.Contains(q, StringComparison.OrdinalIgnoreCase))))
        {
            FilteredServices.Add(svc);
        }

        FilteredTasks.Clear();
        foreach (var task in _allTasks.Where(t =>
            (!OnlyActionable || t.IsMissingTarget || t.IsCleanupCandidate || t.Enabled) &&
            (string.IsNullOrWhiteSpace(q) || t.TaskName.Contains(q, StringComparison.OrdinalIgnoreCase) || t.FullPath.Contains(q, StringComparison.OrdinalIgnoreCase))))
        {
            FilteredTasks.Add(task);
        }
    }

    private void ShowBanner(string message, InfoBarSeverity severity)
    {
        BannerMessage = message;
        BannerSeverity = severity;
        IsBannerOpen = true;
    }
}
