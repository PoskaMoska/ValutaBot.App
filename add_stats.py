import re

with open('MiniApp/Controllers/MiniAppController.Routes.cs', 'r', encoding='utf-8') as f:
    content = f.read()

new_route = '''
        app.MapGet("/api/stats/ml", async (HttpContext context) =>
        {
            try
            {
                using var conn = ValutaBot.App.MiniApp.Data.DbConnectionFactory.GetConnection();
                var stats = await Dapper.SqlMapper.QueryAsync(conn, @"
                    SELECT 
                        asset, 
                        timeframe, 
                        COUNT(*) as TotalTrades, 
                        SUM(CASE WHEN was_win = true THEN 1 ELSE 0 END) as Wins,
                        ROUND(SUM(CASE WHEN was_win = true THEN 1 ELSE 0 END) * 100.0 / NULLIF(COUNT(*), 0), 2) as WinRate
                    FROM trade_outcomes
                    WHERE features_json IS NOT NULL
                    GROUP BY asset, timeframe
                    ORDER BY asset, timeframe
                ");
                return Microsoft.AspNetCore.Http.Results.Json(stats);
            }
            catch (System.Exception ex)
            {
                return Microsoft.AspNetCore.Http.Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        });

        app.MapGet("/api/stats", (Delegate)HandleGetStats).RequireRateLimiting("Global");
'''

content = content.replace('app.MapGet("/api/stats", (Delegate)HandleGetStats).RequireRateLimiting("Global");', new_route)

with open('MiniApp/Controllers/MiniAppController.Routes.cs', 'w', encoding='utf-8') as f:
    f.write(content)
