namespace Syspect.Core.Models;

public enum SafetyTier
{
    CleanupCandidate,
    Standard,
    Caution,
    Protected
}

public sealed record ServiceEntry(
    string Name,
    string DisplayName,
    string StartMode,
    string State,
    string ImagePath,
    SafetyTier SafetyTier,
    string CategoryNote,
    bool IsMicrosoftBinary,
    bool CanModify)
{
    public bool IsRunning => string.Equals(State, "Running", StringComparison.OrdinalIgnoreCase);

    public string SafetyBadge => SafetyTier switch
    {
        SafetyTier.CleanupCandidate => "Cleanup Candidate",
        SafetyTier.Caution => "Caution",
        SafetyTier.Protected => "Protected",
        _ => "Standard"
    };

    public string StartModeDisplay => StartMode switch
    {
        "Auto" or "Automatic" => "Automatic",
        "DelayedAuto" => "Automatic (Delayed)",
        "Manual" => "Manual",
        "Disabled" => "Disabled",
        _ => StartMode
    };
}
