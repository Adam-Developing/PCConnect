const pool = require('./db');
const crypto = require('crypto');

const Response = {
    json: (res, data, statusCode = 200) => {
        res.status(statusCode).json(data);
    },
    error: (res, message, statusCode = 400) => {
        res.status(statusCode).json({ error: true, message });
    },
    success: (res, data = null, statusCode = 200) => {
        const payload = { success: true };
        if (data !== null) Object.assign(payload, { data });
        res.status(statusCode).json(payload);
    }
};

/**
 * Express Middleware to resolve UserID and PCID from headers
 */
const requireAuth = async (req, res, next) => {
    const apiKey = req.header('X-API-Key') || req.header('x-api-key');
    if (!apiKey) return Response.error(res, "Missing API Key", 401);

    try {
        const [rows] = await pool.query("SELECT id FROM users WHERE api_key = ?", [apiKey]);
        if (rows.length === 0) return Response.error(res, "Invalid API key", 401);

        req.userId = rows[0].id;
        next();
    } catch (err) {
        return Response.error(res, "Database error", 500);
    }
};

const requirePC = async (req, res, next) => {
    // If not already authenticated in this loop
    if (!req.userId) {
        const apiKey = req.header('X-API-Key') || req.header('x-api-key');
        if (!apiKey) return Response.error(res, "Missing API Key", 401);
        const [userRows] = await pool.query("SELECT id FROM users WHERE api_key = ?", [apiKey]);
        if (userRows.length === 0) return Response.error(res, "Invalid API key", 401);
        req.userId = userRows[0].id;
    }

    const pcNameRaw = req.header('PCName') || req.header('pcname');
    const pcName = pcNameRaw ? String(pcNameRaw).trim().replace(/<[^>]*>?/gm, '') : ''; // Basic tag strip

    if (!pcName || pcName.length === 0) return Response.error(res, "Missing PCName header", 400);
    if (pcName.length > 100) return Response.error(res, "PCName exceeds maximum length", 400);

    req.pcName = pcName;

    try {
        let [rows] = await pool.query("SELECT PCID FROM pcnames WHERE UserID = ? AND PCName = ?", [req.userId, pcName]);

        if (rows.length > 0) {
            req.pcId = rows[0].PCID;
        } else {
            // Auto register
            const [result] = await pool.query("INSERT INTO pcnames (UserID, PCName, Request, Value) VALUES (?, ?, '0', 0)", [req.userId, pcName]);
            req.pcId = result.insertId;
        }
        next();
    } catch (err) {
        return Response.error(res, "Database error on PC validation", 500);
    }
};

// Decryption for Reminders identical to PHP implementation
function decryptString(dataB64, apiKey) {
    try {
        const cipherProtocol = 'aes-256-cbc';
        const rawData = Buffer.from(dataB64, 'base64');
        const ivlen = 16; // openssl_cipher_iv_length('aes-256-cbc') is 16
        const iv = rawData.slice(0, ivlen);
        let encrypted = rawData.slice(ivlen);

        // Backward compatibility: If the encrypted part is a Base64 string (legacy PHP behavior), decode it
        const encryptedStr = encrypted.toString('utf8');
        if (encryptedStr.match(/^[A-Za-z0-9+/]+={0,2}$/) && encryptedStr.length > 0) {
            const decoded = Buffer.from(encryptedStr, 'base64');
            // Ciphertext must be multiple of 16 for AES-CBC
            if (decoded.length % 16 === 0 && decoded.length > 0) {
                encrypted = decoded;
            }
        }

        const decipher = crypto.createDecipheriv(cipherProtocol, apiKey, iv);
        decipher.setAutoPadding(true);
        let decrypted = decipher.update(encrypted, undefined, 'utf8');
        decrypted += decipher.final('utf8');
        return decrypted;
    } catch (e) {
        console.error("DECRYPT ERR:", e.message);
        return "Error decrypting";
    }
}

function encryptString(plainText, apiKey) {
    try {
        const cipherProtocol = 'aes-256-cbc';
        const iv = crypto.randomBytes(16);
        const cipher = crypto.createCipheriv(cipherProtocol, apiKey, iv);
        cipher.setAutoPadding(true);
        const encrypted = Buffer.concat([cipher.update(String(plainText), 'utf8'), cipher.final()]);
        return Buffer.concat([iv, encrypted]).toString('base64');
    } catch (e) {
        console.error("ENCRYPT ERR:", e.message);
        return null;
    }
}

function formatReminderDate(dateValue) {
    if (!dateValue) return '';
    const d = new Date(dateValue);
    if (Number.isNaN(d.getTime())) return '';
    const dd = String(d.getDate()).padStart(2, '0');
    const mm = String(d.getMonth() + 1).padStart(2, '0');
    const yy = String(d.getFullYear()).slice(-2);
    return `${dd}/${mm}/${yy}`;
}

function validatePassword(password) {
    // Requirements: min 8 chars, at least one letter, one number, and one special character
    if (password.length < 8) return "Password must be at least 8 characters long";
    if (!/[A-Za-z]/.test(password)) return "Password must contain at least one letter";
    if (!/[0-9]/.test(password)) return "Password must contain at least one number";
    if (!/[!@#$%^&*(),.?":{}|<>]/.test(password)) return "Password must contain at least one special character";
    return null;
}

function mapReminderRow(row, apiKey) {
    return {
        ...row,
        Reminder: decryptString(row.Reminder, apiKey),
        Date: formatReminderDate(row.Date)
    };
}

async function fetchUserReminders(userId, apiKey, dbPool = pool) {
    const [rows] = await dbPool.query(
        "SELECT ID, UserID as Username, Date, Time, Reminder, Completed FROM reminders WHERE UserID = ? ORDER BY Date DESC, Time DESC",
        [userId]
    );
    return rows.map((row) => mapReminderRow(row, apiKey));
}

module.exports = {
    Response, requireAuth, requirePC, decryptString, encryptString, fetchUserReminders, validatePassword
};
