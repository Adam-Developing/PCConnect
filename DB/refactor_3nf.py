import os

# Files to update
base_dir = r"C:\Users\Adam\Documents\Filen\Projects\PCConnect\api"

def replace_in_file(filepath, replacements):
    if not os.path.exists(filepath):
        print(f"Skipping {filepath}, does not exist.")
        return
    with open(filepath, 'r', encoding='utf-8') as f:
        content = f.read()
    
    modified = content
    for old, new in replacements:
        modified = modified.replace(old, new)
        
    if modified != content:
        with open(filepath, 'w', encoding='utf-8') as f:
            f.write(modified)
        print(f"Updated: {filepath}")
    else:
        print(f"No changes needed in: {filepath}")


def process_backend():
    # 1. login.php
    replace_in_file(os.path.join(base_dir, "login.php"), [
        ("SELECT users.id, users.Password, apikeys.api_key FROM users INNER JOIN apikeys ON users.id = apikeys.UserID WHERE users.Username = ?",
         "SELECT users.id, users.Password, users.api_key FROM users WHERE users.Username = ?")
    ])

    # 2. pcclient/login.php
    replace_in_file(os.path.join(base_dir, "pcclient", "login.php"), [
        ("SELECT users.id, users.Password, apikeys.api_key FROM users INNER JOIN apikeys ON users.id = apikeys.UserID WHERE users.Username = ?",
         "SELECT users.id, users.Password, users.api_key FROM users WHERE users.Username = ?")
    ])

    # 3. pctime.php
    replace_in_file(os.path.join(base_dir, "pcconnect", "pctime.php"), [
        ("SELECT Time FROM time WHERE PCID = ?", 
         "SELECT Time FROM pcnames WHERE PCID = ?")
    ])

    # 4. updatepctimedatabase.php
    replace_in_file(os.path.join(base_dir, "pcclient", "updatepctimedatabase.php"), [
        ("UPDATE time SET Time = ? WHERE PCID = ?", 
         "UPDATE pcnames SET Time = ? WHERE PCID = ?")
    ])

    # 5. findrequests.php
    replace_in_file(os.path.join(base_dir, "pcclient", "findrequests.php"), [
        ("SELECT Request FROM requests WHERE Value = 1 AND PCID = ?", 
         "SELECT Request FROM pcnames WHERE Value = 1 AND PCID = ?")
    ])

    # 6. updaterequest.php
    replace_in_file(os.path.join(base_dir, "pcclient", "updaterequest.php"), [
        ("UPDATE requests SET Value = 0, Request = '0' WHERE PCID = ?", 
         "UPDATE pcnames SET Value = 0, Request = '0' WHERE PCID = ?")
    ])

    # 7. exchange.php
    replace_in_file(os.path.join(base_dir, "pcconnect", "exchange.php"), [
        ("UPDATE requests SET Request = ?, Value = 1 WHERE PCID = ?", 
         "UPDATE pcnames SET Request = ?, Value = 1 WHERE PCID = ?")
    ])

    # 8. addpc.php
    replace_in_file(os.path.join(base_dir, "pcclient", "addpc.php"), [
        # Original:
        # $addPCStmt = $conn->prepare("INSERT INTO pcnames (UserID, PCName) VALUES (?, ?)");
        # ...
        # $insertRequestStmt = $conn->prepare("INSERT INTO requests (PCID, Request, Value) VALUES (?, '0', 0)");
        # $insertTimeStmt = $conn->prepare("INSERT INTO time (PCID) VALUES (?)");
        
        ("INSERT INTO pcnames (UserID, PCName) VALUES (?, ?)", 
         "INSERT INTO pcnames (UserID, PCName, Request, Value, Time) VALUES (?, ?, '0', 0, NULL)"),
         
        # Removing requests and time inserts dynamically
        ("$insertRequestStmt = $conn->prepare(\"INSERT INTO requests (PCID, Request, Value) VALUES (?, '0', 0)\");", "// REMOVED"),
        ("$insertRequestStmt->bind_param(\"i\", $PCID);", "// REMOVED"),
        ("$insertRequestStmt->execute();", "// REMOVED"),
        ("$insertRequestStmt->close();", "// REMOVED"),
        
        ("$insertTimeStmt = $conn->prepare(\"INSERT INTO time (PCID) VALUES (?)\");", "// REMOVED"),
        ("$insertTimeStmt->bind_param(\"i\", $PCID);", "// REMOVED"),
        ("$insertTimeStmt->execute();", "// REMOVED"),
        ("$insertTimeStmt->close();", "// REMOVED"),
        
        ("$insertRequestTemplate", "// REMOVED")
    ])

if __name__ == '__main__':
    process_backend()
