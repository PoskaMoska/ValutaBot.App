using System;
using System.Data;
using Dapper;
using Npgsql;
class Program {
    static void Main() {
        var connStr = ""postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway"";
        using var conn = new NpgsqlConnection(connStr);
        conn.Open();
        var res = conn.Execute(""DELETE FROM subminute_candles WHERE close_price = 0 OR open_price = 0 OR high_price = 0 OR low_price = 0;"");
        Console.WriteLine($""Deleted {res} rows."");
    }
}
