import re

with open('MiniApp/wwwroot/index.html', 'r', encoding='utf-8') as f:
    content = f.read()

footer_html = '''
            <div id="datasetCounter" style="text-align:center; font-size:10px; color:var(--subtext); margin-top:12px; margin-bottom:12px; font-weight:500;">
                Dataset: ? loading...
            </div>
        </div>
    </div>
'''

content = content.replace("        </div>\n    </div>\n    \n    <script", footer_html + "    <script")

with open('MiniApp/wwwroot/index.html', 'w', encoding='utf-8') as f:
    f.write(content)
