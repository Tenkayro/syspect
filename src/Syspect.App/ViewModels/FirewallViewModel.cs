using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Syspect.App.Services;
using Syspect.Core.Models;

namespace Syspect.App.ViewModels;

public sealed class SelectableFirewallRuleViewModel : ObservableObject
{
    private bool _isChecked;

    public FirewallRuleItem Rule { get; }
    public bool IsChecked { get => _isChecked; set => SetProperty(ref _isChecked, value); }

    public SelectableFirewallRuleViewModel(FirewallRuleItem rule)
    {
        Rule = rule;
        _isChecked = rule.IsCleanupCandidate || rule.IsDownloadsPath;
    }
}

public sealed class FirewallViewModel : ObservableObject
{
    private readonly AppServices _services = AppServices.Current;

    private bool _isLoading;
    private int _selectedTabIndex;
    private FirewallAuditResult? _auditResult;
    private string _bannerMessage = string.Empty;
    private BannerSeverity _bannerSeverity = BannerSeverity.Info;
    private bool _isBannerOpen;

    public ObservableCollection<FirewallProfileStatus> Profiles { get; } = [];
    public ObservableCollection<SelectableFirewallRuleViewModel> OrphanedRules { get; } = [];
    public ObservableCollection<FirewallRuleItem> PublicActiveRules { get; } = [];

    public bool IsLoading { get => _isLoading; set => SetProperty(ref _isLoading, value); }

    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set
        {
            if (SetProperty(ref _selectedTabIndex, value))
            {
                OnPropertyChanged(nameof(IsOrphanedTab));
                OnPropertyChanged(nameof(IsPublicTab));
            }
        }
    }

    public bool IsOrphanedTab => SelectedTabIndex == 0;
    public bool IsPublicTab => SelectedTabIndex == 1;

    public FirewallAuditResult? AuditResult { get => _auditResult; private set => SetProperty(ref _auditResult, value); }
    public string BannerMessage { get => _bannerMessage; set => SetProperty(ref _bannerMessage, value); }
    public BannerSeverity BannerSeverity { get => _bannerSeverity; set => SetProperty(ref _bannerSeverity, value); }
    public bool IsBannerOpen { get => _isBannerOpen; set => SetProperty(ref _isBannerOpen, value); }

    public async Task LoadAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        try
        {
            var result = await _services.FirewallAudit.AuditAsync();
            AuditResult = result;

            Profiles.Clear();
            foreach (var p in result.Profiles) Profiles.Add(p);

            OrphanedRules.Clear();
            foreach (var r in result.OrphanedRules) OrphanedRules.Add(new SelectableFirewallRuleViewModel(r));

            PublicActiveRules.Clear();
            foreach (var r in result.PublicActiveDevRules) PublicActiveRules.Add(r);
        }
        catch (Exception ex)
        {
            ShowBanner($"Failed to audit firewall: {ex.Message}", BannerSeverity.Error);
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task DeleteSelectedOrphanedAsync()
    {
        var selected = OrphanedRules.Where(x => x.IsChecked).Select(x => x.Rule).ToList();
        if (selected.Count == 0)
        {
            ShowBanner("No rules selected.", BannerSeverity.Warning);
            return;
        }

        await DeleteRulesCoreAsync(selected);
    }

    public async Task DeleteCleanupCandidatesAsync()
    {
        var targets = OrphanedRules
            .Select(x => x.Rule)
            .Where(r => r.IsCleanupCandidate || r.IsDownloadsPath)
            .ToList();

        if (targets.Count == 0)
        {
            ShowBanner("No Downloads or legacy leftover rules remaining.", BannerSeverity.Info);
            return;
        }

        await DeleteRulesCoreAsync(targets);
    }

    private async Task DeleteRulesCoreAsync(IReadOnlyList<FirewallRuleItem> rules)
    {
        IsLoading = true;
        int okCount = 0;
        try
        {
            foreach (var rule in rules)
            {
                var (ok, _) = await _services.FirewallAudit.DeleteRuleAsync(rule);
                if (ok) okCount++;
            }
        }
        finally
        {
            IsLoading = false;
        }

        ShowBanner($"Removed {okCount} of {rules.Count} firewall rule(s).", okCount > 0 ? BannerSeverity.Success : BannerSeverity.Warning);
        await LoadAsync();
    }

    private void ShowBanner(string message, BannerSeverity severity)
    {
        BannerMessage = message;
        BannerSeverity = severity;
        IsBannerOpen = true;
    }
}
