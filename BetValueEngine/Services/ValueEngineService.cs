using SmartSportsBetting.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using SmartSportsBetting.Domain.Entities;

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
        Console.WriteLine("[ValueEngine] Starting value generation (ELO moneyline)…");

        // 1) NBA league + MONEYLINE market type
        var league = await _db.Leagues
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Code == "NBA");

        if (league == null)
        {
            Console.WriteLine("[ValueEngine] League 'NBA' not found. Exiting.");
            return;
        }

        var moneylineType = await _db.MarketTypes
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Code == "MONEYLINE");

        if (moneylineType == null)
        {
            Console.WriteLine("[ValueEngine] MarketType 'MONEYLINE' not found. Exiting.");
            return;
        }

        // Guess current season from most recent NBA game
        var season = await _db.Games
            .Where(g => g.LeagueId == league.LeagueId)
            .OrderByDescending(g => g.StartTimeUtc)
            .Select(g => g.Season)
            .FirstOrDefaultAsync() ?? "2024-2025";

        // 2) Load team ratings (ELO). If missing, fall back to 1500.
        var ratings = await _db.TeamRatings
            .Where(tr => tr.Season == season)
            .ToDictionaryAsync(tr => tr.TeamId, tr => tr.Rating);

        Console.WriteLine($"[ValueEngine] Loaded {ratings.Count} team ratings for season {season}.");

        // 3) Ensure a Model + ModelRun entry
        var model = await _db.Models
            .FirstOrDefaultAsync(m =>
                m.ModelTypeCode == "MONEYLINE" &&
                m.Name == "ELO_RATINGS" &&
                m.Version == "v1");

        if (model == null)
        {
            model = new Model
            {
                Name = "ELO_RATINGS",
                Version = "v1",
                ModelTypeCode = "MONEYLINE",
                Description = "Elo-based win probabilities using GameResult history",
                IsActive = true
            };
            _db.Models.Add(model);
            await _db.SaveChangesAsync();
        }

        var run = new ModelRun
        {
            ModelId = model.ModelId,
            RunType = "Live",
            FromDateUtc = null,
            ToDateUtc = null,
            StartedUtc = DateTime.UtcNow,
            ParametersJson = "{\"source\":\"ELO\",\"marketType\":\"MONEYLINE\"}"
        };
        _db.ModelRuns.Add(run);
        await _db.SaveChangesAsync();

        // 4) Deactivate existing MONEYLINE recs for today
        var todayUtc = DateTime.UtcNow.Date;

        var existing = await _db.BetRecommendations
            .Where(br => br.MarketTypeId == moneylineType.MarketTypeId &&
                         br.CreatedUtc.Date == todayUtc)
            .ToListAsync();

        foreach (var br in existing)
            br.IsActive = false;

        // 5) Pull all moneyline odds snapshots for NBA games
        var fromDate = todayUtc.AddDays(-1);   // small safety window

        var snapshots = await _db.OddsSnapshots
            .Include(os => os.MarketOutcome!)
                .ThenInclude(mo => mo.Market!)
                    .ThenInclude(m => m.Game!)
            .Where(os =>
                os.MarketOutcome != null &&
                os.MarketOutcome.Market != null &&
                os.MarketOutcome.Market.Game != null &&
                os.MarketOutcome.Market.Game.LeagueId == league.LeagueId &&
                os.MarketOutcome.Market.MarketTypeId == moneylineType.MarketTypeId &&
                os.SnapshotTimeUtc >= fromDate)
            .ToListAsync();

        Console.WriteLine($"[ValueEngine] Loaded {snapshots.Count} moneyline snapshots.");

        if (!snapshots.Any())
        {
            await _db.SaveChangesAsync();
            Console.WriteLine("[ValueEngine] No snapshots. Exiting.");
            return;
        }

        // Group by game
        var gamesGrouped = snapshots
            .GroupBy(s => s.MarketOutcome!.Market!.Game.GameId)
            .ToList();

        int recCount = 0;

        foreach (var gameGroup in gamesGrouped)
        {
            var anySnap = gameGroup.First();
            var game = anySnap.MarketOutcome!.Market!.Game!;
            long gameId = game.GameId;

            // 5a) Get ELO ratings for home/away
            decimal homeRating = ratings.TryGetValue(game.HomeTeamId, out var hr) ? hr : 1500m;
            decimal awayRating = ratings.TryGetValue(game.AwayTeamId, out var ar) ? ar : 1500m;

            // Elo logistic
            double diff = (double)(homeRating - awayRating);
            double pHomeDouble = 1.0 / (1.0 + Math.Pow(10.0, -diff / 400.0));
            var modelHomeProb = (decimal)pHomeDouble;
            var modelAwayProb = 1m - modelHomeProb;

            if (modelHomeProb <= 0m || modelAwayProb <= 0m)
                continue;

            var fairHomeDec = 1m / modelHomeProb;
            var fairAwayDec = 1m / modelAwayProb;

            // 5b) Build provider-level lines (latest odds HOME/AWAY per provider)
            var providerLines = new List<ProviderLine>();

            var latestPerProviderOutcome = gameGroup
                .GroupBy(s => new
                {
                    s.ProviderId,
                    s.MarketOutcome!.OutcomeCode
                })
                .Select(g => g.OrderByDescending(x => x.SnapshotTimeUtc).First())
                .ToList();

            foreach (var providerGroup in latestPerProviderOutcome.GroupBy(x => x.ProviderId))
            {
                long providerId = providerGroup.Key;

                int? homeAmerican = null;
                decimal? homeDec = null;
                int homeOutcomeId = 0;

                int? awayAmerican = null;
                decimal? awayDec = null;
                int awayOutcomeId = 0;

                foreach (var snap in providerGroup)
                {
                    var outcome = snap.MarketOutcome!;
                    var code = outcome.OutcomeCode.ToUpperInvariant();

                    decimal? dec = snap.DecimalOdds;
                    if (!dec.HasValue)
                        dec = AmericanToDecimal(snap.AmericanOdds);

                    if (code == "HOME")
                    {
                        homeDec = dec;
                        homeAmerican = snap.AmericanOdds;
                        homeOutcomeId = (int)outcome.MarketOutcomeId;
                    }
                    else if (code == "AWAY")
                    {
                        awayDec = dec;
                        awayAmerican = snap.AmericanOdds;
                        awayOutcomeId = (int)outcome.MarketOutcomeId;
                    }
                }

                if (homeDec.HasValue || awayDec.HasValue)
                {
                    providerLines.Add(new ProviderLine
                    {
                        ProviderId = providerId,
                        GameId = gameId,
                        HomeDec = homeDec,
                        HomeAmerican = homeAmerican,
                        HomeOutcomeId = homeOutcomeId,
                        AwayDec = awayDec,
                        AwayAmerican = awayAmerican,
                        AwayOutcomeId = awayOutcomeId
                    });
                }
            }

            if (!providerLines.Any())
                continue;

            // 5c) Create model predictions (one per side, game-wide)
            int canonicalHomeOutcomeId =
                providerLines.FirstOrDefault(pl => pl.HomeOutcomeId != 0)?.HomeOutcomeId ?? 0;
            int canonicalAwayOutcomeId =
                providerLines.FirstOrDefault(pl => pl.AwayOutcomeId != 0)?.AwayOutcomeId ?? 0;

            ModelPrediction? homePrediction = null;
            ModelPrediction? awayPrediction = null;

            if (canonicalHomeOutcomeId != 0)
            {
                homePrediction = new ModelPrediction
                {
                    ModelRun = run,
                    MarketOutcomeId = canonicalHomeOutcomeId,
                    ProviderId = null,   // provider-agnostic model
                    WinProbability = modelHomeProb,
                    FairDecimalOdds = fairHomeDec,
                    FairAmericanOdds = DecimalToAmerican(fairHomeDec),
                    CreatedUtc = DateTime.UtcNow
                };
                _db.ModelPredictions.Add(homePrediction);
            }

            if (canonicalAwayOutcomeId != 0)
            {
                awayPrediction = new ModelPrediction
                {
                    ModelRun = run,
                    MarketOutcomeId = canonicalAwayOutcomeId,
                    ProviderId = null,
                    WinProbability = modelAwayProb,
                    FairDecimalOdds = fairAwayDec,
                    FairAmericanOdds = DecimalToAmerican(fairAwayDec),
                    CreatedUtc = DateTime.UtcNow
                };
                _db.ModelPredictions.Add(awayPrediction);
            }

            // 5d) Create bet recommendations per provider using model probabilities
            foreach (var line in providerLines)
            {
                // HOME side
                if (homePrediction != null && line.HomeDec.HasValue && line.HomeOutcomeId != 0)
                {
                    var bookDec = line.HomeDec.Value;
                    if (bookDec > 1m && fairHomeDec > 1m)
                    {
                        var edge = bookDec / fairHomeDec - 1m;
                        var bookProb = 1m / bookDec;
                        var american = line.HomeAmerican ?? DecimalToAmerican(bookDec);

                        var rec = new BetRecommendation
                        {
                            ModelPrediction = homePrediction,
                            MarketOutcomeId = line.HomeOutcomeId,
                            ProviderId = line.ProviderId,
                            GameId = line.GameId,
                            PlayerId = null,
                            MarketTypeId = moneylineType.MarketTypeId,
                            LineValue = null,
                            AmericanOdds = american,
                            ImpliedProbability = bookProb,
                            ModelProbability = modelHomeProb,
                            Edge = edge,
                            RiskLevel = ClassifyRisk(edge),
                            CreatedUtc = DateTime.UtcNow,
                            IsActive = true
                        };

                        _db.BetRecommendations.Add(rec);
                        recCount++;
                    }
                }

                // AWAY side
                if (awayPrediction != null && line.AwayDec.HasValue && line.AwayOutcomeId != 0)
                {
                    var bookDec = line.AwayDec.Value;
                    if (bookDec > 1m && fairAwayDec > 1m)
                    {
                        var edge = bookDec / fairAwayDec - 1m;
                        var bookProb = 1m / bookDec;
                        var american = line.AwayAmerican ?? DecimalToAmerican(bookDec);

                        var rec = new BetRecommendation
                        {
                            ModelPrediction = awayPrediction,
                            MarketOutcomeId = line.AwayOutcomeId,
                            ProviderId = line.ProviderId,
                            GameId = line.GameId,
                            PlayerId = null,
                            MarketTypeId = moneylineType.MarketTypeId,
                            LineValue = null,
                            AmericanOdds = american,
                            ImpliedProbability = bookProb,
                            ModelProbability = modelAwayProb,
                            Edge = edge,
                            RiskLevel = ClassifyRisk(edge),
                            CreatedUtc = DateTime.UtcNow,
                            IsActive = true
                        };

                        _db.BetRecommendations.Add(rec);
                        recCount++;
                    }
                }
            }
        }

        await _db.SaveChangesAsync();
        Console.WriteLine($"[ValueEngine] Completed. Inserted {recCount} MONEYLINE value bets (ELO).");
    }

    // ---------- helpers ----------

    private static decimal AmericanToDecimal(int odds)
    {
        if (odds > 0)
            return 1m + odds / 100m;

        var abs = Math.Abs(odds);
        return 1m + 100m / abs;
    }

    private static int DecimalToAmerican(decimal dec)
    {
        if (dec <= 1m) return 0;

        if (dec >= 2m)
        {
            return (int)Math.Round((dec - 1m) * 100m, MidpointRounding.AwayFromZero);
        }
        else
        {
            var frac = dec - 1m;
            if (frac <= 0m) return 0;
            var val = -100m / frac;
            return (int)Math.Round(val, MidpointRounding.AwayFromZero);
        }
    }

    private static string ClassifyRisk(decimal edge)
    {
        if (edge >= 0.06m) return "High";      // 6%+
        if (edge >= 0.03m) return "Medium";    // 3–6%
        if (edge >= 0.01m) return "Low";       // 1–3%
        return "Negative";
    }

    private sealed class ProviderLine
    {
        public long ProviderId { get; set; }
        public long GameId { get; set; }

        public decimal? HomeDec { get; set; }
        public int? HomeAmerican { get; set; }
        public int HomeOutcomeId { get; set; }

        public decimal? AwayDec { get; set; }
        public int? AwayAmerican { get; set; }
        public int AwayOutcomeId { get; set; }
    }
}
