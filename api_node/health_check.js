#!/usr/bin/env node
/**
 * Quick Database and Server Health Check
 * Verifies basic connectivity before running full test suite
 */

const mysql = require('mysql2/promise');
const fs = require('fs');
const path = require('path');

async function checkDatabase() {
    console.log('🔍 Database Connectivity Check\n');
    
    try {
        const configPath = path.join(__dirname, 'config.json');
        const config = JSON.parse(fs.readFileSync(configPath, 'utf8'));
        
        console.log(`Attempting to connect to:
  Host: ${config.host || 'localhost'}
  User: ${config.user || 'root'}
  Database: ${config.database || 'pcconnect'}`);
        
        const connection = await mysql.createConnection(config);
        console.log('✓ Database connection successful!\n');
        
        // Check users table
        try {
            const [users] = await connection.query('SELECT COUNT(*) as count FROM users');
            console.log(`✓ Users table: ${users[0].count} total users`);
        } catch (e) {
            console.log('✗ Users table not found or error:', e.message);
        }
        
        // Check reminders table
        try {
            const [reminders] = await connection.query('SELECT COUNT(*) as count FROM reminders');
            console.log(`✓ Reminders table: ${reminders[0].count} total reminders`);
        } catch (e) {
            console.log('✗ Reminders table not found or error:', e.message);
            console.log('  → Run: mysql -u root pcconnect < ../DB/pcconnect_new.sql');
        }
        
        // Check admin user
        try {
            const [admin] = await connection.query(
                'SELECT id, Username, api_key FROM users WHERE id = 1'
            );
            if (admin.length > 0) {
                console.log(`✓ Admin user found: ${admin[0].Username} (ID: ${admin[0].id})`);
                console.log(`  API Key: ${admin[0].api_key}`);
            } else {
                console.log('✗ Admin user (ID=1) not found');
            }
        } catch (e) {
            console.log('✗ Error checking admin user:', e.message);
        }
        
        // Check user API keys
        try {
            const [keys] = await connection.query(
                'SELECT COUNT(*) as count FROM users WHERE api_key IS NOT NULL'
            );
            console.log(`✓ Users with API keys: ${keys[0].count}`);
        } catch (e) {
            console.log('✗ Error checking API keys:', e.message);
        }
        
        await connection.end();
        console.log('\n✓ All database checks passed!\n');
        return true;
        
    } catch (err) {
        console.error('\n✗ Database connection failed:', err.message);
        console.log('\nTroubleshooting steps:');
        console.log('1. Ensure MySQL/MariaDB is running');
        console.log('2. Check config.json has correct host, user, password, database');
        console.log('3. Ensure pcconnect database exists');
        console.log('4. Default: host=localhost, user=root, password=<empty>, database=pcconnect\n');
        return false;
    }
}

async function checkConfig() {
    console.log('🔍 Configuration Check\n');
    
    try {
        const configPath = path.join(__dirname, 'config.json');
        if (!fs.existsSync(configPath)) {
            console.log('✗ config.json not found at:', configPath);
            console.log('\nExpected format:');
            console.log(JSON.stringify({
                host: 'localhost',
                user: 'root',
                password: '',
                database: 'pcconnect'
            }, null, 2));
            return false;
        }
        
        const config = JSON.parse(fs.readFileSync(configPath, 'utf8'));
        console.log('✓ config.json found and valid:');
        console.log(JSON.stringify(config, null, 2));
        return true;
        
    } catch (err) {
        console.error('✗ Error reading config.json:', err.message);
        return false;
    }
}

async function checkDependencies() {
    console.log('\n🔍 Dependencies Check\n');
    
    const required = ['mysql2', 'express', 'cors', 'socket.io'];
    const optional = ['socket.io-client', 'axios'];
    
    let allOk = true;
    
    console.log('Required packages:');
    for (const pkg of required) {
        try {
            require.resolve(pkg);
            console.log(`  ✓ ${pkg}`);
        } catch (e) {
            console.log(`  ✗ ${pkg} NOT FOUND`);
            allOk = false;
        }
    }
    
    console.log('\nOptional packages (needed for test_reminders.js):');
    for (const pkg of optional) {
        try {
            require.resolve(pkg);
            console.log(`  ✓ ${pkg}`);
        } catch (e) {
            console.log(`  ✗ ${pkg} NOT FOUND`);
            console.log(`    Run: npm install ${pkg}`);
        }
    }
    
    return allOk;
}

async function run() {
    console.log('\n╔════════════════════════════════════════════════════════════╗');
    console.log('║  PCConnect API Node - Health Check                         ║');
    console.log('╚════════════════════════════════════════════════════════════╝\n');
    
    const configOk = await checkConfig();
    const depsOk = await checkDependencies();
    const dbOk = configOk ? await checkDatabase() : false;
    
    console.log('\n╔════════════════════════════════════════════════════════════╗');
    console.log('║  Summary                                                   ║');
    console.log('╚════════════════════════════════════════════════════════════╝\n');
    
    if (configOk && depsOk && dbOk) {
        console.log('✓ All checks passed! Ready to run server and tests.\n');
        console.log('Next steps:');
        console.log('1. In Terminal 1: npm install socket.io-client axios');
        console.log('2. In Terminal 1: node server.js');
        console.log('3. In Terminal 2: node test_reminders.js\n');
        process.exit(0);
    } else {
        console.log('✗ Some checks failed. Fix issues above before proceeding.\n');
        process.exit(1);
    }
}

run().catch(err => {
    console.error('Fatal error:', err);
    process.exit(1);
});
