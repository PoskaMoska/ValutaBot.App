import re

with open('scratch/MLPythonService.cs', 'r', encoding='utf-8') as f:
    code = f.read()

# Rename the class to DefaultMLPythonService and remove static
code = code.replace('public static class MLPythonService', 'public class DefaultMLPythonService : IMLPythonService')
# Remove static from all methods and fields
code = re.sub(r'public static (async Task|void|Task)', r'public \1', code)
code = re.sub(r'private static (string|IHttpClientFactory|Process|readonly JsonSerializerOptions)', r'private \1', code)
code = code.replace('static MLPythonService()', 'public DefaultMLPythonService(IHttpClientFactory httpFactory)')
code = code.replace('// Note: HttpClient instances are obtained via IHttpClientFactory to benefit from Polly policies.', '_httpFactory = httpFactory;\n        _baseUrl = "http://127.0.0.1:8000";\n        Init(null);')

# The Init method in the original sets _baseUrl, let's look at how it was defined.
# I will use a simple regex to remove "static" from all method declarations inside the class.
# It's actually easier to just do simple string replacements.

with open('scratch/refactor_ml.py', 'w', encoding='utf-8') as f:
    pass
