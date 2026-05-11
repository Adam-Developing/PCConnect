import re
import traceback

def write_batched_inserts(header, vals, out_list, batch_size=100):
    if not vals:
        out_list.pop() # remove initial header
        return
    out_list.pop() # we will re-insert headers
    for i in range(0, len(vals), batch_size):
        batch = vals[i:i+batch_size]
        out_list.append(header)
        out_list.append(",\n".join(batch) + ";")

def parse_sql():
    with open('pcconnect.sql', 'r', encoding='utf-8') as f:
        content = f.read()

    # Extract users: ID, Name, Username, DateOfBirth, Email, Password, Enabled, DateTimeOfSignup
    # For robust parsing, we use a regex to grab all values in INSERT INTO `users`
    user_pattern = re.compile(r"INSERT INTO `users` \([^\)]+\) VALUES\s*([\s\S]*?);")
    users_inserts = user_pattern.findall(content)
    
    users = {} # username -> id
    user_emails = {} # username -> email
    valid_pids = set() # valid user IDs
    
    for insert_block in users_inserts:
        # split by '),'
        rows = insert_block.split('),')
        for row in rows:
            row = row.strip()
            if not row: continue
            if row.startswith('('): row = row[1:]
            if row.endswith(')'): row = row[:-1]
            
            # parse values, respecting single quotes
            import csv
            import io
            reader = csv.reader(io.StringIO(row), quotechar="'", skipinitialspace=True)
            for cols in reader:
                try:
                    uid = int(cols[0])
                    username = cols[2]
                    email = cols[4]
                    users[username] = uid
                    valid_pids.add(uid)
                    user_emails[username] = email
                except Exception as e:
                    print("Error parsing user row:", row, e)

    print(f"Loaded {len(users)} valid users.")

    # mailing list table: we are dropping it, but we want to note who is in the mailing list to add MailingList to users table
    mailing_pattern = re.compile(r"INSERT INTO `mailing_list` \([^\)]+\) VALUES\s*([\s\S]*?);")
    mailings = mailing_pattern.findall(content)
    mailing_user_ids = set()
    for insert_block in mailings:
        rows = insert_block.split('),')
        for row in rows:
            row = row.strip()
            if not row: continue
            if row.startswith('('): row = row[1:]
            if row.endswith(')'): row = row[:-1]
            try:
                cols = list(csv.reader(io.StringIO(row), quotechar="'", skipinitialspace=True))[0]
                uid = int(cols[1])
                mailing_user_ids.add(uid)
            except Exception as e:
                pass


    # PCNames table: PCID -> Username -> PCName
    # We want PCName associations for our requests, and time tables which only stored (Username, PCName) 
    # instead of PCID.
    pcname_pattern = re.compile(r"INSERT INTO `pcnames` \([^\)]+\) VALUES\s*([\s\S]*?);")
    pcnew_inserts = pcname_pattern.findall(content)
    
    pcnames_map = {} # (UserID, PCName) -> PCID
    pc_auto_incr = 1
    
    valid_pcs_output = []
    
    for insert_block in pcnew_inserts:
        rows = insert_block.split('),')
        for row in rows:
            row = row.strip()
            if not row: continue
            if row.startswith('('): row = row[1:]
            if row.endswith(')'): row = row[:-1]
            try:
                cols = list(csv.reader(io.StringIO(row), quotechar="'", escapechar='\\', doublequote=False, skipinitialspace=True))[0]
                pcid = int(cols[0])
                uname = cols[1]
                pcname = cols[2].replace("'", "\\'")
                
                if uname in users:
                    uid = users[uname]
                    pcnames_map[(uid, pcname)] = pcid
                    valid_pcs_output.append(f"({pcid}, {uid}, '{pcname}')")
                    if pcid >= pc_auto_incr:
                        pc_auto_incr = pcid + 1
            except Exception as e:
                print("pcname err", e)


    out_lines = []
    lines = content.split('\n')
    i = 0
    
    while i < len(lines):
        line = lines[i]
        if line.startswith("CREATE TABLE `apikeys`"):
            out_lines.append("CREATE TABLE `apikeys` (")
            out_lines.append("  `UserID` int NOT NULL,")
            out_lines.append("  `api_key` varchar(512) NOT NULL")
            out_lines.append(") ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;")
            i += 4
            continue
            
        elif line.startswith("INSERT INTO `apikeys`"):
            out_lines.append("INSERT INTO `apikeys` (`UserID`, `api_key`) VALUES")
            i += 1
            # gather following lines until ;
            stmt = ""
            while i < len(lines):
                stmt += lines[i] + "\n"
                if ';' in lines[i]:
                    break
                i += 1
            
            # parse stmt and convert to UserID
            new_vals = []
            for row in stmt.split('),'):
                row_str = row.strip().strip(';').strip('(').strip(')')
                if not row_str: continue
                cols = list(csv.reader(io.StringIO(row_str), quotechar="'", escapechar='\\', doublequote=False, skipinitialspace=True))[0]
                uname = cols[0]
                api = cols[1].replace("'", "\\'")
                if uname in users:
                    new_vals.append(f"({users[uname]}, '{api}')")
            write_batched_inserts("INSERT INTO `apikeys` (`UserID`, `api_key`) VALUES", new_vals, out_lines)
            i += 1
            continue

        elif line.startswith("CREATE TABLE `mailing_list`"):
            # skip it
            while i < len(lines) and not "Dumping data for table" in lines[i]:
                i += 1
            continue

        elif line.startswith("-- Dumping data for table `mailing_list`"):
            # skip this and insert blocks
            while i < len(lines) and not line.startswith("CREATE TABLE"):
                i += 1
                if i < len(lines): line = lines[i]
            continue
            
        elif line.startswith("CREATE TABLE `pcnames`"):
            out_lines.append("CREATE TABLE `pcnames` (")
            out_lines.append("  `PCID` int NOT NULL AUTO_INCREMENT,")
            out_lines.append("  `UserID` int NOT NULL,")
            out_lines.append("  `PCName` varchar(255) NOT NULL,")
            out_lines.append("  PRIMARY KEY (`PCID`),")
            out_lines.append("  FOREIGN KEY (`UserID`) REFERENCES `users`(`id`) ON DELETE CASCADE")
            out_lines.append(") ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;")
            i += 5
            continue
            
        elif line.startswith("INSERT INTO `pcnames`"):
            if valid_pcs_output:
                out_lines.append("INSERT INTO `pcnames` (`PCID`, `UserID`, `PCName`) VALUES")
                write_batched_inserts("INSERT INTO `pcnames` (`PCID`, `UserID`, `PCName`) VALUES", valid_pcs_output, out_lines)
            # skip original insert chunk
            while i < len(lines) and not ';' in lines[i]:
                i += 1
            i += 1
            continue

        elif line.startswith("CREATE TABLE `reminders`"):
            out_lines.append("CREATE TABLE `reminders` (")
            out_lines.append("  `ID` int NOT NULL AUTO_INCREMENT,")
            out_lines.append("  `UserID` int NOT NULL,")
            out_lines.append("  `Time` time NOT NULL,")
            out_lines.append("  `Reminder` text NOT NULL,")
            out_lines.append("  `Completed` int NOT NULL DEFAULT '0',")
            out_lines.append("  `Recurrence` varchar(255) DEFAULT 'none',")
            out_lines.append("  `Recurrence_Frequency` varchar(255) DEFAULT NULL,")
            out_lines.append("  `Recurrence_Day` varchar(255) DEFAULT NULL,")
            out_lines.append("  `Recurrence_Time` time DEFAULT NULL,")
            out_lines.append("  `Recurrence_End_Date` date DEFAULT NULL,")
            out_lines.append("  `Date` date DEFAULT NULL,")
            out_lines.append("  PRIMARY KEY (`ID`),")
            out_lines.append("  FOREIGN KEY (`UserID`) REFERENCES `users`(`id`) ON DELETE CASCADE")
            out_lines.append(") ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;")
            while i < len(lines) and not "ENGINE=" in lines[i]: i += 1
            i += 1
            continue
            
        elif line.startswith("INSERT INTO `reminders`"):
            out_lines.append("INSERT INTO `reminders` (`ID`, `UserID`, `Time`, `Reminder`, `Completed`, `Recurrence`, `Recurrence_Frequency`, `Recurrence_Day`, `Recurrence_Time`, `Recurrence_End_Date`, `Date`) VALUES")
            i += 1
            stmt = ""
            while i < len(lines):
                stmt += lines[i] + "\n"
                if ';' in lines[i]: break
                i += 1
            new_vals = []
            for row in stmt.split('),'):
                row_str = row.strip().strip(';').strip('(').strip(')')
                if not row_str: continue
                cols = list(csv.reader(io.StringIO(row_str), quotechar="'", escapechar='\\', doublequote=False, skipinitialspace=True))[0]
                uname = cols[1]
                if uname in users:
                    cols[1] = str(users[uname])
                    # Ensure properly quoted values for remaining
                    new_cols = []
                    for c in cols:
                        if c == 'NULL' or c is None: new_cols.append('NULL')
                        elif c.isdigit(): new_cols.append(c)
                        else: new_cols.append(f"'{c.replace(chr(39), chr(92)+chr(39))}'")
                    new_vals.append("(" + ", ".join(new_cols) + ")")
            write_batched_inserts("INSERT INTO `reminders` (`ID`, `UserID`, `Time`, `Reminder`, `Completed`, `Recurrence`, `Recurrence_Frequency`, `Recurrence_Day`, `Recurrence_Time`, `Recurrence_End_Date`, `Date`) VALUES", new_vals, out_lines)
            i += 1
            continue

        elif line.startswith("CREATE TABLE `requests`"):
            out_lines.append("CREATE TABLE `requests` (")
            out_lines.append("  `ID` int NOT NULL AUTO_INCREMENT,")
            out_lines.append("  `PCID` int NOT NULL,")
            out_lines.append("  `Request` varchar(512) NOT NULL,")
            out_lines.append("  `Value` int NOT NULL,")
            out_lines.append("  PRIMARY KEY (`ID`),")
            out_lines.append("  FOREIGN KEY (`PCID`) REFERENCES `pcnames`(`PCID`) ON DELETE CASCADE")
            out_lines.append(") ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;")
            while i < len(lines) and not "ENGINE=" in lines[i]: i += 1
            i += 1
            continue
            
        elif line.startswith("INSERT INTO `requests`"):
            out_lines.append("INSERT INTO `requests` (`ID`, `PCID`, `Request`, `Value`) VALUES")
            i += 1
            stmt = ""
            while i < len(lines):
                stmt += lines[i] + "\n"
                if ';' in lines[i]: break
                i += 1
            new_vals = []
            for row in stmt.split('),'):
                row_str = row.strip().strip(';').strip('(').strip(')')
                if not row_str: continue
                # We need to use shlex or csv to handle potential internal quotes correctly. 
                # Let's fix missing ones using basic processing
                try:
                    cols = list(csv.reader(io.StringIO(row_str), quotechar="'", escapechar='\\', doublequote=False, skipinitialspace=True))[0]
                    uname = cols[1]
                    pcname = cols[2].replace("'", "\\'")
                    
                    if uname in users:
                        uid = users[uname]
                        # check PCID
                        key = (uid, pcname)
                        if key not in pcnames_map:
                            if pcname == 'NULL' or not pcname:
                                pcname = 'Unknown'
                            pcnames_map[key] = pc_auto_incr
                            valid_pcs_output.append(f"({pc_auto_incr}, {uid}, '{pcname}')")
                            pc_auto_incr += 1
                        pcid = pcnames_map[key]
                        rid = cols[0]
                        req = cols[3].replace("'", "\\'")
                        val = cols[4]
                        new_vals.append(f"({rid}, {pcid}, '{req}', {val})")
                except:
                    pass
            write_batched_inserts("INSERT INTO `requests` (`ID`, `PCID`, `Request`, `Value`) VALUES", new_vals, out_lines)
            i += 1
            continue

        elif line.startswith("CREATE TABLE `time`"):
            out_lines.append("CREATE TABLE `time` (")
            out_lines.append("  `ID` int NOT NULL AUTO_INCREMENT,")
            out_lines.append("  `PCID` int NOT NULL,")
            out_lines.append("  `Time` varchar(255) DEFAULT NULL,")
            out_lines.append("  PRIMARY KEY (`ID`),")
            out_lines.append("  FOREIGN KEY (`PCID`) REFERENCES `pcnames`(`PCID`) ON DELETE CASCADE")
            out_lines.append(") ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;")
            while i < len(lines) and not "ENGINE=" in lines[i]: i += 1
            i += 1
            continue
            
        elif line.startswith("INSERT INTO `time`"):
            out_lines.append("INSERT INTO `time` (`ID`, `PCID`, `Time`) VALUES")
            i += 1
            stmt = ""
            while i < len(lines):
                stmt += lines[i] + "\n"
                if ';' in lines[i]: break
                i += 1
            new_vals = []
            for row in stmt.split('),'):
                row_str = row.strip().strip(';').strip('(').strip(')')
                if not row_str: continue
                try:
                    cols = list(csv.reader(io.StringIO(row_str), quotechar="'", escapechar='\\', doublequote=False, skipinitialspace=True))[0]
                    uname = cols[1]
                    pcname = cols[2].replace("'", "\\'")
                    
                    if uname in users:
                        uid = users[uname]
                        key = (uid, pcname)
                        if key not in pcnames_map:
                            if pcname == 'NULL' or not pcname:
                                pcname = 'Unknown'
                            pcnames_map[key] = pc_auto_incr
                            valid_pcs_output.append(f"({pc_auto_incr}, {uid}, '{pcname}')")
                            pc_auto_incr += 1
                        pcid = pcnames_map[key]
                        rid = cols[0]
                        time_val = cols[3].replace("'", "\\'")
                        time_out = "NULL" if time_val == 'NULL' else f"'{time_val}'"
                        new_vals.append(f"({rid}, {pcid}, {time_out})")
                except:
                    pass
            write_batched_inserts("INSERT INTO `time` (`ID`, `PCID`, `Time`) VALUES", new_vals, out_lines)
            i += 1
            continue

        elif line.startswith("CREATE TABLE `users`"):
            out_lines.append("CREATE TABLE `users` (")
            out_lines.append("  `id` int NOT NULL AUTO_INCREMENT,")
            out_lines.append("  `Name` varchar(255) NOT NULL,")
            out_lines.append("  `Username` varchar(255) NOT NULL,")
            out_lines.append("  `DateOfBirth` varchar(255) NOT NULL,")
            out_lines.append("  `Email` varchar(255) NOT NULL,")
            out_lines.append("  `Password` varchar(64) NOT NULL,")
            out_lines.append("  `Enabled` tinyint(1) NOT NULL DEFAULT '1',")
            out_lines.append("  `DateTimeOfSignup` varchar(255) NOT NULL,")
            out_lines.append("  `MailingList` tinyint(1) NOT NULL DEFAULT '0',")
            out_lines.append("  PRIMARY KEY (`id`)")
            out_lines.append(") ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;")
            while i < len(lines) and not "ENGINE=" in lines[i]: i += 1
            i += 1
            continue

        elif line.startswith("INSERT INTO `users`"):
            out_lines.append("INSERT INTO `users` (`id`, `Name`, `Username`, `DateOfBirth`, `Email`, `Password`, `Enabled`, `DateTimeOfSignup`, `MailingList`) VALUES")
            i += 1
            stmt = ""
            while i < len(lines):
                stmt += lines[i] + "\n"
                if ';' in lines[i]: break
                i += 1
            new_vals = []
            for row in stmt.split('),'):
                row_str = row.strip().strip(';').strip('(').strip(')')
                if not row_str: continue
                try:
                    cols = list(csv.reader(io.StringIO(row_str), quotechar="'", escapechar='\\', doublequote=False, skipinitialspace=True))[0]
                    uid = int(cols[0])
                    mailing = "1" if uid in mailing_user_ids else "0"
                    
                    new_cols = []
                    for c in cols:
                        if c == 'NULL': new_cols.append('NULL')
                        else: new_cols.append(f"'{c.replace(chr(39), chr(92)+chr(39))}'")
                    new_cols.append(f"'{mailing}'")
                    new_vals.append("(" + ", ".join(new_cols) + ")")
                except:
                    pass
            write_batched_inserts("INSERT INTO `users` (`id`, `Name`, `Username`, `DateOfBirth`, `Email`, `Password`, `Enabled`, `DateTimeOfSignup`, `MailingList`) VALUES", new_vals, out_lines)
            i += 1
            continue

        # Convert other table encoding as well
        elif line.startswith(") ENGINE=InnoDB"):
            line = line.replace("utf8mb3", "utf8mb4 COLLATE=utf8mb4_general_ci")
            out_lines.append(line)
            i += 1
            continue
            
        elif line.startswith("ALTER TABLE") or "Indexes for table" in line:
            # We already added PRIMARY KEYs to our modified tables in CREATE TABLE.
            # Avoid repeating them in ALTER TABLE block for the ones we modified.
            pass
            
        out_lines.append(line)
        i += 1

    # Cleanup the index section by removing ALTER TABLE parts for tables we fixed inline
    final_output = []
    skip_alter = False
    for line in out_lines:
        if line.startswith("ALTER TABLE `apikeys`") or \
           line.startswith("ALTER TABLE `pcnames`") or \
           line.startswith("ALTER TABLE `reminders`") or \
           line.startswith("ALTER TABLE `requests`") or \
           line.startswith("ALTER TABLE `time`") or \
           line.startswith("ALTER TABLE `users`") or \
           line.startswith("ALTER TABLE `mailing_list`"):
            skip_alter = True
            continue
            
        if skip_alter:
            if ';' in line:
                skip_alter = False
            continue
            
        final_output.append(line)

    # Note: we need to replace pcnames data block as well since we generated it inline
    
    final_output.insert(0, "SET FOREIGN_KEY_CHECKS = 0;")
    final_output.append("SET FOREIGN_KEY_CHECKS = 1;")

    with open('pcconnect_new.sql', 'w', encoding='utf-8') as f:
        f.write('\n'.join(final_output))
        
    print("Done generating pcconnect_new.sql")

if __name__ == '__main__':
    parse_sql()
