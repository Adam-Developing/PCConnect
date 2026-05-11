import re

def main():
    with open('pcconnect.sql', 'r', encoding='utf-8') as f:
        content = f.read()

    # Extract users
    user_pattern = re.compile(r"INSERT INTO `users` \([^)]+\) VALUES\s+(.*?);", re.DOTALL)
    m = user_pattern.search(content)
    users = {}
    if m:
        vals = m.group(1)
        # We need a robust parser since strings can contain commas
        # A simple split by \),( might work better
        for row in re.findall(r"\((.*?)\)", vals):
            # Split by commas not inside quotes
            # Actually, this is just for a rough count
            elements = row.split(",")
            uid = elements[0].strip()
            # Try to get username which is the 3rd column
            # This is fragile if there are commas in Name, but let's try
            username = elements[2].strip().strip("'")
            users[username] = uid

    print(f"Total users found: {len(users)}")

    # Check apikeys
    apikeys_pattern = re.compile(r"INSERT INTO `apikeys` \([^)]+\) VALUES\s+(.*?);", re.DOTALL)
    m2 = apikeys_pattern.search(content)
    missing_users = set()
    if m2:
        vals = m2.group(1)
        for row in re.findall(r"\((.*?)\)", vals):
            parts = row.split(",")
            username = parts[0].strip().strip("'")
            if username not in users:
                missing_users.add(username)
    print(f"Apikey users missing from users table: {len(missing_users)}")
    if missing_users:
        print("Sample missing:", list(missing_users)[:5])

if __name__ == '__main__':
    main()
