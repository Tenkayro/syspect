using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Syspect.App.Services;
using Syspect.Core.Models;

namespace Syspect.App.ViewModels;

public sealed class DriversViewModel : ObservableObject
{
    private readonly AppServices _services = AppServices.Current;
    private List<DriverEntry> _allDrivers = [];

    private bool _isLoading;
    private string _searchText = string.Empty;
    private int _selectedFilterIndex;
    private int _problemCount;
    private int _hintCount;
    private int _totalCount;

    public ObservableCollection<DriverEntry> FilteredDrivers { get; } = [];

    public bool IsLoading { get => _isLoading; set => SetProperty(ref _isLoading, value); }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value)) ApplyFilter();
        }
    }

    public int SelectedFilterIndex
    {
        get => _selectedFilterIndex;
        set
        {
            if (SetProperty(ref _selectedFilterIndex, value)) ApplyFilter();
        }
    }

    public int ProblemCount { get => _problemCount; private set => SetProperty(ref _problemCount, value); }
    public int HintCount { get => _hintCount; private set => SetProperty(ref _hintCount, value); }
    public int TotalCount { get => _totalCount; private set => SetProperty(ref _totalCount, value); }

    public async Task LoadAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        try
        {
            var audit = await _services.DriverAudit.AuditAsync();
            _allDrivers = [.. audit.ProblemDevices, .. audit.KeyDrivers];
            ProblemCount = audit.ProblemDevices.Count;
            HintCount = audit.GenericHintCount;
            TotalCount = audit.KeyDrivers.Count;
            ApplyFilter();
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ApplyFilter()
    {
        FilteredDrivers.Clear();
        var query = SearchText.Trim();

        IEnumerable<DriverEntry> items = _allDrivers;
        if (SelectedFilterIndex == 1)
        {
            items = items.Where(d => d.HasAnyIssue);
        }
        else if (SelectedFilterIndex == 2)
        {
            items = items.Where(d => d.DeviceClass is "DISPLAY" or "NET" or "MEDIA" or "SCSIADAPTER" or "HDC" or "SECURITYDEVICES");
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            items = items.Where(d =>
                d.DeviceName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                d.DeviceClass.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                d.ProviderName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                d.InfName.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var d in items) FilteredDrivers.Add(d);
    }
}
