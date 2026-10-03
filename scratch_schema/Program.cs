using System;
using System.Threading.Tasks;
using Npgsql;

class Program
{
    static async Task Main()
    {
        try {
            string connStr = "Host=centerbeam.proxy.rlwy.net;Port=47825;Database=railway;Username=postgres;Password=MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN";
            await using var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync();
            using var cmd = new NpgsqlCommand("SELECT key, weights_json FROM meta_learner_weights", conn);
            using var r = await cmd.ExecuteReaderAsync();
            while(await r.ReadAsync()) {
                Console.WriteLine($"{r.GetString(0)}: {r.GetString(1)}");
            }
        } catch (Exception e) {
            Console.WriteLine(e.Message);
        }
    }
}
