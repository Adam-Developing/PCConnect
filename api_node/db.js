const fs = require('fs');
const path = require('path');
const mysql = require('mysql2/promise');

// Read native config.json
function getDbConfig() {
    try {
        const configRaw = fs.readFileSync(path.join(__dirname, 'config.json'), 'utf8');
        return JSON.parse(configRaw);
    } catch (e) {
        console.error("Failed to parse config.json. Generating default.", e);
        return {
            host: 'localhost',
            user: 'root',
            password: '',
            database: 'pcconnect_new'
        };
    }
}

// Create connection pool
const pool = mysql.createPool({
    ...getDbConfig(),
    waitForConnections: true,
    connectionLimit: 10,
    queueLimit: 0
});

module.exports = pool;
