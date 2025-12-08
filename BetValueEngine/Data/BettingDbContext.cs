using Microsoft.EntityFrameworkCore;
using BetValueEngine.Domain.Entities;

namespace BetValueEngine.Data;

public class BettingDbContext : DbContext
{
    public BettingDbContext(DbContextOptions<BettingDbContext> options) : base(options) { }

    public DbSet<GameOdds> GameOdds => Set<GameOdds>();
    public DbSet<BetRecommendation> BetRecommendations => Set<BetRecommendation>();
    public DbSet<ModelRun> ModelRuns => Set<ModelRun>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<GameOdds>().ToTable("GameOdds","betting").HasKey(x=>x.GameOddsId);
        b.Entity<BetRecommendation>().ToTable("BetRecommendation","betting").HasKey(x=>x.BetRecommendationId);
        b.Entity<ModelRun>().ToTable("ModelRun","betting").HasKey(x=>x.ModelRunId);
    }
}
