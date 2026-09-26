namespace SemanticKernel.Minutes;

public enum MinutesStage
{
    Extracting,
    Consolidating,
    Verifying
}

/// <summary>Fragment <see cref="Current"/> of <see cref="Total"/> of a minutes stage; 1 of 1 for a short transcript.</summary>
public sealed record MinutesProgress(MinutesStage Stage, int Current, int Total);
