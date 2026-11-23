using Syspect.Core.Services;

namespace Syspect.App.Services;

/// <summary>
/// Central service registry for WPF views and ViewModels.
/// </summary>
public sealed class AppServices
{
    public static AppServices Current { get; } = new();

    public SystemInfoService SystemInfo { get; }
    public CbsLogParserService CbsParser { get; }
    public DriverAuditService DriverAudit { get; }
    public StartupAndServiceAuditService StartupAndServices { get; }
    public FirewallAuditService FirewallAudit { get; }
    public QuickCleanupService QuickCleanup { get; }
    public HealthRepairService HealthRepair { get; }
    public LogRepositoryService LogRepository { get; }
    public UpdateService Updates { get; }
    public AppIconService Icons { get; }

    public Action<Type, object?>? NavigateRequested { get; set; }

    private AppServices()
    {
        SystemInfo = new SystemInfoService();
        CbsParser = new CbsLogParserService();
        DriverAudit = new DriverAuditService();
        StartupAndServices = new StartupAndServiceAuditService();
        FirewallAudit = new FirewallAuditService();
        QuickCleanup = new QuickCleanupService(StartupAndServices, FirewallAudit);
        HealthRepair = new HealthRepairService(SystemInfo, QuickCleanup);
        LogRepository = new LogRepositoryService();
        Updates = new UpdateService();
        Icons = new AppIconService();
    }
}
