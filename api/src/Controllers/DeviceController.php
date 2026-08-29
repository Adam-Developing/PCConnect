<?php
namespace App\Controllers;

use App\Database;
use App\Auth;
use App\Response;

class DeviceController {
    private Database $db;
    private Auth $auth;

    public function __construct(Database $db, Auth $auth) {
        $this->db = $db;
        $this->auth = $auth;
    }

    public function listDevices() {
        $userId = $this->auth->requireAuth();
        
        $stmt = $this->db->query("SELECT PCName FROM pcnames WHERE UserID = ?", [$userId]);
        $devices = $stmt->fetchAll();
        
        $pcNames = array_column($devices, 'PCName');
        Response::success(["PCNames" => $pcNames]);
    }

    public function addDevice() {
        // By calling requirePC, the Auth class automatically creates the PC if it doesn't exist
        $this->auth->requirePC();
        // Respond successful registration (if it already existed, it acts idempotently)
        Response::success(["message" => "PC added successfully"]);
    }

    public function getRequests() {
        $pcId = $this->auth->requirePC();
        
        $stmt = $this->db->query("SELECT Request FROM pcnames WHERE Value = 1 AND PCID = ?", [$pcId]);
        $request = $stmt->fetchColumn();

        if ($request !== false) {
            Response::success(["request" => ltrim(trim($request), ',')]);
        } else {
            Response::success(["request" => null]);
        }
    }

    public function clearRequests() {
        $pcId = $this->auth->requirePC();
        
        $this->db->query("UPDATE pcnames SET Value = 0, Request = '0' WHERE PCID = ?", [$pcId]);
        Response::success(["message" => "Request cleared properly"]);
    }

    public function sendExchange() {
        $userId = $this->auth->requireAuth();
        $pcId = $this->auth->requirePC();
        $requestData = $_POST['Request'] ?? json_decode(file_get_contents('php://input'), true)['Request'] ?? null;

        $requestData = trim((string)$requestData);
        if ($requestData === '') {
            Response::error("Missing parameter: Request", 400);
        }
        if (strlen($requestData) > 500) {
            Response::error("Request parameter exceeds maximum length of 500 characters", 400);
        }
        // Basic sanitization from direct HTML rendering risks
        $requestData = strip_tags($requestData);

        $this->db->query("UPDATE pcnames SET Value = 1, Request = ? WHERE PCID = ?", [$requestData, $pcId]);
        Response::success(["message" => "Success"]);
    }

    public function checkInternet() {
        Response::json("Pong", 200);
    }
}
