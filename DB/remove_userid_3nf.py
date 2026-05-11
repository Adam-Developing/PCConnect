import os
import glob
import re

files_to_fix = [
    r'C:\Users\Adam\Documents\Filen\Projects\PCConnect\api\pcclient\addpc.php',
    r'C:\Users\Adam\Documents\Filen\Projects\PCConnect\api\pcclient\findrequests.php',
    r'C:\Users\Adam\Documents\Filen\Projects\PCConnect\api\pcclient\updatepctimedatabase.php',
    r'C:\Users\Adam\Documents\Filen\Projects\PCConnect\api\pcclient\updaterequest.php',
    r'C:\Users\Adam\Documents\Filen\Projects\PCConnect\api\pcconnect\exchange.php',
    r'C:\Users\Adam\Documents\Filen\Projects\PCConnect\api\pcconnect\pctime.php',
    r'C:\Users\Adam\Documents\Filen\Projects\PCConnect\api\signup.php'
]

for p in files_to_fix:
    if not os.path.exists(p): continue
    with open(p, 'r', encoding='utf-8') as f:
        content = f.read()

    # addpc.php
    if "addpc.php" in p:
        content = content.replace("INSERT INTO requests (UserID, PCID, Request, Value) VALUES (?, ?, ?, ?)", "INSERT INTO requests (PCID, Request, Value) VALUES (?, ?, ?)")
        content = content.replace('$insertRequestStmt->bind_param("iisi", $UserID, $PCID, $DefaultValue, $DefaultValue);', '$insertRequestStmt->bind_param("isi", $PCID, $DefaultValue, $DefaultValue);')
        content = content.replace("INSERT INTO time (UserID, PCID) VALUES (?, ?)", "INSERT INTO time (PCID) VALUES (?)")
        content = content.replace('$insertTimeStmt->bind_param("ii", $UserID, $PCID);', '$insertTimeStmt->bind_param("i", $PCID);')

    # findrequests.php
    elif "findrequests.php" in p:
        content = content.replace("AND UserID = ? AND PCID = ?", "AND PCID = ?")
        content = content.replace('$requestStmt->bind_param("ii", $UserID, $PCID);', '$requestStmt->bind_param("i", $PCID);')

    # updatepctimedatabase.php
    elif "updatepctimedatabase.php" in p:
        content = content.replace("WHERE UserID = ? and PCID = ?", "WHERE PCID = ?")
        content = content.replace('$updateStmt->bind_param("sii", $Time, $UserID, $PCID);', '$updateStmt->bind_param("si", $Time, $PCID);')

    # updaterequest.php
    elif "updaterequest.php" in p:
        content = content.replace("WHERE UserID = ? AND PCID = ?", "WHERE PCID = ?")
        content = content.replace('$updateStmt->bind_param("ii", $UserID, $PCID);', '$updateStmt->bind_param("i", $PCID);')

    # exchange.php
    elif "exchange.php" in p:
        content = content.replace("WHERE UserID = ? AND PCID = ?", "WHERE PCID = ?")
        content = content.replace('$updateStmt->bind_param("sii", $Message, $UserID, $PCID);', '$updateStmt->bind_param("si", $Message, $PCID);')

    # pctime.php
    elif "pctime.php" in p:
        content = content.replace("WHERE UserID = ? AND PCID = ?", "WHERE PCID = ?")
        content = content.replace('$timeStmt->bind_param("ii", $UserID, $PCID);', '$timeStmt->bind_param("i", $PCID);')

    # signup.php
    elif "signup.php" in p:
        content = content.replace("INSERT INTO requests (UserID, PCID, Request, Value) VALUES (?, 1, ?, ?)", "INSERT INTO requests (PCID, Request, Value) VALUES (1, ?, ?)")
        content = content.replace('$insertRequestTemplate->bind_param("iii", $UserID, $requestValue, $requestValue);', '$insertRequestTemplate->bind_param("ii", $requestValue, $requestValue);')
        content = content.replace("INSERT INTO time (UserID, PCID) VALUES (?, 1)", "INSERT INTO time (PCID) VALUES (1)")
        content = content.replace('$insertTimeTemplate->bind_param("i", $UserID);', '')

    with open(p, 'w', encoding='utf-8') as f:
        f.write(content)
    print("Updated", p)

