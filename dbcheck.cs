using System;
using System.Data;
using Dapper;
using Npgsql;
class Program {
    static void Main() {
        var connStr = ""postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway"";
        using var conn = new NpgsqlConnection(connStr);
        conn.Open();
        var res = conn.Query(""SELECT asset, interval, open_price, high_price, low_price, close_price FROM subminute_candles WHERE close_price = 0 OR open_price = 0 LIMIT 10;"");
        foreach(var r in res) {
            Console.WriteLine($""{r.asset} {r.interval} O:{r.open_price} H:{r.high_price} L:{r.low_price} C:{r.close_price}"");
        }
    }
}
