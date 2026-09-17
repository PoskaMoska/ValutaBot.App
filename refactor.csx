using System;
using System.IO;

var content = File.ReadAllText(""MiniApp/Controllers/MiniAppController.cs"");

int startIdx = content.IndexOf(""public static void Start(string[] args, int port = 5000)"");
int bodyStart = content.IndexOf(""{"", startIdx) + 1;
int appRunIdx = content.IndexOf(""app.Run("", bodyStart);

string preStart = content.Substring(0, bodyStart);
string startBody = content.Substring(bodyStart, appRunIdx - bodyStart);
string postStart = content.Substring(appRunIdx);

int buildIdx = startBody.IndexOf(""var app = builder.Build();"");

string servicesPart = startBody.Substring(0, buildIdx);
string appPart = startBody.Substring(buildIdx);

string newContent = preStart + @""
        Console.WriteLine(""""========================================""\"\");
        Console.WriteLine(""""[Live Core] TradeBE_bot \""""v\"""" MiniApp Server"""");

        string? envPort = Environment.GetEnvironmentVariable(""""PORT"""");
        if (!string.IsNullOrEmpty(envPort) && int.TryParse(envPort, out int parsedPort))
        {
            port = parsedPort;
        }

        Console.WriteLine($""""[+] Port: {port}"""");
        Console.WriteLine(""""========================================""\"\");

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            WebRootPath = System.IO.Path.Combine(AppContext.BaseDirectory, """"MiniApp"""", """"wwwroot"""")
        });

        builder.AddBotServices();
        var app = builder.Build();
        app.MapBotEndpoints();
        
        "" + postStart;

File.WriteAllText(""MiniApp/Controllers/MiniAppController.cs"", newContent);

int botSettingsIdx = servicesPart.IndexOf(""var botSettings"");
string extServices = servicesPart.Substring(botSettingsIdx);

int appUseIdx = appPart.IndexOf(""app.Use"");
if (appUseIdx == -1) appUseIdx = appPart.IndexOf(""app.Map"");
string extApp = appPart.Substring(appUseIdx);

string extContent = @""using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http;
using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace ValutaBot.MiniApp;

public static class MiniAppConfigurationExtensions
{
    public static void AddBotServices(this WebApplicationBuilder builder)
    {
"" + extServices + @""    }

    public static void MapBotEndpoints(this WebApplication app)
    {
"" + extApp + @""    }
}
"";

File.WriteAllText(""MiniApp/Controllers/MiniAppConfigurationExtensions.cs"", extContent);
Console.WriteLine(""Done!"");
