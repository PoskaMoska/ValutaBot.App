import re

with open('MiniApp/Controllers/MiniAppController.Routes.cs', 'r', encoding='utf-8') as f:
    content = f.read()

new_route = '''
        app.MapGet("/api/stats/dataset", async (HttpContext context) =>
        {
            try
            {
                using var conn = ValutaBot.App.MiniApp.Data.DbConnectionFactory.GetConnection();
                int tradeOutcomesCount = await Dapper.SqlMapper.ExecuteScalarAsync<int>(conn, "SELECT COUNT(*) FROM trade_outcomes WHERE features_json IS NOT NULL");
                int pendingTradesCount = await Dapper.SqlMapper.ExecuteScalarAsync<int>(conn, "SELECT COUNT(*) FROM pending_trades WHERE features_json IS NOT NULL");
                return Microsoft.AspNetCore.Http.Results.Json(new {
                    trade_outcomes = tradeOutcomesCount,
                    pending_trades = pendingTradesCount,
                    total = tradeOutcomesCount + pendingTradesCount
                });
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
