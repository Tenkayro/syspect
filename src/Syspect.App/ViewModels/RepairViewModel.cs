using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Syspect.App.Services;
using Syspect.Core.Models;

namespace Syspect.App.ViewModels;

public sealed class RepairStepItemViewModel : ObservableObject
{
    private bool _isSelected;
    private RepairStepState _state = RepairStepState.Pending;
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

    public string ExitCodeText { get => _exitCodeText; set => SetProperty(ref _exitCodeText, value); }
    public string DurationText { get => _durationText; set => SetProperty(ref _durationText, value); }
    public string SummaryText { get => _summaryText; set => SetProperty(ref _summaryText, value); }

    public string StateLabel => State switch
    {
        RepairStepState.Running => "Running...",
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
    private readonly AppServices _services = AppServices.Current;
    private readonly StringBuilder _consoleBuffer = new();
    private CancellationTokenSource? _cts;

    private bool _isRunning;
    private double _progressPercent;
    private string _currentStepText = "Ready to run.";
    private string _consoleText = "Click 'Run Smart Pipeline' to execute SSD, chkdsk /scan, DISM, and SFC diagnostics.";
    private string _lastSavedLogPath = string.Empty;
    private CbsRepairReport? _cbsReport;
    private string _bannerMessage = string.Empty;
    private InfoBarSeverity _bannerSeverity = InfoBarSeverity.Informational;
    private bool _isBannerOpen;

    public ObservableCollection<RepairStepItemViewModel> Steps { get; } = new(
        Core.Services.HealthRepairService.GetDefaultSteps().Select(d => new RepairStepItemViewModel(d)));

    public bool IsRunning { get => _isRunning; set => SetProperty(ref _isRunning, value); }
    public double ProgressPercent { get => _progressPercent; set => SetProperty(ref _progressPercent, value); }
    public string CurrentStepText { get => _currentStepText; set => SetProperty(ref _currentStepText, value); }
    public string ConsoleText { get => _consoleText; set => SetProperty(ref _consoleText, value); }
    public string LastSavedLogPath { get => _lastSavedLogPath; set => SetProperty(ref _lastSavedLogPath, value); }
    public CbsRepairReport? CbsReport { get => _cbsReport; private set => SetProperty(ref _cbsReport, value); }

    public string BannerMessage { get => _bannerMessage; set => SetProperty(ref _bannerMessage, value); }
    public InfoBarSeverity BannerSeverity { get => _bannerSeverity; set => SetProperty(ref _bannerSeverity, value); }
    public bool IsBannerOpen { get => _isBannerOpen; set => SetProperty(ref _isBannerOpen, value); }

    public async Task LoadCbsSummaryAsync()
    {
        CbsReport = await _services.CbsParser.ParseAsync();
    }

    public async Task StartPipelineAsync(DispatcherQueue dispatcher, Action scrollConsoleToEnd)
    {
        if (IsRunning) return;

        var selected = Steps.Where(s => s.IsSelected).Select(s => s.Id).ToList();
        if (selected.Count == 0)
        {
            BannerMessage = "Select at least one repair step.";
            BannerSeverity = InfoBarSeverity.Warning;
            IsBannerOpen = true;
            return;
        }

        foreach (var step in Steps)
        {
            step.State = step.IsSelected ? RepairStepState.Pending : RepairStepState.Skipped;
            step.ExitCodeText = "-";
            step.DurationText = "-";
            step.SummaryText = step.Description;
        }

        _consoleBuffer.Clear();
        ConsoleText = string.Empty;
        ProgressPercent = 0;
        LastSavedLogPath = string.Empty;
        CurrentStepText = "Starting pipeline...";
        IsBannerOpen = false;
        IsRunning = true;

        _cts = new CancellationTokenSource();
        var progress = new Progress<RepairProgressUpdate>(update =>
        {
            dispatcher.TryEnqueue(() =>
            {
                if (update.StepId.HasValue)
                {
                    var item = Steps.FirstOrDefault(s => s.Id == update.StepId.Value);
                    if (item is not null && update.StepState.HasValue)
                    {
                        item.State = update.StepState.Value;
                        if (update.StepState.Value == RepairStepState.Running)
                        {
                            CurrentStepText = $"Running: {item.Title}";
                        }
                    }
                }

                if (update.Percent.HasValue)
                {
                    ProgressPercent = update.Percent.Value;
                }

                if (!string.IsNullOrEmpty(update.LogLine))
                {
                    _consoleBuffer.AppendLine(update.LogLine);
                    ConsoleText = _consoleBuffer.ToString();
                    scrollConsoleToEnd();
                }

                if (update.CompletedResult is not null)
                {
                    var item = Steps.FirstOrDefault(s => s.Id == update.CompletedResult.Id);
                    if (item is not null)
                    {
                        item.State = update.CompletedResult.State;
                        item.ExitCodeText = update.CompletedResult.ExitCodeDisplay;
                        item.DurationText = update.CompletedResult.DurationDisplay;
                        item.SummaryText = update.CompletedResult.Summary;
                    }
                }

                if (!string.IsNullOrWhiteSpace(update.SavedLogPath))
                {
                    LastSavedLogPath = update.SavedLogPath;
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
            BannerSeverity = anyError ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
            IsBannerOpen = true;

            await LoadCbsSummaryAsync();
        }
        catch (Exception ex)
        {
            CurrentStepText = $"Error: {ex.Message}";
            BannerMessage = ex.Message;
            BannerSeverity = InfoBarSeverity.Error;
            IsBannerOpen = true;
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
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
            BannerSeverity = ok ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
            IsBannerOpen = true;
        }
        finally
        {
            IsRunning = false;
        }
    }
}
