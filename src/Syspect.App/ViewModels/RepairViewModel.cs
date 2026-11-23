using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Syspect.App.Services;
using Syspect.Core.Models;
using Syspect.Core.Services;

namespace Syspect.App.ViewModels;

public sealed class RepairStepItemViewModel : ObservableObject
{
    private bool _isSelected;
    private RepairStepState _state = RepairStepState.Pending;
    private double? _stepPercent;
    private string _exitCodeText = "-";
    private string _durationText = "-";
    private string _summaryText;

    public RepairStepId Id { get; }
    public string Title { get; }
    public string Description { get; }

    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }

    public RepairStepState State
    {
        get => _state;
        set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(StateLabel));
            }
        }
    }

    public double? StepPercent
    {
        get => _stepPercent;
        set
        {
            if (SetProperty(ref _stepPercent, value))
            {
                OnPropertyChanged(nameof(StateLabel));
            }
        }
    }

    public string ExitCodeText { get => _exitCodeText; set => SetProperty(ref _exitCodeText, value); }
    public string DurationText { get => _durationText; set => SetProperty(ref _durationText, value); }
    public string SummaryText { get => _summaryText; set => SetProperty(ref _summaryText, value); }

    public string StateLabel => State switch
    {
        RepairStepState.Running => StepPercent.HasValue
            ? string.Create(CultureInfo.InvariantCulture, $"{StepPercent.Value:0.0}%")
            : "Running...",
        RepairStepState.Succeeded => "OK",
        RepairStepState.Warning => "Warning",
        RepairStepState.Failed => "Failed",
        RepairStepState.Skipped => "Skipped",
        _ => "Ready"
    };

    public RepairStepItemViewModel(RepairStepDefinition def)
    {
        Id = def.Id;
        Title = def.Title;
        Description = def.Description;
        _isSelected = def.DefaultSelected;
        _summaryText = def.Description;
    }
}

public sealed class RepairViewModel : ObservableObject
{
    private const int MaxConsoleDisplayChars = 150_000;

    private readonly AppServices _services = AppServices.Current;
    private readonly StringBuilder _consoleBuffer = new();
    private CancellationTokenSource? _cts;
    private DateTimeOffset _pipelineStartedAt;

    private bool _isRunning;
    private double _progressPercent;
    private string _currentStepText = "Ready to run.";
    private string _elapsedStatusText = string.Empty;
    private string _liveActivityText = string.Empty;
    private string _liveMetricsText = string.Empty;
    private bool _hasDownloadProgress;
    private double _downloadProgressPercent;
    private string _consoleText = "Click 'Run Smart Pipeline' to execute SSD, chkdsk /scan, DISM, and SFC diagnostics.";
    private string _lastSavedLogPath = string.Empty;
    private CbsRepairReport? _cbsReport;
    private string _bannerMessage = string.Empty;
    private BannerSeverity _bannerSeverity = BannerSeverity.Info;
    private bool _isBannerOpen;

    public ObservableCollection<RepairStepItemViewModel> Steps { get; } = new(
        HealthRepairService.GetDefaultSteps().Select(d => new RepairStepItemViewModel(d)));

    public bool IsRunning { get => _isRunning; set => SetProperty(ref _isRunning, value); }
    public double ProgressPercent { get => _progressPercent; set => SetProperty(ref _progressPercent, value); }
    public string CurrentStepText { get => _currentStepText; set => SetProperty(ref _currentStepText, value); }
    public string ElapsedStatusText { get => _elapsedStatusText; set => SetProperty(ref _elapsedStatusText, value); }
    public string LiveActivityText { get => _liveActivityText; set => SetProperty(ref _liveActivityText, value); }
    public string LiveMetricsText { get => _liveMetricsText; set => SetProperty(ref _liveMetricsText, value); }
    public bool HasDownloadProgress { get => _hasDownloadProgress; set => SetProperty(ref _hasDownloadProgress, value); }
    public double DownloadProgressPercent { get => _downloadProgressPercent; set => SetProperty(ref _downloadProgressPercent, value); }
    public string ConsoleText { get => _consoleText; set => SetProperty(ref _consoleText, value); }
    public string LastSavedLogPath { get => _lastSavedLogPath; set => SetProperty(ref _lastSavedLogPath, value); }
    public CbsRepairReport? CbsReport { get => _cbsReport; private set => SetProperty(ref _cbsReport, value); }

    public string BannerMessage { get => _bannerMessage; set => SetProperty(ref _bannerMessage, value); }
    public BannerSeverity BannerSeverity { get => _bannerSeverity; set => SetProperty(ref _bannerSeverity, value); }
    public bool IsBannerOpen { get => _isBannerOpen; set => SetProperty(ref _isBannerOpen, value); }

    public async Task LoadCbsSummaryAsync()
    {
        CbsReport = await _services.CbsParser.ParseAsync();
    }

    public async Task StartPipelineAsync(Dispatcher dispatcher, Action scrollConsoleToEnd)
    {
        if (IsRunning) return;

        var selected = Steps.Where(s => s.IsSelected).Select(s => s.Id).ToList();
        if (selected.Count == 0)
        {
            BannerMessage = "Select at least one repair step.";
            BannerSeverity = BannerSeverity.Warning;
            IsBannerOpen = true;
            return;
        }

        foreach (var step in Steps)
        {
            step.State = step.IsSelected ? RepairStepState.Pending : RepairStepState.Skipped;
            step.StepPercent = null;
            step.ExitCodeText = "-";
            step.DurationText = "-";
            step.SummaryText = step.Description;
        }

        _pipelineStartedAt = DateTimeOffset.UtcNow;
        _consoleBuffer.Clear();
        ConsoleText = string.Empty;
        ProgressPercent = 0;
        LastSavedLogPath = string.Empty;
        CurrentStepText = "Starting pipeline...";
        ElapsedStatusText = "Elapsed: 0s";
        LiveActivityText = "Preparing selected diagnostic steps...";
        LiveMetricsText = string.Empty;
        HasDownloadProgress = false;
        DownloadProgressPercent = 0;
        IsBannerOpen = false;
        IsRunning = true;

        _cts = new CancellationTokenSource();
        var progress = new Progress<RepairProgressUpdate>(update =>
        {
            _ = dispatcher.BeginInvoke(() =>
            {
                var activeItem = update.StepId.HasValue
                    ? Steps.FirstOrDefault(s => s.Id == update.StepId.Value)
                    : null;

                if (activeItem is not null && update.StepState.HasValue)
                {
                    activeItem.State = update.StepState.Value;
                    if (update.StepState.Value == RepairStepState.Running && update.Telemetry is null)
                    {
                        CurrentStepText = activeItem.StepPercent.HasValue
                            ? string.Create(CultureInfo.InvariantCulture, $"Running: {activeItem.Title} · {activeItem.StepPercent.Value:0.0}%")
                            : $"Running: {activeItem.Title}";
                    }
                }

                if (update.Telemetry is { } telemetry)
                {
                    var pipelineElapsed = DateTimeOffset.UtcNow - _pipelineStartedAt;
                    if (activeItem is not null)
                    {
                        if (telemetry.StepElapsed.HasValue)
                        {
                            activeItem.DurationText = RepairLiveMonitor.FormatDuration(telemetry.StepElapsed.Value);
                        }
                        if (telemetry.StepPercent.HasValue)
                        {
                            activeItem.StepPercent = telemetry.StepPercent.Value;
                        }
                        if (!string.IsNullOrWhiteSpace(telemetry.PhaseText))
                        {
                            activeItem.SummaryText = telemetry.PhaseText;
                        }

                        CurrentStepText = activeItem.StepPercent.HasValue
                            ? string.Create(CultureInfo.InvariantCulture, $"Running: {activeItem.Title} · {activeItem.StepPercent.Value:0.0}%")
                            : $"Running: {activeItem.Title}";
                    }

                    ElapsedStatusText = telemetry.StepElapsed.HasValue
                        ? $"Elapsed: {RepairLiveMonitor.FormatDuration(pipelineElapsed)} · Step: {RepairLiveMonitor.FormatDuration(telemetry.StepElapsed.Value)}"
                        : $"Elapsed: {RepairLiveMonitor.FormatDuration(pipelineElapsed)}";

                    if (telemetry.DownloadTotalBytes is > 0 && telemetry.DownloadReceivedBytes is >= 0)
                    {
                        double dlPct = Math.Clamp(telemetry.DownloadPercent ?? 0, 0, 100);
                        var etaPart = telemetry.DownloadEta.HasValue
                            ? $" · ETA {RepairLiveMonitor.FormatEta(telemetry.DownloadEta)}"
                            : string.Empty;
                        LiveActivityText = string.Create(
                            CultureInfo.InvariantCulture,
                            $"Downloading repair payload: {dlPct:0}% ({RepairLiveMonitor.FormatBytes(telemetry.DownloadReceivedBytes.Value)} / {RepairLiveMonitor.FormatBytes(telemetry.DownloadTotalBytes.Value)}){etaPart}");
                        HasDownloadProgress = dlPct < 100;
                        DownloadProgressPercent = dlPct;
                    }
                    else
                    {
                        HasDownloadProgress = false;
                        DownloadProgressPercent = 0;
                        LiveActivityText = !string.IsNullOrWhiteSpace(telemetry.PhaseText)
                            ? telemetry.PhaseText
                            : "Working in background...";
                    }

                    var metrics = new List<string>(2);
                    double? netBps = telemetry.DownloadBytesPerSec ?? telemetry.SystemNetworkDownBytesPerSec;
                    if (netBps is >= 1024)
                    {
                        metrics.Add($"↓ {RepairLiveMonitor.FormatSpeed(netBps.Value)}");
                    }
                    else if (telemetry.SystemNetworkDownBytesPerSec is >= 0)
                    {
                        metrics.Add("↓ 0.0 MB/s");
                    }

                    if (!string.IsNullOrWhiteSpace(telemetry.ActiveWorkerText))
                    {
                        metrics.Add(telemetry.ActiveWorkerText);
                    }

                    LiveMetricsText = string.Join(" · ", metrics);
                }

                if (update.Percent.HasValue)
                {
                    ProgressPercent = update.Percent.Value;
                }

                if (!string.IsNullOrEmpty(update.LogLine))
                {
                    _consoleBuffer.AppendLine(update.LogLine);
                    if (_consoleBuffer.Length > MaxConsoleDisplayChars)
                    {
                        int excess = _consoleBuffer.Length - (MaxConsoleDisplayChars - 10_000);
                        var current = _consoleBuffer.ToString();
                        int cutIdx = current.IndexOf('\n', Math.Clamp(excess, 0, Math.Max(0, current.Length - 1)));
                        int start = cutIdx >= 0 ? cutIdx + 1 : excess;
                        _consoleBuffer.Clear();
                        _consoleBuffer.Append("... [older console output trimmed; full log is saved to disk] ...\n");
                        _consoleBuffer.Append(current.AsSpan(start));
                    }

                    ConsoleText = _consoleBuffer.ToString();
                    scrollConsoleToEnd();
                }

                if (update.CompletedResult is not null)
                {
                    var item = Steps.FirstOrDefault(s => s.Id == update.CompletedResult.Id);
                    if (item is not null)
                    {
                        item.StepPercent = null;
                        item.State = update.CompletedResult.State;
                        item.ExitCodeText = update.CompletedResult.ExitCodeDisplay;
                        item.DurationText = update.CompletedResult.DurationDisplay;
                        item.SummaryText = update.CompletedResult.Summary;
                    }

                    HasDownloadProgress = false;
                    DownloadProgressPercent = 0;
                }

                if (!string.IsNullOrWhiteSpace(update.SavedLogPath))
                {
                    LastSavedLogPath = update.SavedLogPath;
                    ElapsedStatusText = update.SavedLogPath;
                }
            });
        });

        try
        {
            var results = await _services.HealthRepair.RunPipelineAsync(selected, progress, _cts.Token);
            bool anyError = results.Any(r => r.State == RepairStepState.Failed);
            CurrentStepText = anyError ? "Pipeline completed with warnings." : "All selected steps completed successfully.";
            BannerMessage = anyError
                ? "One or more steps exited with a non-zero code. Review the console log for details."
                : "Pipeline finished cleanly and log was saved.";
            BannerSeverity = anyError ? BannerSeverity.Warning : BannerSeverity.Success;
            IsBannerOpen = true;

            await LoadCbsSummaryAsync();
        }
        catch (Exception ex)
        {
            CurrentStepText = $"Error: {ex.Message}";
            BannerMessage = ex.Message;
            BannerSeverity = BannerSeverity.Error;
            IsBannerOpen = true;
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            HasDownloadProgress = false;
            DownloadProgressPercent = 0;
            LiveActivityText = string.Empty;
            LiveMetricsText = string.Empty;
            if (string.IsNullOrEmpty(LastSavedLogPath))
            {
                ElapsedStatusText = string.Empty;
            }
            IsRunning = false;
        }
    }

    public void CancelPipeline()
    {
        _cts?.Cancel();
        CurrentStepText = "Cancelling...";
    }

    public async Task ScheduleChkdskRebootAsync()
    {
        if (IsRunning) return;
        IsRunning = true;
        try
        {
            var (ok, msg) = await _services.HealthRepair.ScheduleChkdskOnRebootAsync();
            BannerMessage = ok
                ? "chkdsk C: /f scheduled for the next Windows reboot."
                : $"Could not schedule chkdsk: {msg}";
            BannerSeverity = ok ? BannerSeverity.Success : BannerSeverity.Warning;
            IsBannerOpen = true;
        }
        finally
        {
            IsRunning = false;
        }
    }
}
