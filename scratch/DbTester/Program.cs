using System;
using Npgsql;
using Dapper;
using System.Threading.Tasks;

class Program {
    static async Task Main() {
        var connStr = "Host=centerbeam.proxy.rlwy.net;Port=47825;Username=postgres;Password=MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN;Database=railway;";
        using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();
        
        int rows = await conn.ExecuteAsync("UPDATE calibration_state SET ema_win_rate = 0.5, total_trades = 0;");
        Console.WriteLine($"Reset {rows} rows in calibration_state to 50% winrate.");
    }
}
