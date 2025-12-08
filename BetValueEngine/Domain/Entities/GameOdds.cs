namespace BetValueEngine.Domain.Entities;

public class GameOdds
{
    public long GameOddsId { get; set; }
    public long GameId { get; set; }
    public int OddsProviderId { get; set; }
    public DateTime SnapshotTimeUtc { get; set; }
    public int? HomeMoneyline { get; set; }
    public int? AwayMoneyline { get; set; }
}
