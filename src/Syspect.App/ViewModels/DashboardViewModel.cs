using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Controls;
using Syspect.App.Services;
using Syspect.Core.Models;
using Syspect.Core.Services;

namespace Syspect.App.ViewModels;

public sealed class DashboardViewModel : ObservableObject
{
    private readonly AppServices _services = AppServices.Current;

    private bool _isLoading;
    private SystemOverview? _overview;
    private DiskHealthInfo? _primaryDisk;
    private CbsRepairReport? _cbsReport;
    private DriverAuditResult? _driverAudit;
    private FirewallAuditResult? _firewallAudit;
    private string _bannerMessage = string.Empty;
    private InfoBarSeverity _bannerSeverity = InfoBarSeverity.Informational;
    private bool _isBannerOpen;

    public bool IsLoading { get => _isLoading; set => SetProperty(ref _isLoading, value); }
    public SystemOverview? Overview { get => _overview; private set => SetProperty(ref _overview, value); }
    public DiskHealthInfo? PrimaryDisk { get => _primaryDisk; private set => SetProperty(ref _primaryDisk, value); }
    public CbsRepairReport? CbsReport { get => _cbsReport; private set => SetProperty(ref _cbsReport, value); }
    public DriverAuditResult? DriverAudit { get => _driverAudit; private set => SetProperty(ref _driverAudit, value); }
    public FirewallAuditResult? FirewallAudit { get => _firewallAudit; private set => SetProperty(ref _firewallAudit, value); }

    public string BannerMessage { get => _bannerMessage; set => SetProperty(ref _bannerMessage, value); }
    public InfoBarSeverity BannerSeverity { get => _bannerSeverity; set => SetProperty(ref _bannerSeverity, value); }
    public bool IsBannerOpen { get => _isBannerOpen; set => SetProperty(ref _isBannerOpen, value); }

    public string CurrentVersionDisplay => $"Check for Updates (v{UpdateService.GetCurrentVersion()})";

    public string SsdHeadline => PrimaryDisk is null
        ? "Loading..."
        : $"{PrimaryDisk.HealthStatus} · {PrimaryDisk.TemperatureDisplay}";

    public string SsdSubtext => PrimaryDisk is null
        ? "Reading physical storage telemetry"
        : $"{PrimaryDisk.FriendlyName} · Wear: {PrimaryDisk.WearDisplay} · {PrimaryDisk.CapacityDisplay}";

    public string WindowsHeadline => CbsReport is null
        ? "Loading..."
        : CbsReport.TotalRepairedCorruption > 0
            ? $"{CbsReport.TotalRepairedCorruption:N0} / {CbsReport.TotalDetectedCorruption:N0} repaired"
            : "Component Store Clean";

    public string WindowsSubtext => CbsReport is null
        ? "Analyzing CBS.log"
        : $"SFC: {CbsReport.LastSfcSummary}";

    public string DriverHeadline => DriverAudit is null
        ? "Loading..."
        : DriverAudit.ProblemDevices.Count == 0
            ? "0 Device Errors"
            : $"{DriverAudit.ProblemDevices.Count} Device Error(s)";

    public string DriverSubtext => DriverAudit is null
        ? "Scanning signed drivers"
        : $"{DriverAudit.KeyDrivers.Count} key drivers · {DriverAudit.GenericHintCount} hint(s)";

    public string FirewallHeadline => FirewallAudit is null
        ? "Loading..."
        : FirewallAudit.Profiles.All(p => p.Enabled)
            ? $"Secure ({FirewallAudit.Profiles.Count}/3 Profiles)"
            : "Attention Needed";

    public string FirewallSubtext => FirewallAudit is null
        ? "Auditing rules & profiles"
        : $"{FirewallAudit.NetworkCategory} · RDP: {(FirewallAudit.RdpEnabled ? "ON" : "OFF")} · {FirewallAudit.OrphanedRules.Count} orphaned";

    public async Task LoadAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        try
        {
            var sysTask = _services.SystemInfo.GetSystemOverviewAsync();
            var diskTask = _services.SystemInfo.GetDiskHealthAsync();
            var cbsTask = _services.CbsParser.ParseAsync();
            var drvTask = _services.DriverAudit.AuditAsync();
            var fwTask = _services.FirewallAudit.AuditAsync();

            await Task.WhenAll(sysTask, diskTask, cbsTask, drvTask, fwTask);

            Overview = sysTask.Result;
            PrimaryDisk = diskTask.Result.FirstOrDefault(d => d.DriveLetter == "C:") ?? diskTask.Result.FirstOrDefault();
            CbsReport = cbsTask.Result;
            DriverAudit = drvTask.Result;
            FirewallAudit = fwTask.Result;

            NotifyComputedCards();
        }
        catch (Exception ex)
        {
            ShowBanner($"Failed to load overview: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task RunOneClickCleanupAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        try
        {
            var summary = await _services.QuickCleanup.RunSafeOneClickCleanupAsync();
            ShowBanner(string.Join(" | ", summary.Details), InfoBarSeverity.Success);
            FirewallAudit = await _services.FirewallAudit.AuditAsync();
            OnPropertyChanged(nameof(FirewallHeadline));
            OnPropertyChanged(nameof(FirewallSubtext));
        }
        catch (Exception ex)
        {
            ShowBanner($"Cleanup failed: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task RestartNvidiaTrayAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        try
        {
            var (ok, msg) = await _services.QuickCleanup.RestartNvidiaTrayAsync();
            ShowBanner(msg, ok ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            ShowBanner($"Error: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void NotifyComputedCards()
    {
        OnPropertyChanged(nameof(SsdHeadline));
        OnPropertyChanged(nameof(SsdSubtext));
        OnPropertyChanged(nameof(WindowsHeadline));
        OnPropertyChanged(nameof(WindowsSubtext));
        OnPropertyChanged(nameof(DriverHeadline));
        OnPropertyChanged(nameof(DriverSubtext));
        OnPropertyChanged(nameof(FirewallHeadline));
        OnPropertyChanged(nameof(FirewallSubtext));
    }

    public async Task<UpdateCheckResult?> CheckForUpdatesAsync()
    {
        if (IsLoading)
        {
            return null;
        }

        IsLoading = true;
        try
        {
            var result = await _services.Updates.CheckForUpdatesAsync();
            var severity = result.Status switch
            {
                UpdateStatus.UpToDate => InfoBarSeverity.Success,
                UpdateStatus.Error => InfoBarSeverity.Warning,
                _ => InfoBarSeverity.Informational
            };
            ShowBanner(result.Message, severity);
            return result;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ShowBanner(string message, InfoBarSeverity severity)
    {
        BannerMessage = message;
        BannerSeverity = severity;
        IsBannerOpen = true;
    }
}
