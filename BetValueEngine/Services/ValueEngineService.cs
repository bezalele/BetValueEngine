using BetValueEngine.Data;
using BetValueEngine.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace BetValueEngine.Services;

public class ValueEngineService
{
    private readonly BettingDbContext _db;

    public ValueEngineService(BettingDbContext db)
    {
        _db = db;
    }

    public async Task RunAsync()
    {
        Console.WriteLine("=== MarketConsensusML (relative edge) start ===");

        var run = new ModelRun
        {
            ModelName = "MarketConsensusML",
            RunType = "Live",
            RunStartedUtc = DateTime.UtcNow,
            Notes = "Edge = book decimal odds vs market avg (excluding itself)."
        };
        _db.ModelRuns.Add(run);
        await _db.SaveChangesAsync();

        var fromDate = DateTime.UtcNow.Date.AddDays(-1);

        var oddsList = await _db.GameOdds
            .Where(o => o.SnapshotTimeUtc >= fromDate)
            .ToListAsync();

        Console.WriteLine($"Total GameOdds loaded: {oddsList.Count}");

        var latestPerGameProvider = oddsList
            .GroupBy(o => new { o.GameId, o.OddsProviderId })
            .Select(g => g.OrderByDescending(x => x.SnapshotTimeUtc).First())
            .ToList();

        Console.WriteLine($"Distinct game/provider pairs: {latestPerGameProvider.Count}");

        var gamesGrouped = latestPerGameProvider
            .GroupBy(o => o.GameId)
            .ToList();

        int recCount = 0;

        foreach (var gameGroup in gamesGrouped)
        {
            // Build per-provider decimal odds
            var providerLines = new List<(GameOdds Odds, decimal? HomeDec, decimal? AwayDec)>();

            foreach (var odds in gameGroup)
            {
                decimal? homeDec = odds.HomeMoneyline.HasValue
                    ? AmericanToDecimal(odds.HomeMoneyline.Value)
                    : null;

                decimal? awayDec = odds.AwayMoneyline.HasValue
                    ? AmericanToDecimal(odds.AwayMoneyline.Value)
                    : null;

                providerLines.Add((odds, homeDec, awayDec));
            }

            foreach (var line in providerLines)
            {
                var odds = line.Odds;
                var others = providerLines
                    .Where(x => x.Odds.OddsProviderId != odds.OddsProviderId)
                    .ToList();

                // If only one book for that game, skip – no market to compare
                if (!others.Any())
                    continue;

                // HOME side
                if (line.HomeDec.HasValue)
                {
                    var marketHomeDec = others
                        .Where(x => x.HomeDec.HasValue)
                        .Select(x => x.HomeDec!.Value)
                        .DefaultIfEmpty()
                        .Average();

                    if (marketHomeDec > 0)
                    {
                        var bookDec = line.HomeDec.Value;
                        var edge = bookDec / marketHomeDec - 1m; // relative edge

                        // rough probs just for info
                        var modelProb = 1m / marketHomeDec;
                        var bookProb = 1m / bookDec;

                        var rec = new BetRecommendation
                        {
                            ModelRunId = run.ModelRunId,
                            GameId = odds.GameId,
                            OddsProviderId = odds.OddsProviderId,
                            BetType = "ML_HOME",
                            LineValue = null,
                            BookOdds = odds.HomeMoneyline ?? 0,
                            ModelProbability = modelProb,
                            ImpliedProbability = bookProb,
                            Edge = edge,
                            RiskLevel = ClassifyRisk(edge),
                            StakeFraction = null,
                            CreatedUtc = DateTime.UtcNow
                        };
                        _db.BetRecommendations.Add(rec);
                        recCount++;
                    }
                }

                // AWAY side
                if (line.AwayDec.HasValue)
                {
                    var marketAwayDec = others
                        .Where(x => x.AwayDec.HasValue)
                        .Select(x => x.AwayDec!.Value)
                        .DefaultIfEmpty()
                        .Average();

                    if (marketAwayDec > 0)
                    {
                        var bookDec = line.AwayDec.Value;
                        var edge = bookDec / marketAwayDec - 1m;

                        var modelProb = 1m / marketAwayDec;
                        var bookProb = 1m / bookDec;

                        var rec = new BetRecommendation
                        {
                            ModelRunId = run.ModelRunId,
                            GameId = odds.GameId,
                            OddsProviderId = odds.OddsProviderId,
                            BetType = "ML_AWAY",
                            LineValue = null,
                            BookOdds = odds.AwayMoneyline ?? 0,
                            ModelProbability = modelProb,
                            ImpliedProbability = bookProb,
                            Edge = edge,
                            RiskLevel = ClassifyRisk(edge),
                            StakeFraction = null,
                            CreatedUtc = DateTime.UtcNow
                        };
                        _db.BetRecommendations.Add(rec);
                        recCount++;
                    }
                }
            }
        }

        await _db.SaveChangesAsync();

        run.RunFinishedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        Console.WriteLine($"Inserted BetRecommendations: {recCount}");
        Console.WriteLine("=== MarketConsensusML (relative edge) end ===");
    }

    private static decimal AmericanToDecimal(int odds)
    {
        if (odds > 0)
            return 1m + odds / 100m;
        else
        {
            var abs = Math.Abs(odds);
            return 1m + 100m / abs;
        }
    }

    private static string ClassifyRisk(decimal edge)
    {
        if (edge >= 0.06m) return "High";      // 6% better than market
        if (edge >= 0.03m) return "Medium";    // 3–6%
        if (edge >= 0.01m) return "Low";       // 1–3%
        return "Negative";
    }
}
