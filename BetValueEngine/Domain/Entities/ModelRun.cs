namespace BetValueEngine.Domain.Entities;

public class ModelRun
{
    public long ModelRunId { get; set; }
    public string ModelName { get; set; } = string.Empty;
    public string RunType { get; set; } = string.Empty;
    public DateTime RunStartedUtc { get; set; }
    public DateTime? RunFinishedUtc { get; set; }
    public string? Notes { get; set; }
}
