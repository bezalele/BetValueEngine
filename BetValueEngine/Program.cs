using BetValueEngine.Data;
using BetValueEngine.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

var config = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .Build();

var services = new ServiceCollection();

services.AddDbContext<BettingDbContext>(options =>
    options.UseSqlServer(config.GetConnectionString("BettingDb")));

services.AddScoped<ValueEngineService>();

var provider = services.BuildServiceProvider();

using (var scope = provider.CreateScope())
{
    var svc = scope.ServiceProvider.GetRequiredService<ValueEngineService>();
    await svc.RunAsync();
}

Console.WriteLine("Value engine run completed. Press any key to exit.");
Console.ReadKey();
