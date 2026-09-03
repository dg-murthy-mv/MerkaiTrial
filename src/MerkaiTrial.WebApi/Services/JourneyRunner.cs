using MerkaiTrial.Infrastructure.Persistence;

namespace MerkaiTrial.WebApi.Services
{
    // src/MerkaiTrial.WebApi/Services/JourneyRunner.cs
    public sealed class JourneyRunner : BackgroundService
    {
        private readonly FlowDbContext _db; private readonly ILogger<JourneyRunner> _log;
        public JourneyRunner(FlowDbContext db, ILogger<JourneyRunner> log) { _db = db; _log = log; }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // pseudo: find due enrollments & enqueue messages into ChannelMessages
                // Keep it tiny for demo; run every minute.
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }
    }
    
    

}
