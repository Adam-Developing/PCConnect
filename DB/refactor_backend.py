import glob
import re

files = glob.glob(r'C:\Users\Adam\Documents\Filen\Projects\PCConnect\api\**\*.php', recursive=True)

# Helper function to get PCID snippet
def get_pcid_snippet():
    return """
    // FETCH PCID
    $pcid_query = "SELECT PCID FROM pcnames WHERE UserID = ? AND PCName = ?";
    $pcid_stmt = $conn->prepare($pcid_query);
    $pcid_stmt->bind_param("is", $UserID, $PCName);
    $pcid_stmt->execute();
    $pcid_stmt->bind_result($PCID);
    $pcid_stmt->fetch();
    $pcid_stmt->close();
    
    if (!$PCID) {
        $insert_pc = "INSERT INTO pcnames (UserID, PCName) VALUES (?, ?)";
        $ins_pc = $conn->prepare($insert_pc);
        $ins_pc->bind_param("is", $UserID, $PCName);
        $ins_pc->execute();
        $PCID = $ins_pc->insert_id;
        $ins_pc->close();
    }
"""

def process_file(p):
    with open(p, 'r', encoding='utf-8') as f:
        content = f.read()

    orig_content = content

    # 1. Api Key Check replacements
    content = content.replace("SELECT username FROM apikeys", "SELECT UserID FROM apikeys")
    content = content.replace("$stmt->bind_result($dbUsername);", "$stmt->bind_result($UserID);")
    content = content.replace("$stmt->fetch(); // Fetch the result into $dbUsername", "$stmt->fetch();")
    content = content.replace("$Username = $dbUsername; // Assign the value to the $Username variable", "")
    content = content.replace("$Username = $dbUsername;", "")
    
    # 2. Replace all remaining $Username with $UserID
    content = content.replace("$Username", "$UserID")
    
    # Replace column names in queries
    # reminders
    content = re.sub(r'INSERT INTO reminders \(username,', r'INSERT INTO reminders (UserID,', content)
    content = re.sub(r'WHERE username = \?', r'WHERE UserID = ?', content)
    content = re.sub(r'AND username = \?', r'AND UserID = ?', content)
    
    # pcnames
    content = content.replace("SELECT PCName FROM pcnames WHERE Username = ?", "SELECT PCName FROM pcnames WHERE UserID = ?")
    
    # updatetimedatabase.php
    content = content.replace("UPDATE time SET Time = ? WHERE Username = ?", "UPDATE time SET Time = ? WHERE UserID = ?")
    
    # login.php specifics
    content = content.replace("SELECT Username FROM users WHERE Email = ? AND password = ? and Enabled = 1", "SELECT id FROM users WHERE Email = ? AND password = ? and Enabled = 1")
    content = content.replace("SELECT Username FROM users WHERE username = ? AND password = ? and Enabled = 1", "SELECT id FROM users WHERE username = ? AND password = ? and Enabled = 1")
    content = content.replace("SELECT api_key FROM apikeys WHERE username = ?", "SELECT api_key FROM apikeys WHERE UserID = ?")
    
    # signup.php specifics
    content = content.replace("INSERT INTO time (Username)", "INSERT INTO time (UserID)")
    content = content.replace("INSERT INTO Mailing_List (Email) VALUES (?)", "UPDATE users SET MailingList = 1 WHERE Email = ?")
    content = content.replace("INSERT INTO apikeys (username, api_key)", "INSERT INTO apikeys (UserID, api_key)")
    
    # signup.php user ID retrieval:
    if "signup.php" in p:
        content = content.replace("$insertUserStmt->execute();", "$insertUserStmt->execute();\\n            $UserID = $insertUserStmt->insert_id;")
    
    # 3. Handle PCID transformations
    # For scripts that use PCName in requests or time tables, we must inject get_pcid_snippet() right before the relevant query.
    
    if "addpc.php" in p:
        content = content.replace("INSERT INTO pcnames (Username, PCName)", "INSERT INTO pcnames (UserID, PCName)")
        # addpc.php also inserts into time and requests.
        # it has:
        # $addpc="INSERT INTO pcnames (Username, PCName) VALUES (?, ?)";
        # $insertRequestQuery = "INSERT INTO requests (Username, PCName, Request, Value) VALUES (?, ?, ?, ?)";
        # $insertTimeQuery = "INSERT INTO time (Username, PCName) VALUES (?, ?)";
        # We need to replace the variables bound.
        content = content.replace("INSERT INTO requests (Username, PCName,", "INSERT INTO requests (UserID, PCID,")
        content = content.replace("INSERT INTO time (Username, PCName)", "INSERT INTO time (UserID, PCID)")
        # Where it prepares insertRequestQuery
        find_req = r"(\$insertRequestTemplate = \$conn->prepare\(\$insertRequestQuery\);\s+)\$insertRequestTemplate->bind_param\(\"ssss\", \$UserID, \$PCName, \$requestValue, \$requestValue\);"
        repl_req = r"\n" + get_pcid_snippet() + r"\n\1$insertRequestTemplate->bind_param(\"iisi\", $UserID, $PCID, $requestValue, $requestValue);"
        content = re.sub(find_req, repl_req, content)
        
        find_time = r"(\$insertTimeTemplate = \$conn->prepare\(\$insertTimeQuery\);\s+)\$insertTimeTemplate->bind_param\(\"ss\", \$UserID, \$PCName\);"
        repl_time = r"\1$insertTimeTemplate->bind_param(\"ii\", $UserID, $PCID);"
        content = re.sub(find_time, repl_time, content)
        
    if "findrequests.php" in p or "findrequests-old.php" in p:
        find = r"\$query = \"SELECT Request FROM requests WHERE Value = '1' AND Username = \? AND PCName = \?\";"
        repl = get_pcid_snippet() + '\n    $query = "SELECT Request FROM requests WHERE Value = \'1\' AND UserID = ? AND PCID = ?";'
        content = re.sub(find, repl, content)
        content = content.replace("$requestStmt->bind_param(\"ss\", $UserID, $PCName);", "$requestStmt->bind_param(\"ii\", $UserID, $PCID);")
        
    if "updatepctimedatabase.php" in p:
        find = r"\$updateQuery = \"UPDATE time SET Time = \? WHERE Username = \? and PCName = \?\";"
        repl = get_pcid_snippet() + '\n    $updateQuery = "UPDATE time SET Time = ? WHERE UserID = ? and PCID = ?";'
        content = re.sub(find, repl, content)
        content = content.replace("$updateStmt->bind_param(\"sss\", $Time, $UserID, $PCName);", "$updateStmt->bind_param(\"sii\", $Time, $UserID, $PCID);")
        
    if "updaterequest.php" in p or "updaterequest-old.php" in p:
        find = r"\$updateQuery = \"UPDATE requests SET Value = 0, Request = 0 WHERE Username = \? AND PCName = \?\";"
        repl = get_pcid_snippet() + '\n    $updateQuery = "UPDATE requests SET Value = 0, Request = 0 WHERE UserID = ? AND PCID = ?";'
        content = re.sub(find, repl, content)
        content = content.replace("$updateStmt->bind_param(\"ss\", $UserID, $PCName);", "$updateStmt->bind_param(\"ii\", $UserID, $PCID);")
        
    if "exchange.php" in p or "exchange-Old.php" in p:
        find = r"\$updateQuery = \"UPDATE requests SET Value = 1, Request = \? WHERE Username = \? AND PCName = \?\";"
        repl = get_pcid_snippet() + '\n    $updateQuery = "UPDATE requests SET Value = 1, Request = ? WHERE UserID = ? AND PCID = ?";'
        content = re.sub(find, repl, content)
        content = content.replace("$updateStmt->bind_param(\"sss\", $Message, $UserID, $PCName);", "$updateStmt->bind_param(\"sii\", $Message, $UserID, $PCID);")
        
    if "pctime.php" in p or "pctime-Old.php" in p:
        find = r"\$query = \"SELECT Time FROM time WHERE Username = \? [Aa][Nn][Dd] PCName = \?\";"
        repl = get_pcid_snippet() + '\n    $query = "SELECT Time FROM time WHERE UserID = ? AND PCID = ?";'
        content = re.sub(find, repl, content)
        content = content.replace("$timeStmt->bind_param(\"ss\", $UserID, $PCName);", "$timeStmt->bind_param(\"ii\", $UserID, $PCID);")

    # In signup.php it also binds for request and time but without PCID in the DB... Wait!
    # original signup.php: $insertRequestQuery = "INSERT INTO requests (Username, Request, Value) VALUES (?, ?, ?)";
    # Wait, my new schema for requests requires PCID! It is NOT NULL. 
    # So signup.php MUST map a PCName. But signup doesn't have a PCName, users sign up without PC.
    # Let me check signup updates manually later.

    if content != orig_content:
        with open(p, 'w', encoding='utf-8') as f:
            f.write(content)
        print("Updated:", p)

for p in files:
    if '-old' in p.lower() or ' - old' in p.lower():
        continue
    process_file(p)

print("Done refactoring backend.")
