import os
import re
import glob

php_files = glob.glob(r'C:\Users\Adam\Documents\Filen\Projects\PCConnect\api\**\*.php', recursive=True)

queries = []
for p in php_files:
    if '-old' in p.lower() or ' - old' in p.lower():
        continue
    with open(p, 'r', encoding='utf-8') as f:
        content = f.read()
    
    # find sql queries
    for m in re.finditer(r'(INSERT INTO|SELECT|UPDATE|DELETE FROM)\s+(.*?)(?=";)', content, re.IGNORECASE):
        queries.append(f"{os.path.basename(p)}: {m.group(0)}")

for q in queries:
    print(q)
