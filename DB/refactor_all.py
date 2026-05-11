import os
import re

api_dir = r"C:\Users\Adam\Documents\Filen\Projects\PCConnect\api"

def process_file(file_path):
    with open(file_path, 'r', encoding='utf-8') as f:
        content = f.read()

    original = content

    # 1. API Keys table removal everywhere
    content = content.replace(
        "SELECT UserID FROM apikeys WHERE api_key = ?",
        "SELECT id as UserID FROM users WHERE api_key = ?"
    )
    content = content.replace(
        "SELECT username FROM apikeys WHERE api_key = ?",
        "SELECT Username as username FROM users WHERE api_key = ?"
    )
    content = content.replace(
        "SELECT api_key FROM apikeys WHERE UserID = ?",
        "SELECT api_key FROM users WHERE id = ?"
    )
    content = content.replace(
        "apikeys table",
        "users table"
    )

    # 2. Time table removal
    content = content.replace(
        "SELECT Time FROM time WHERE PCID = ?",
        "SELECT Time FROM pcnames WHERE PCID = ?"
    )
    content = content.replace(
        "UPDATE time SET Time = ? WHERE PCID = ?",
        "UPDATE pcnames SET Time = ? WHERE PCID = ?"
    )
    content = content.replace(
        "UPDATE time SET Time = ? WHERE Username = ? AND PCName = ?",
        "UPDATE pcnames SET Time = ? WHERE UserID = (SELECT id FROM users WHERE Username = ?) AND PCName = ?"
    )
    content = content.replace(
        "SELECT Time FROM time WHERE Username = ? AND PCName = ?",
        "SELECT Time FROM pcnames WHERE UserID = (SELECT id FROM users WHERE Username = ?) AND PCName = ?"
    )
    

    # 3. Requests table removal
    content = content.replace(
        "SELECT Request FROM requests WHERE Value = 1 AND PCID = ?",
        "SELECT Request FROM pcnames WHERE Value = 1 AND PCID = ?"
    )
    content = content.replace(
        "SELECT Request FROM requests WHERE Value = '1' AND PCID = ?",
        "SELECT Request FROM pcnames WHERE Value = 1 AND PCID = ?"
    )
    content = content.replace(
        "UPDATE requests SET Value = 0, Request = '0' WHERE PCID = ?",
        "UPDATE pcnames SET Value = 0, Request = '0' WHERE PCID = ?"
    )
    content = content.replace(
        "UPDATE requests SET Value = 0, Request = '0' WHERE Username = ? AND PCName = ?",
        "UPDATE pcnames SET Value = 0, Request = '0' WHERE UserID = (SELECT id FROM users WHERE Username = ?) AND PCName = ?"
    )
    content = content.replace(
        "UPDATE requests SET Request = ?, Value = 1 WHERE PCID = ?",
        "UPDATE pcnames SET Request = ?, Value = 1 WHERE PCID = ?"
    )
    content = content.replace(
        "UPDATE requests SET Request = ?, Value = 1 WHERE Username = ? AND PCName = ?",
        "UPDATE pcnames SET Request = ?, Value = 1 WHERE UserID = (SELECT id FROM users WHERE Username = ?) AND PCName = ?"
    )
    content = content.replace(
        "SELECT Request FROM requests WHERE Value = '1' AND Username = ? AND PCName = ?",
        "SELECT Request FROM pcnames WHERE Value = 1 AND UserID = (SELECT id FROM users WHERE Username = ?) AND PCName = ?"
    )

    # 4. Any leftover pcid references where requests/time used to be
    content = content.replace("INSERT INTO time (PCID) VALUES (?)", "// REMOVED")
    content = content.replace("INSERT INTO requests (PCID, Request, Value) VALUES (?, '0', 0)", "// REMOVED")


    if original != content:
        with open(file_path, 'w', encoding='utf-8') as f:
            f.write(content)
        print(f"Updated: {file_path}")

for root, _, sorted_files in os.walk(api_dir):
    for f in sorted_files:
        if f.endswith('.php'):
            process_file(os.path.join(root, f))

print("All patches finalized via recursive script traversal!")
