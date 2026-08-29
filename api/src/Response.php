<?php
namespace App;

class Response {
    public static function json($data, int $statusCode = 200) {
        http_response_code($statusCode);
        header('Content-Type: application/json; charset=utf-8');
        echo json_encode($data);
        exit(); // Always stop execution after sending a JSON response
    }

    public static function error(string $message, int $statusCode = 400) {
        self::json(['error' => true, 'message' => $message], $statusCode);
    }
    
    public static function success($data = null, int $statusCode = 200) {
        $res = ['success' => true];
        if ($data !== null) {
            $res['data'] = $data;
        }
        self::json($res, $statusCode);
    }
}
