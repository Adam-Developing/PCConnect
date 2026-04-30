const express = require('express');
const router = express.Router();
const pool = require('./db');
const { Response, requireAuth, requirePC, encryptString, fetchUserReminders } = require('./helpers');

function getPushManager() {
    try {
        return require('./server').PushManager;
    } catch (e) {
        return null;
    }
}

function normalizeTimeToSql(timeInput) {
    const value = (timeInput || '').toString().trim().toUpperCase();
    const twelveHour = /^(\d{1,2}):(\d{2})\s*(AM|PM)$/;
    const twentyFourHour = /^(\d{1,2}):(\d{2})(?::(\d{2}))?$/;

    let match = value.match(twelveHour);
    if (match) {
        let hour = parseInt(match[1], 10);
        const minute = parseInt(match[2], 10);
        const ampm = match[3];
        if (hour < 1 || hour > 12 || minute < 0 || minute > 59) return null;
        if (ampm === 'PM' && hour !== 12) hour += 12;
        if (ampm === 'AM' && hour === 12) hour = 0;
        return `${String(hour).padStart(2, '0')}:${String(minute).padStart(2, '0')}:00`;
    }

    match = value.match(twentyFourHour);
    if (match) {
        const hour = parseInt(match[1], 10);
        const minute = parseInt(match[2], 10);
        const second = match[3] ? parseInt(match[3], 10) : 0;
        if (hour < 0 || hour > 23 || minute < 0 || minute > 59 || second < 0 || second > 59) return null;
        return `${String(hour).padStart(2, '0')}:${String(minute).padStart(2, '0')}:${String(second).padStart(2, '0')}`;
    }

    return null;
}

function normalizeDateToSql(dateInput) {
    const value = (dateInput || '').toString().trim();
    const parsed = new Date(value);
    if (Number.isNaN(parsed.getTime())) return null;
    return parsed.toISOString().slice(0, 10);
}

async function emitReminderUpdate(userId, apiKey, type, reminderId = null) {
    const PushManager = getPushManager();
    if (!PushManager) return;
    const reminders = await fetchUserReminders(userId, apiKey);
    let changedReminder = null;
    if (reminderId !== null) {
        changedReminder = reminders.find((r) => Number(r.ID) === Number(reminderId)) || null;
    }
    PushManager.pushReminderUpdate(userId, {
        type,
        reminder: changedReminder,
        reminders
    });
}

// ==== AUTHENTICATION ====
router.post('/auth/login', async (req, res) => {
    let { loginUsername, loginPassword } = req.body;
    loginUsername = (loginUsername || '').toString().trim();
    loginPassword = (loginPassword || '').toString().trim();

    if (!loginUsername || !loginPassword) {
        return Response.error(res, "Missing credentials", 400);
    }
    if (loginUsername.length > 255) {
        return Response.error(res, "Username format invalid or too long", 400);
    }

    try {
        let query = "SELECT api_key FROM users WHERE Username = ? AND Password = ? AND Enabled = 1";
        if (loginUsername.includes('@')) {
            query = "SELECT api_key FROM users WHERE Email = ? AND Password = ? AND Enabled = 1";
        }

        const [rows] = await pool.query(query, [loginUsername, loginPassword]);
        if (rows.length > 0) {
            return Response.success(res, { api_key: rows[0].api_key });
        } else {
            return Response.error(res, "Invalid username or password.", 401);
        }
    } catch (err) {
        console.error("LOGIN DB ERROR:", err.message);
        return Response.error(res, "Database error", 500);
    }
});

// ==== DEVICE MANAGEMENT ====
router.get('/v1/devices', requireAuth, async (req, res) => {
    try {
        const [rows] = await pool.query("SELECT PCName FROM pcnames WHERE UserID = ?", [req.userId]);
        const pcNames = rows.map(r => r.PCName);
        return Response.success(res, { PCNames: pcNames });
    } catch (e) {
        return Response.error(res, "Database error", 500);
    }
});

router.post('/v1/devices', requirePC, (req, res) => {
    return Response.success(res, { message: "PC added successfully" });
});

router.get('/v1/system/checkinternet', (req, res) => {
    return Response.json(res, "Pong", 200);
});

// ==== REMOTE REQUESTS (Standard Pull method, but WebSockets make this legacy) ====
router.get('/v1/devices/requests', requirePC, async (req, res) => {
    try {
        const [rows] = await pool.query("SELECT Request FROM pcnames WHERE Value = 1 AND PCID = ?", [req.pcId]);
        if (rows.length > 0 && rows[0].Request) {
            const reqString = rows[0].Request.replace(/^,+/, '').trim();
            return Response.success(res, { request: reqString });
        } else {
            return Response.success(res, { request: null });
        }
    } catch (e) {
        return Response.error(res, "Database error", 500);
    }
});

router.post('/v1/devices/requests/clear', requirePC, async (req, res) => {
    try {
        await pool.query("UPDATE pcnames SET Value = 0, Request = '0' WHERE PCID = ?", [req.pcId]);
        return Response.success(res, { message: "Request cleared properly" });
    } catch (e) {
        return Response.error(res, "Database error", 500);
    }
});

// The phone hits this endpoint to push a command!
router.post('/v1/devices/requests/exchange', requirePC, async (req, res) => {
    let requestData = req.body.Request || '';
    requestData = String(requestData).trim().replace(/<[^>]*>?/gm, '');

    if (!requestData) {
        return Response.error(res, "Missing parameter: Request", 400);
    }
    if (requestData.length > 500) {
        return Response.error(res, "Request parameter exceeds maximum length", 400);
    }

    try {
        await pool.query("UPDATE pcnames SET Value = 1, Request = ? WHERE PCID = ?", [requestData, req.pcId]);

        // INSTANT WEBSOCKET PUSH!
        // We emit it directly to the exact target PC.
        const PushManager = getPushManager();
        if (PushManager) {
            PushManager.pushCommand(req.userId, req.pcId, requestData);
        }

        return Response.success(res, { message: "Success" });
    } catch (e) {
        return Response.error(res, "Database error", 500);
    }
});

// ==== REMINDERS ====
router.get('/v1/reminders', requireAuth, async (req, res) => {
    try {
        const apiKey = req.header('X-API-Key') || req.header('x-api-key');
        const mapped = await fetchUserReminders(req.userId, apiKey);
        return Response.json(res, mapped);
    } catch (e) {
        return Response.error(res, "Database error", 500);
    }
});

router.post('/v1/reminders', requireAuth, async (req, res) => {
    const apiKey = req.header('X-API-Key') || req.header('x-api-key');
    const date = normalizeDateToSql(req.body.date || req.body.Date);
    const time = normalizeTimeToSql(req.body.time || req.body.Time);
    const reminderText = (req.body.reminder || req.body.Reminder || '').toString().trim();
    const completed = Number(req.body.completed || req.body.Completed || 0) ? 1 : 0;

    if (!date || !time || !reminderText) {
        return Response.error(res, "Missing or invalid reminder fields", 400);
    }
    if (reminderText.length > 2000) {
        return Response.error(res, "Reminder exceeds maximum length", 400);
    }

    const encryptedReminder = encryptString(reminderText, apiKey);
    if (!encryptedReminder) {
        return Response.error(res, "Failed to encrypt reminder", 500);
    }

    try {
        const [result] = await pool.query(
            "INSERT INTO reminders (UserID, Date, Time, Reminder, Completed) VALUES (?, ?, ?, ?, ?)",
            [req.userId, date, time, encryptedReminder, completed]
        );
        await emitReminderUpdate(req.userId, apiKey, 'created', result.insertId);
        return Response.success(res, { message: "Reminder created", id: result.insertId }, 201);
    } catch (e) {
        return Response.error(res, "Database error", 500);
    }
});

router.put('/v1/reminders/:id', requireAuth, async (req, res) => {
    const apiKey = req.header('X-API-Key') || req.header('x-api-key');
    const reminderId = Number(req.params.id);
    if (!Number.isInteger(reminderId) || reminderId <= 0) {
        return Response.error(res, "Invalid reminder ID", 400);
    }

    const updates = [];
    const values = [];

    if (req.body.date !== undefined || req.body.Date !== undefined) {
        const date = normalizeDateToSql(req.body.date || req.body.Date);
        if (!date) return Response.error(res, "Invalid date format", 400);
        updates.push("Date = ?");
        values.push(date);
    }

    if (req.body.time !== undefined || req.body.Time !== undefined) {
        const time = normalizeTimeToSql(req.body.time || req.body.Time);
        if (!time) return Response.error(res, "Invalid time format", 400);
        updates.push("Time = ?");
        values.push(time);
    }

    if (req.body.reminder !== undefined || req.body.Reminder !== undefined) {
        const reminderText = (req.body.reminder || req.body.Reminder || '').toString().trim();
        if (!reminderText) return Response.error(res, "Reminder cannot be empty", 400);
        if (reminderText.length > 2000) return Response.error(res, "Reminder exceeds maximum length", 400);
        const encryptedReminder = encryptString(reminderText, apiKey);
        if (!encryptedReminder) return Response.error(res, "Failed to encrypt reminder", 500);
        updates.push("Reminder = ?");
        values.push(encryptedReminder);
    }

    if (req.body.completed !== undefined || req.body.Completed !== undefined) {
        const completed = Number(req.body.completed || req.body.Completed) ? 1 : 0;
        updates.push("Completed = ?");
        values.push(completed);
    }

    if (updates.length === 0) {
        return Response.error(res, "No fields to update", 400);
    }

    try {
        const [exists] = await pool.query("SELECT ID FROM reminders WHERE ID = ? AND UserID = ?", [reminderId, req.userId]);
        if (exists.length === 0) {
            return Response.error(res, "Reminder not found", 404);
        }

        values.push(reminderId, req.userId);
        await pool.query(`UPDATE reminders SET ${updates.join(', ')} WHERE ID = ? AND UserID = ?`, values);
        await emitReminderUpdate(req.userId, apiKey, 'updated', reminderId);
        return Response.success(res, { message: "Reminder updated", id: reminderId });
    } catch (e) {
        return Response.error(res, "Database error", 500);
    }
});

router.post('/v1/reminders/:id/complete', requireAuth, async (req, res) => {
    const apiKey = req.header('X-API-Key') || req.header('x-api-key');
    const reminderId = Number(req.params.id);
    if (!Number.isInteger(reminderId) || reminderId <= 0) {
        return Response.error(res, "Invalid reminder ID", 400);
    }

    const completed = Number(req.body.completed) === 0 ? 0 : 1;

    try {
        const [result] = await pool.query(
            "UPDATE reminders SET Completed = ? WHERE ID = ? AND UserID = ?",
            [completed, reminderId, req.userId]
        );
        if (result.affectedRows === 0) {
            return Response.error(res, "Reminder not found", 404);
        }
        await emitReminderUpdate(req.userId, apiKey, 'updated', reminderId);
        return Response.success(res, { message: "Reminder completion updated", id: reminderId, completed });
    } catch (e) {
        return Response.error(res, "Database error", 500);
    }
});

// ==== ACCOUNT MANAGEMENT ====
router.get('/v1/account/profile', requireAuth, async (req, res) => {
    try {
        const [rows] = await pool.query("SELECT Name, Username, Email FROM users WHERE id = ?", [req.userId]);
        if (rows.length === 0) return Response.error(res, "User not found", 404);
        return Response.success(res, rows[0]);
    } catch (e) {
        return Response.error(res, "Database error", 500);
    }
});

router.put('/v1/account/profile', requireAuth, async (req, res) => {
    const { name, email, oldPassword, newPassword } = req.body;
    const updates = [];
    const values = [];

    if (name) {
        updates.push("Name = ?");
        values.push(name.toString().trim());
    }

    if (email) {
        // Basic check
        if (email.includes('@')) {
            updates.push("Email = ?");
            values.push(email.toString().trim());
        } else {
            return Response.error(res, "Invalid email format", 400);
        }
    }

    try {
        if (newPassword) {
            if (!oldPassword) return Response.error(res, "Current password required to set new password", 400);
            
            const [user] = await pool.query("SELECT Password FROM users WHERE id = ?", [req.userId]);
            if (user[0].Password !== oldPassword) {
                return Response.error(res, "Current password incorrect", 401);
            }
            
            updates.push("Password = ?");
            values.push(newPassword);
        }

        if (updates.length === 0) {
            return Response.error(res, "No fields to update", 400);
        }

        values.push(req.userId);
        await pool.query(`UPDATE users SET ${updates.join(', ')} WHERE id = ?`, values);
        
        return Response.success(res, { message: "Profile updated successfully" });
    } catch (e) {
        console.error("PROFILE UPDATE ERROR:", e);
        if (e.code === 'ER_DUP_ENTRY') return Response.error(res, "Email already in use", 400);
        return Response.error(res, "Database error", 500);
    }
});

module.exports = router;
