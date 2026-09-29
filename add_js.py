import re

with open('MiniApp/wwwroot/js/api.js', 'r', encoding='utf-8') as f:
    content = f.read()

# Add a function at the very end of api.js
js_code = '''
async function fetchDatasetStats() {
    try {
        const res = await fetch(API_BASE_URL + '/stats/dataset');
        if (res.ok) {
            const data = await res.json();
            const counter = document.getElementById('datasetCounter');
            if (counter) {
                counter.innerHTML = 'Dataset: <span style=\"color:#10b981\">' + data.total + '</span> rows';
            }
        }
    } catch (e) {
        console.log("Failed to fetch dataset stats", e);
    }
}
setInterval(fetchDatasetStats, 30000); // Check every 30 seconds
setTimeout(fetchDatasetStats, 1000); // Check shortly after load
'''

content += js_code

with open('MiniApp/wwwroot/js/api.js', 'w', encoding='utf-8') as f:
    f.write(content)
