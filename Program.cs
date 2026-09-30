using System;
using System.Threading.Tasks;
using Npgsql;
using Dapper;

class Program
{
    static async Task Main(string[] args)
    {
        string connStr = ""postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway"";
        // Convert to Npgsql format
        var uri = new Uri(connStr);
        var userInfo = uri.UserInfo.Split(':');
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 5432,
            Username = userInfo[0],
            Password = userInfo[1],
            Database = uri.AbsolutePath.TrimStart('/'),
            SslMode = SslMode.Disable
        };

        using var conn = new NpgsqlConnection(builder.ToString());
        await conn.OpenAsync();
        
        var totalTrades = await conn.ExecuteScalarAsync<int>(""SELECT COUNT(*) FROM trade_outcomes;"");
        var lastNight = await conn.ExecuteScalarAsync<int>(""SELECT COUNT(*) FROM trade_outcomes WHERE created_at >= (NOW() - INTERVAL '24 hours');"");
        var wins = await conn.ExecuteScalarAsync<int>(""SELECT COUNT(*) FROM trade_outcomes WHERE created_at >= (NOW() - INTERVAL '24 hours') AND is_win = true;"");
        var subminuteCandles = await conn.ExecuteScalarAsync<int>(""SELECT COUNT(*) FROM subminute_candles;"");
        var ticksOvernight = await conn.ExecuteScalarAsync<int>(""SELECT COUNT(*) FROM subminute_candles WHERE open_time >= (NOW() - INTERVAL '24 hours');"");
        
        Console.WriteLine($""Total trades: {totalTrades}"");
        Console.WriteLine($""Last 24h trades: {lastNight}"");
        Console.WriteLine($""Wins (24h): {wins}"");
        Console.WriteLine($""Total Subminute Candles: {subminuteCandles}"");
        Console.WriteLine($""Ticks Overnight: {ticksOvernight}"");
    }
}