import pymysql
import os

connection = pymysql.connect(
    host='##',
    user='##',
    password='##',
    db='##',
    charset='utf8mb4',
    client_flag=pymysql.constants.CLIENT.MULTI_STATEMENTS
)

def run_sql_file(filename, connection):
    print(f"Reading {filename}...")
    with open(filename, 'r', encoding='utf-8') as f:
        lines = f.readlines()
        
    cursor = connection.cursor()
    statement = ''
    try:
        print("Uploading to MySQL... please wait.")
        
        # Clean slate the tables before inserting
        cursor.execute("SET FOREIGN_KEY_CHECKS=0;")
        cursor.execute("DROP TABLE IF EXISTS apikeys, code, feedback, links, menupages, pcnames, reminders, requests, time, users, verifications, verificationtypes, mailing_list;")
        cursor.execute("SET FOREIGN_KEY_CHECKS=1;")
        
        for line in lines:
            stripped = line.strip()
            
            # Ignore completely empty lines and SQL full-line comments
            if not stripped or stripped.startswith('--') or stripped.startswith('/*'):
                continue
                
            statement += line + '\n'
            
            # If the line ends with a semicolon, it's a complete query block 
            # (our migration script safely generates terminal semicolons at the end of inserts)
            if stripped.endswith(';'):
                cursor.execute(statement)
                statement = ''
                
        connection.commit()
        print("Database normalization upload completely successful!")
    except Exception as e:
        print(f"Error during execution: {e}")
        connection.rollback()
    finally:
        cursor.close()

run_sql_file('pcconnect_new.sql', connection)
connection.close()
