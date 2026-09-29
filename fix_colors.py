# -*- coding: utf-8 -*-
import re

with open('MiniApp/wwwroot/js/api.js', 'r', encoding='utf-8') as f:
    content = f.read()

def replace_block(pattern, new_code):
    global content
    content = re.sub(pattern, new_code, content, flags=re.DOTALL)

# Fix Session Colors
session_pattern = r'let sessionLow = data\.uiMarketSession\.toLowerCase\(\);.*?wSession\.style\.color = sColor;'
session_new = '''let sessionLow = data.uiMarketSession.toLowerCase();
                if (sessionLow.includes("европа") || sessionLow.includes("америка") || sessionLow.includes("нью-йорк")) {
                    sColor = "#10b981"; // green (good volume)
                } else if (sessionLow.includes("otc")) {
                    sColor = "#8b5cf6"; // purple (OTC)
                } else if (sessionLow.includes("мертвая") || sessionLow.includes("пила") || sessionLow.includes("азия")) {
                    sColor = "#f59e0b"; // yellow (choppy)
                }
                wSession.style.color = sColor;'''
replace_block(session_pattern, session_new)

# Fix Phase Colors
phase_pattern = r'let phaseLow = data\.uiMarketPhase\.toLowerCase\(\);.*?wPhase\.style\.color = phaseColor;'
phase_new = '''let phaseLow = data.uiMarketPhase.toLowerCase();

                if (phaseLow.includes("флэт") || phaseLow.includes("замедление") || phaseLow.includes("консолидация")) {
                    phaseColor = "#f59e0b"; // Yellow (Average / Sideways)
                } else if (phaseLow.includes("медвеж") || phaseLow.includes("перепроданность") || phaseLow.includes("падение")) {
                    phaseColor = "#ef4444"; // Red (Downward / Bad)
                } else if (phaseLow.includes("быч") || phaseLow.includes("рост") || phaseLow.includes("перекупленность")) {
                    phaseColor = "#10b981"; // Green (Upward)
                }
                wPhase.style.color = phaseColor;'''
replace_block(phase_pattern, phase_new)

# Fix Entropy Colors
entropy_pattern = r'let entLow = data\.uiMarketEntropy\.toLowerCase\(\);.*?wEntropy\.style\.color = entColor;'
entropy_new = '''let entLow = data.uiMarketEntropy.toLowerCase();

                if (entLow.includes("норма") || entLow.includes("безопасно")) {
                    entColor = "#10b981"; // Green
                } else if (entLow.includes("хаос") || entLow.includes("опасно") || entLow.includes("шторм")) {
                    entColor = "#ef4444"; // Red
                } else if (entLow.includes("мертвый") || entLow.includes("сонный") || entLow.includes("штиль")) {
                    entColor = "#f59e0b"; // Yellow / Orange
                }

                wEntropy.style.color = entColor;'''
replace_block(entropy_pattern, entropy_new)

with open('MiniApp/wwwroot/js/api.js', 'w', encoding='utf-8') as f:
    f.write(content)
