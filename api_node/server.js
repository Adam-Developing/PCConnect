const express = require('express');
const cors = require('cors');
const http = require('http');
const { Server } = require("socket.io");
const pool = require('./db');
const { fetchUserReminders } = require('./helpers');

const cookieParser = require('cookie-parser');

const app = express();
app.use(cors({ origin: true, credentials: true })); // Allow cookies
app.use(cookieParser());
app.use(express.json());
app.use(express.urlencoded({ extended: true }));

const server = http.createServer(app);

const { crypto } = require('crypto'); // Built-in for session tokens
// ==== Cookie / Session System ====
const sessionTokens = {}; // token -> { userId, pcId, role, pcName }

// New REST Endpoint to get a secure cookie token
app.post('/v1/auth/session', async (req, res) => {
    const { apiKey, pcName, clientType } = req.body; // clientType = 'desktop' or 'mobile_controller'
    if (!apiKey || !pcName || !clientType) return res.status(400).json({ error: "Missing required fields" });

    try {
        const [userRows] = await pool.query("SELECT id FROM users WHERE api_key = ?", [apiKey]);
        if (userRows.length === 0) return res.status(401).json({ error: "Invalid API key" });
        const userId = userRows[0].id;

        const safePcName = String(pcName).trim().replace(/<[^>]*>?/gm, '');
        let [pcRows] = await pool.query("SELECT PCID FROM pcnames WHERE UserID = ? AND PCName = ?", [userId, safePcName]);
        let pcId = null;
        if (pcRows.length > 0) {
            pcId = pcRows[0].PCID;
        } else {
            const [result] = await pool.query("INSERT INTO pcnames (UserID, PCName, Request, Value) VALUES (?, ?, '0', 0)", [userId, safePcName]);
            pcId = result.insertId;
        }

        // Generate Secure Token
        const token = require('crypto').randomBytes(32).toString('hex');
        sessionTokens[token] = { userId, pcId, role: clientType, pcName: safePcName };

        // Set Cookie
        res.cookie('auth_token', token, {
            httpOnly: true,
            secure: false, // Since this runs locally
            sameSite: 'lax',
            maxAge: 24 * 60 * 60 * 1000 // 1 day
        });
        
        return res.json({ message: "Authenticated", role: clientType });
    } catch (e) {
        return res.status(500).json({ error: "DB Error" });
    }
});

// Websocket Engine attached to Express
const io = new Server(server, {
  cors: { origin: "*" }
});

// A localized Push Manager that allows REST routes to talk to web sockets
const PushManager = {
    pushCommand: (userId, pcId, commandString) => {
        const roomName = `user_${userId}_pc_${pcId}`;
        console.log(`[PUSH] Emitting command '${commandString}' to room [${roomName}]`);
        io.to(roomName).emit('execute_command', { command: commandString });
    },
    pushReminderUpdate: (userId, payload) => {
        const roomName = `user_${userId}`;
        console.log(`[PUSH] Emitting reminder update to room [${roomName}]`);
        io.to(roomName).emit('reminder_update', payload);
    }
};

module.exports.PushManager = PushManager;

// Load standard REST routes
const apiRoutes = require('./routes');
app.use('/api/index.php', apiRoutes); // Matching the exact PHP router format!
app.use('/api_node', apiRoutes);      // Native alternative path

// Default
app.get('/ping', (req, res) => res.send('Node Gateway Active'));

// ==== WebSockets Real-Time Logic ====
const connectedPCs = {}; // Track PC socket IDs by room

io.use((socket, next) => {
    // Parse cookies from standard headers
    if (socket.request.headers.cookie) {
        const cookies = require('cookie').parse(socket.request.headers.cookie);
        if (cookies.auth_token && sessionTokens[cookies.auth_token]) {
            socket.session = sessionTokens[cookies.auth_token];
            return next();
        }
    }
    return next(new Error("Authentication error: No valid cookie session found"));
});

io.on('connection', (socket) => {
    console.log(`Socket Connected: ${socket.id} with session role: ${socket.session.role}`);
    
    // Auto-authenticate because middleware succeeded
    const { userId, pcId, role, pcName } = socket.session;
    const isMobile = role === 'mobile_controller';
    const roomName = `user_${userId}_pc_${pcId}`;
    
    socket.join(roomName);
    socket.join(`user_${userId}`);
    socket.emit('authenticated', { message: "Authenticated via secure cookie session", roomId: roomName });
    console.log(`${role === 'desktop' ? 'PC' : 'Mobile App'} [${pcName}] joined room [${roomName}]`);

    if (role === 'desktop') {
        if (!connectedPCs[roomName]) connectedPCs[roomName] = new Set();
        connectedPCs[roomName].add(socket.id);
        io.to(roomName).emit('device_status', { online: true, pcName: pcName });
    }

    // Immediately send current PC status to Mobile Apps
    const isOnline = connectedPCs[roomName] && connectedPCs[roomName].size > 0;
    socket.emit('device_status', { online: isOnline, pcName: pcName });

    // Handle reminders
    socket.on('authenticate', () => { /* Ignored, handled in middleware */ });

    socket.on('disconnect', () => {
         console.log(`${role === 'desktop' ? 'PC' : 'Mobile App'} [${pcName}] disconnected smoothly.`);
         if (role === 'desktop' && connectedPCs[roomName]) {
             connectedPCs[roomName].delete(socket.id);
             if (connectedPCs[roomName].size === 0) {
                 io.to(roomName).emit('device_status', { online: false, pcName: pcName });
             }
         }
    });

});

// START SERVER
const PORT = process.env.PORT || 3000;
server.listen(PORT, '0.0.0.0', () => {
    console.log(`[PCConnect] Unified Gateway + WebSockets running on port ${PORT}`);
});
