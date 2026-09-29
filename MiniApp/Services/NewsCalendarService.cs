using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ValutaBot.MiniApp.Services
{
    public class NewsEvent
    {
        public string Title { get; set; } = """";
        public string Country { get; set; } = """";
        public DateTime UtcTime { get; set; }
        public string Impact { get; set; } = """"; // High, Medium, Low
    }

    public interface INewsCalendarService
    {
        int? GetMinutesToNextHighImpactNews(string asset);
        NewsEvent? GetNextHighImpactNews(string asset);
    }

    public class NewsCalendarService : BackgroundService, INewsCalendarService
    {
        private readonly ILogger<NewsCalendarService> _logger;
        private readonly HttpClient _httpClient;
        private List<NewsEvent> _events = new();
        private readonly SemaphoreSlim _lock = new(1, 1);
        private DateTime _lastUpdate = DateTime.MinValue;

        public NewsCalendarService(ILogger<NewsCalendarService> logger)
        {
            _logger = logger;
            _httpClient = new HttpClient();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await UpdateCalendarAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromHours(4), stoppingToken);
            }
        }

        private async Task UpdateCalendarAsync(CancellationToken stoppingToken)
        {
            try
            {
                var response = await _httpClient.GetStringAsync(""https://nfs.faireconomy.media/ff_calendar_thisweek.xml"", stoppingToken);
                var doc = XDocument.Parse(response);
                
                var events = new List<NewsEvent>();
                var estZone = TimeZoneInfo.FindSystemTimeZoneById(""Eastern Standard Time""); // Windows 

                foreach (var ev in doc.Descendants(""event""))
                {
                    string dateStr = ev.Element(""date"")?.Value?.Trim() ?? """";
                    string timeStr = ev.Element(""time"")?.Value?.Trim() ?? """";
                    string country = ev.Element(""country"")?.Value?.Trim() ?? """";
                    string impact = ev.Element(""impact"")?.Value?.Trim() ?? """";
                    string title = ev.Element(""title"")?.Value?.Trim() ?? """";

                    if (impact != ""High"") continue; // We only care about high impact for the shield
                    if (timeStr == ""All Day"" || timeStr == ""Tentative"") continue;

                    string dateTimeStr = $""{dateStr} {timeStr}"";
                    if (DateTime.TryParseExact(dateTimeStr, ""MM-dd-yyyy hh:mmtt"", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsedEst))
                    {
                        var utcTime = TimeZoneInfo.ConvertTimeToUtc(parsedEst, estZone);
                        events.Add(new NewsEvent
                        {
                            Title = title,
                            Country = country,
                            Impact = impact,
                            UtcTime = utcTime
                        });
                    }
                }

                await _lock.WaitAsync(stoppingToken);
                try
                {
                    _events = events.OrderBy(e => e.UtcTime).ToList();
                    _lastUpdate = DateTime.UtcNow;
                }
                finally
                {
                    _lock.Release();
                }

                _logger.LogInformation($""[NewsCalendar] Downloaded {_events.Count} High-Impact news events for this week."");
            }
            catch (Exception ex)
            {
                _logger.LogError($""[NewsCalendar] Failed to update: {ex.Message}"");
            }
        }

        public NewsEvent? GetNextHighImpactNews(string asset)
        {
            // Asset is e.g. "EUR/USD" or "EUR/USD OTC"
            var parts = asset.Replace("" OTC"", """").Split('/');
            if (parts.Length != 2) return null;

            var c1 = parts[0];
            var c2 = parts[1];

            _lock.Wait();
            try
            {
                var now = DateTime.UtcNow;
                return _events.FirstOrDefault(e => 
                    (e.Country == c1 || e.Country == c2) && 
                    e.UtcTime >= now);
            }
            finally
            {
                _lock.Release();
            }
        }

        public int? GetMinutesToNextHighImpactNews(string asset)
        {
            var nextNews = GetNextHighImpactNews(asset);
            if (nextNews == null) return null;

            return (int)(nextNews.UtcTime - DateTime.UtcNow).TotalMinutes;
        }
    }
}
