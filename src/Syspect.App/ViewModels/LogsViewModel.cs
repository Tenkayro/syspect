using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Syspect.App.Services;
using Syspect.Core.Models;

namespace Syspect.App.ViewModels;

public sealed class LogsViewModel : ObservableObject
{
    private readonly AppServices _services = AppServices.Current;

    private bool _isLoading;
    private LogFileEntry? _selectedLog;
    private string _selectedLogContent = string.Empty;
    private CbsRepairReport? _cbsReport;

    public ObservableCollection<LogFileEntry> LogFiles { get; } = [];

    public bool IsLoading { get => _isLoading; set => SetProperty(ref _isLoading, value); }

    public LogFileEntry? SelectedLog
    {
        get => _selectedLog;
        set
        {
            if (SetProperty(ref _selectedLog, value) && value is not null)
            {
                _ = LoadSelectedContentAsync(value);
            }
        }
    }

    public string SelectedLogContent { get => _selectedLogContent; set => SetProperty(ref _selectedLogContent, value); }
    public CbsRepairReport? CbsReport { get => _cbsReport; private set => SetProperty(ref _cbsReport, value); }

    public async Task LoadAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        try
        {
            var filesTask = _services.LogRepository.GetLogFilesAsync();
            var cbsTask = _services.CbsParser.ParseAsync();
            await Task.WhenAll(filesTask, cbsTask);

            CbsReport = cbsTask.Result;
            LogFiles.Clear();

            LogFiles.Add(new LogFileEntry(
                FileName: "Windows CBS.log Analysis",
                FullPath: Core.Services.CbsLogParserService.DefaultCbsLogPath,
                SourceLabel: "Live Parser",
                LastModified: CbsReport.LastRepairTimestamp ?? DateTime.Now,
                SizeBytes: 0,
                IsCbsVirtualEntry: true));

            foreach (var f in filesTask.Result)
            {
                LogFiles.Add(f);
            }

            if (LogFiles.Count > 0)
            {
                SelectedLog = LogFiles.Count > 1 ? LogFiles[1] : LogFiles[0];
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task LoadSelectedContentAsync(LogFileEntry entry)
    {
        if (entry.IsCbsVirtualEntry)
        {
            CbsReport ??= await _services.CbsParser.ParseAsync();
            SelectedLogContent = CbsReport.RawSummaryText;
            return;
        }

        SelectedLogContent = await _services.LogRepository.ReadLogContentAsync(entry.FullPath);
    }
}
