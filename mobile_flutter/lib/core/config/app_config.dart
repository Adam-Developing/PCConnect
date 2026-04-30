class AppConfig {
  /// The local IP address of the machine running the api_node server.
  /// Change this to match your PC's IP when running locally (e.g. 192.168.0.113).
  /// Do not use 'localhost' or '127.0.0.1' as that refers to the emulator/device itself.
  static const String localIp = '192.168.0.113';
  static const String port = '3000';

  /// Base URL for REST API endpoints
  static const String baseUrl = 'http://$localIp:$port/api_node/';

  /// Base URL for WebSocket connections
  static const String socketUrl = 'http://$localIp:$port';
}
