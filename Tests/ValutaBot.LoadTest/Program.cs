using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Linq;

namespace ValutaBot.LoadTest
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("==================================================");
            Console.WriteLine("        VALUTABOT API STRESS TEST TOOL");
            Console.WriteLine("==================================================");

            int concurrency = 200;
            int requestsPerThread = 25;
            int totalRequests = concurrency * requestsPerThread;
            string url = "http://localhost:5000/api/analyze?asset=EURUSD&timeframe=1m";

            Console.WriteLine($"Target: {url}");
            Console.WriteLine($"Concurrency: {concurrency}");
            Console.WriteLine($"Requests per thread: {requestsPerThread}");
            Console.WriteLine($"Total requests: {totalRequests}\n");

            var handler = new SocketsHttpHandler 
            { 
                MaxConnectionsPerServer = 1000,
                PooledConnectionLifetime = TimeSpan.FromMinutes(2)
            };
            var client = new HttpClient(handler);
            
            int successCount = 0;
            int failCount = 0;
            var latencies = new ConcurrentBag<double>();

            Console.WriteLine("Warming up with 1 request...");
            try { await client.GetAsync(url); } catch { Console.WriteLine("Warning: API might be down."); }

            Console.WriteLine("Starting barrage...");
            var sw = Stopwatch.StartNew();

            var tasks = new Task[concurrency];
            for (int i = 0; i < concurrency; i++)
            {
                tasks[i] = Task.Run(async () =>
                {
                    for (int j = 0; j < requestsPerThread; j++)
                    {
                        var reqSw = Stopwatch.StartNew();
                        try
                        {
                            var response = await client.GetAsync(url);
                            reqSw.Stop();
                            latencies.Add(reqSw.Elapsed.TotalMilliseconds);
                            if (response.IsSuccessStatusCode)
                            {
                                Interlocked.Increment(ref successCount);
                            }
                            else
                            {
                                Interlocked.Increment(ref failCount);
                            }
                        }
                        catch
                        {
                            reqSw.Stop();
                            Interlocked.Increment(ref failCount);
                        }
                    }
                });
            }

            await Task.WhenAll(tasks);
            sw.Stop();

            var latencyList = latencies.OrderBy(x => x).ToList();
            double avgLatency = latencyList.Count > 0 ? latencyList.Average() : 0;
            double p95 = latencyList.Count > 0 ? latencyList[(int)(latencyList.Count * 0.95)] : 0;
            double p99 = latencyList.Count > 0 ? latencyList[(int)(latencyList.Count * 0.99)] : 0;

            Console.WriteLine("==================================================");
            Console.WriteLine($"Time taken: {sw.Elapsed.TotalSeconds:F2} seconds");
            Console.WriteLine($"Requests/sec: {totalRequests / sw.Elapsed.TotalSeconds:F2}");
            Console.WriteLine($"Successful: {successCount}");
            Console.WriteLine($"Failed: {failCount}");
            Console.WriteLine($"Avg Latency: {avgLatency:F2} ms");
            Console.WriteLine($"P95 Latency: {p95:F2} ms");
            Console.WriteLine($"P99 Latency: {p99:F2} ms");
            Console.WriteLine("==================================================");
        }
    }
}
