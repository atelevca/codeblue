namespace HealthTech.Jobs
{
    public enum JobStatus
    {
        Pending,
        Running,
        Completed,
        Failed
    }

    /// <summary>Строка таблицы Jobs. Единственный источник правды о состоянии для UI.</summary>
    public record Job(
        Guid Id,
        string FileName,
        string ProfileKey,
        JobStatus Status,
        string? CurrentStep,
        int Percent,
        string? WorkflowId,
        string? Error,
        DateTimeOffset CreatedAt,
        DateTimeOffset? CompletedAt);
}
