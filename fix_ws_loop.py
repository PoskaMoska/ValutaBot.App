import re

with open('MiniApp/Services/TwelveDataWebSocketStream.cs', 'r', encoding='utf-8') as f:
    content = f.read()

old_code = """                    exitLoop:;
                    watchdogCts.Cancel(); // Stop watchdog when connection loop ends
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)"""

new_code = """                    exitLoop:;
                    watchdogCts.Cancel(); // Stop watchdog when connection loop ends
                }
                
                BotLogger.Warn("[TwelveData WS] Disconnected. Waiting 10s before next reconnect to avoid spamming API...");
                await Task.Delay(10000, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)"""

if old_code in content:
    content = content.replace(old_code, new_code)
    with open('MiniApp/Services/TwelveDataWebSocketStream.cs', 'w', encoding='utf-8') as f:
        f.write(content)
    print("Fixed WS loop gracefully")
else:
    print("Could not find the exact block to replace")
