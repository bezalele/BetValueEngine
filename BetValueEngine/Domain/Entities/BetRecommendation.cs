namespace BetValueEngine.Domain.Entities;

public class BetRecommendation
{
    public long BetRecommendationId { get; set; }
    public long ModelRunId { get; set; }
    public long GameId { get; set; }
    public int OddsProviderId { get; set; }
    public string BetType { get; set; } = string.Empty;
    public decimal? LineValue { get; set; }
    public int BookOdds { get; set; }
    public decimal ModelProbability { get; set; }
    public decimal ImpliedProbability { get; set; }
    public decimal Edge { get; set; }
    public string? RiskLevel { get; set; }
    public decimal? StakeFraction { get; set; }
    public DateTime CreatedUtc { get; set; }
}
