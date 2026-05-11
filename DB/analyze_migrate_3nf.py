import re
import csv
import io

def write_batched_inserts(header, vals, out_list, batch_size=100):
    if not vals: return
    for i in range(0, len(vals), batch_size):
        batch = vals[i:i+batch_size]
        out_list.append(header)
        out_list.append(",\n".join(batch) + ";")

def escape_sql(val):
    if val is None or val == 'NULL': return 'NULL'
    if str(val).isdigit(): return str(val)
    # escape single quotes
    return f"'{str(val).replace(chr(39), chr(92)+chr(39))}'"

def clean_pcname(pcname):
    if not pcname or pcname == 'NULL' or pcname.strip() == '':
        return 'Unknown PC'
    return pcname

def parse_sql():
    with open('pcconnect.sql', 'r', encoding='utf-8') as f:
        content = f.read()

    def get_rows(table_name):
        pattern = re.compile(f"INSERT INTO `{table_name}` \([^\)]+\) VALUES\s*([\s\S]*?);")
        inserts = pattern.findall(content)
        res = []
        for block in inserts:
            for row in block.split('),'):
                row_str = row.strip().strip(';').strip('(').strip(')')
                if not row_str: continue
                try:
                    cols = list(csv.reader(io.StringIO(row_str), quotechar="'", escapechar='\\', doublequote=False, skipinitialspace=True))[0]
                    res.append(cols)
                except Exception as e:
                    pass
        return res

    # 1. Parse apikeys
    apikeys = {} # username -> api_key
    for cols in get_rows('apikeys'):
        apikeys[cols[0]] = cols[1]

    # 2. Parse mailing_list
    mailing_user_ids = set()
    for cols in get_rows('mailing_list'):
        try:
            mailing_user_ids.add(int(cols[1]))
        except: pass

    # 3. Parse requests
    requests_map = {} # (username, pcname) -> (request, value)
    for cols in get_rows('requests'):
        # cols: ID, username, pcname, request, value
        uname, pcname, req, val = cols[1], clean_pcname(cols[2]), cols[3], cols[4]
        requests_map[(uname, pcname)] = (req, val)

    # 4. Parse time
    time_map = {} # (username, pcname) -> time
    for cols in get_rows('time'):
        # cols: ID, username, pcname, time
        uname, pcname, time_val = cols[1], clean_pcname(cols[2]), cols[3]
        time_map[(uname, pcname)] = time_val

    # 5. Parse users
    users_uname_to_id = {}
    users_output = []
    for cols in get_rows('users'):
        try:
            uid = int(cols[0])
            uname = cols[2]
            users_uname_to_id[uname] = uid
            
            mailing = "1" if uid in mailing_user_ids else "0"
            api_key = apikeys.get(uname, 'NULL')
            
            new_cols = []
            for c in cols: new_cols.append(escape_sql(c))
            new_cols.append(f"'{mailing}'")
            if api_key == 'NULL': new_cols.append("NULL")
            else: new_cols.append(escape_sql(api_key))
            
            users_output.append("(" + ", ".join(new_cols) + ")")
        except: pass

    # 6. Parse pcnames and add requests + time into it
    pcnames_map = {} # (uid, pcname) -> pcid
    pc_auto_incr = 1
    pcs_output = []
    
    # original pcnames
    for cols in get_rows('pcnames'):
        pcid = int(cols[0])
        uname = cols[1]
        pcname = clean_pcname(cols[2])
        if uname in users_uname_to_id:
            uid = users_uname_to_id[uname]
            pcnames_map[(uid, pcname)] = pcid
            if pcid >= pc_auto_incr: pc_auto_incr = pcid + 1
            
            req, val = requests_map.get((uname, pcname), ('0', '0'))
            time_val = time_map.get((uname, pcname), 'NULL')
            
            out_tup = f"({pcid}, {uid}, {escape_sql(pcname)}, {escape_sql(req)}, {escape_sql(str(val))}, {escape_sql(time_val)})"
            pcs_output.append(out_tup)

    # some requests/times might have existed for PCNames that weren't in pcnames table! Group them up securely
    for (uname, pcname), (req, val) in requests_map.items():
        if uname in users_uname_to_id:
            uid = users_uname_to_id[uname]
            if (uid, pcname) not in pcnames_map:
                pcnames_map[(uid, pcname)] = pc_auto_incr
                time_val = time_map.get((uname, pcname), 'NULL')
                out_tup = f"({pc_auto_incr}, {uid}, {escape_sql(pcname)}, {escape_sql(req)}, {escape_sql(str(val))}, {escape_sql(time_val)})"
                pcs_output.append(out_tup)
                pc_auto_incr += 1

    for (uname, pcname), time_val in time_map.items():
        if uname in users_uname_to_id:
            uid = users_uname_to_id[uname]
            if (uid, pcname) not in pcnames_map:
                pcnames_map[(uid, pcname)] = pc_auto_incr
                out_tup = f"({pc_auto_incr}, {uid}, {escape_sql(pcname)}, '0', 0, {escape_sql(time_val)})"
                pcs_output.append(out_tup)
                pc_auto_incr += 1

    # 7. Parse reminders
    reminders_output = []
    for cols in get_rows('reminders'):
        uname = cols[1]
        if uname in users_uname_to_id:
            cols[1] = str(users_uname_to_id[uname])
            new_cols = []
            for c in cols: new_cols.append(escape_sql(c))
            reminders_output.append("(" + ", ".join(new_cols) + ")")


    # Generating SQL Output file 
    out_lines = []
    out_lines.append("SET FOREIGN_KEY_CHECKS = 0;")
    
    # USERS
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
    out_lines.append("  `api_key` varchar(512) DEFAULT NULL,")
    out_lines.append("  PRIMARY KEY (`id`)")
    out_lines.append(") ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;")
    
    write_batched_inserts("INSERT INTO `users` (`id`, `Name`, `Username`, `DateOfBirth`, `Email`, `Password`, `Enabled`, `DateTimeOfSignup`, `MailingList`, `api_key`) VALUES", users_output, out_lines)

    # PCNAMES
    out_lines.append("CREATE TABLE `pcnames` (")
    out_lines.append("  `PCID` int NOT NULL AUTO_INCREMENT,")
    out_lines.append("  `UserID` int NOT NULL,")
    out_lines.append("  `PCName` varchar(255) NOT NULL,")
    out_lines.append("  `Request` varchar(512) DEFAULT '0',")
    out_lines.append("  `Value` int DEFAULT 0,")
    out_lines.append("  `Time` varchar(255) DEFAULT NULL,")
    out_lines.append("  PRIMARY KEY (`PCID`),")
    out_lines.append("  FOREIGN KEY (`UserID`) REFERENCES `users`(`id`) ON DELETE CASCADE")
    out_lines.append(") ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;")
    
    write_batched_inserts("INSERT INTO `pcnames` (`PCID`, `UserID`, `PCName`, `Request`, `Value`, `Time`) VALUES", pcs_output, out_lines)

    # REMINDERS
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
    
    write_batched_inserts("INSERT INTO `reminders` (`ID`, `UserID`, `Time`, `Reminder`, `Completed`, `Recurrence`, `Recurrence_Frequency`, `Recurrence_Day`, `Recurrence_Time`, `Recurrence_End_Date`, `Date`) VALUES", reminders_output, out_lines)

    # Keep other standalone tables dynamically
    lines = content.split('\n')
    i = 0
    while i < len(lines):
        line = lines[i]
        
        # skip old tables completely
        if any(line.startswith(f"CREATE TABLE `{t}`") for t in ['apikeys', 'mailing_list', 'pcnames', 'reminders', 'requests', 'time', 'users']):
            while i < len(lines) and not "ENGINE=" in lines[i]: i+=1
            i += 1
            continue
            
        if any(line.startswith(f"INSERT INTO `{t}`") for t in ['apikeys', 'mailing_list', 'pcnames', 'reminders', 'requests', 'time', 'users']) or \
           any(line.startswith(f"-- Dumping data for table `{t}`") for t in ['apikeys', 'mailing_list', 'pcnames', 'reminders', 'requests', 'time', 'users']):
            while i < len(lines) and not ';' in lines[i]: i+=1
            i += 1
            continue
            
        if line.startswith("ALTER TABLE") or "Indexes for table" in line:
            # Skip all alter statements if they're for the ones we dropped or re-generated constraints explicitly for
            if any(f"`{t}`" in line for t in ['apikeys', 'mailing_list', 'pcnames', 'reminders', 'requests', 'time', 'users']):
                while i < len(lines) and not ';' in lines[i]: i+=1
                i += 1
                continue
                
        if line.startswith(") ENGINE=InnoDB"):
            line = line.replace("utf8mb3", "utf8mb4 COLLATE=utf8mb4_general_ci")
            
        out_lines.append(line)
        i+=1
        
    out_lines.append("SET FOREIGN_KEY_CHECKS = 1;")
    
    with open('pcconnect_new.sql', 'w', encoding='utf-8') as f:
        f.write('\n'.join(out_lines))
        
    print(f"Generated pcconnect_new.sql dynamically natively merging requests/times/apikeys!")

if __name__ == '__main__':
    parse_sql()
