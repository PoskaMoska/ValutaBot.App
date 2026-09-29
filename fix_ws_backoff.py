import re

with open('MiniApp/Services/TwelveDataWebSocketStream.cs', 'r', encoding='utf-8') as f:
    content = f.read()

# Add connectionTime tracking
content = content.replace('using (_ws = new ClientWebSocket())', 'var connectTime = DateTime.UtcNow;\n                using (_ws = new ClientWebSocket())')

# Add adaptive backoff logic
old_backoff = 'BotLogger.Warn("[TwelveData WS] Disconnected. Waiting 10s before next reconnect to avoid spamming API...");\n                await Task.Delay(10000, ct);'
new_backoff = """double aliveSeconds = (DateTime.UtcNow - connectTime).TotalSeconds;
                if (aliveSeconds < 10) 
                {
                    BotLogger.Error($"[TwelveData WS] Server rejected connection instantly ({aliveSeconds:F1}s). Limit likely exceeded. Sleeping 1 hour.");
                    await Task.Delay(TimeSpan.FromHours(1), ct);
                }
                else 
                {
                    BotLogger.Warn($"[TwelveData WS] Disconnected after {aliveSeconds:F1}s. Waiting 15s...");
                    await Task.Delay(15000, ct);
                }"""

content = content.replace(old_backoff, new_backoff)

with open('MiniApp/Services/TwelveDataWebSocketStream.cs', 'w', encoding='utf-8') as f:
    f.write(content)
