import sys
import os

with open('MiniApp/Controllers/MiniAppController.cs', 'r', encoding='utf-8') as f:
    content = f.read()

start_idx = content.find('public static void Start(string[] args, int port = 5000)')
body_start = content.find('{', start_idx) + 1
app_run_idx = content.find('app.Run(', body_start)

pre_start = content[:body_start]
start_body = content[body_start:app_run_idx]
post_start = content[app_run_idx:]

build_idx = start_body.find('var app = builder.Build();')

services_part = start_body[:build_idx]
app_part = start_body[build_idx:]

new_content = pre_start + '''
        Console.WriteLine("========================================");
        Console.WriteLine("[Live Core] TradeBE_bot \\\"v\\\" MiniApp Server");

        string? envPort = Environment.GetEnvironmentVariable("PORT");
        if (!string.IsNullOrEmpty(envPort) && int.TryParse(envPort, out int parsedPort))
        {
            port = parsedPort;
        }

        Console.WriteLine($"[+] Port: {port}");
        Console.WriteLine("========================================");

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            WebRootPath = System.IO.Path.Combine(AppContext.BaseDirectory, "MiniApp", "wwwroot")
        });

        builder.AddBotServices();
        var app = builder.Build();
        app.MapBotEndpoints();
        
        ''' + post_start

with open('MiniApp/Controllers/MiniAppController.cs', 'w', encoding='utf-8') as f:
    f.write(new_content)

ext_content = '''using Microsoft.AspNetCore.Builder;
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
''' + services_part[services_part.find('var botSettings'):] + '''    }

    public static void MapBotEndpoints(this WebApplication app)
    {
''' + app_part[app_part.find('app.Use'):] + '''    }
}
'''

with open('MiniApp/Controllers/MiniAppConfigurationExtensions.cs', 'w', encoding='utf-8') as f:
    f.write(ext_content)

print('Refactored!')
