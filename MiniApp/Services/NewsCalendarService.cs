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
        public string Title { get; set; } = "";
        public string Country { get; set; } = "";
        public DateTime UtcTime { get; set; }
        public string Impact { get; set; } = ""; // High, Medium, Low
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
                var response = await _httpClient.GetStringAsync("https://nfs.faireconomy.media/ff_calendar_thisweek.xml", stoppingToken);
                var doc = XDocument.Parse(response);
                
                var events = new List<NewsEvent>();
                TimeZoneInfo estZone;
                try { estZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
                catch { estZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); } 

                foreach (var ev in doc.Descendants("event"))
                {
                    string dateStr = ev.Element("date")?.Value?.Trim() ?? "";
                    string timeStr = ev.Element("time")?.Value?.Trim() ?? "";
                    string country = ev.Element("country")?.Value?.Trim() ?? "";
                    string impact = ev.Element("impact")?.Value?.Trim() ?? "";
                    string title = ev.Element("title")?.Value?.Trim() ?? "";

                    if (impact != "High") continue; // We only care about high impact for the shield
                    if (timeStr == "All Day" || timeStr == "Tentative") continue;

                    string dateTimeStr = $"{dateStr} {timeStr}";
                    if (DateTime.TryParseExact(dateTimeStr, "MM-dd-yyyy hh:mmtt", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsedEst))
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

                _logger.LogInformation($"[NewsCalendar] Downloaded {_events.Count} High-Impact news events for this week.");
            }
            catch (Exception ex)
            {
                _logger.LogError($"[NewsCalendar] Failed to update: {ex.Message}");
            }
        }

        public NewsEvent? GetNextHighImpactNews(string asset)
        {
            if (string.IsNullOrWhiteSpace(asset)) return null;

            string clean = asset.ToUpper().Replace(" OTC", "").Replace("_OTC", "").Replace("/", "").Replace("-", "").Trim();
            string c1 = "";
            string c2 = "";

            if (clean.Length >= 6)
            {
                c1 = clean.Substring(0, 3);
                c2 = clean.Substring(3, 3);
            }
            else
            {
                var parts = asset.Replace(" OTC", "").Split('/');
                if (parts.Length == 2)
                {
                    c1 = parts[0].Trim().ToUpper();
                    c2 = parts[1].Trim().ToUpper();
                }
            }

            if (string.IsNullOrEmpty(c1) || string.IsNullOrEmpty(c2)) return null;

            _lock.Wait();
            try
            {
                var now = DateTime.UtcNow;
                return _events.FirstOrDefault(e => 
                    (string.Equals(e.Country, c1, StringComparison.OrdinalIgnoreCase) || 
                     string.Equals(e.Country, c2, StringComparison.OrdinalIgnoreCase)) && 
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
