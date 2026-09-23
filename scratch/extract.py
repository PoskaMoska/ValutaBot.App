import re
import os

source_file = 'ml_service/model.py'
with open(source_file, 'r', encoding='utf-8') as f:
    content = f.read()

# I will just write the data loader manually to be safer, no need for complex regex.
