import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:socket_io_client/socket_io_client.dart' as IO;
import '../security/secure_storage_service.dart';
import '../config/app_config.dart';
import '../../app/providers.dart';

final socketClientProvider = Provider<SocketClient>((ref) {
  final storage = ref.watch(secureStorageProvider);
  return SocketClient(storage);
});

final socketConnectionStateProvider = StateProvider<bool>((ref) => false);
final deviceOnlineStateProvider = StateProvider<bool>((ref) => false);

class SocketClient {
  final SecureStorageService _storageService;
  IO.Socket? _socket;

  SocketClient(this._storageService);

  void connect(WidgetRef ref) async {
    if (_socket != null && _socket!.connected) return;

    final apiKey = await _storageService.readApiKey();
    final pcName = await _storageService.readPcName();
    
    if (apiKey == null || pcName == null) return;

    final dio = ref.read(apiClientProvider).dio;
    String cookieToken = "";
    try {
      final res = await dio.post('${AppConfig.baseUrl}/v1/auth/session', data: {
        'apiKey': apiKey,
        'pcName': pcName,
        'clientType': 'mobile_controller' // Role-Based Auth Role
      });
      // Extract cookie
      final cookies = res.headers['set-cookie'];
      if (cookies != null && cookies.isNotEmpty) {
        cookieToken = cookies.first.split(';')[0];
      }
    } catch (e) {
      print("Could not obtain session cookie: $e");
      return;
    }

    _socket = IO.io(AppConfig.socketUrl, <String, dynamic>{
      'transports': ['websocket'],
      'autoConnect': false,
      'extraHeaders': {'cookie': cookieToken}
    });

    _socket!.onConnect((_) {
      print('Socket Connected Securely!');
      ref.read(socketConnectionStateProvider.notifier).state = true;
    });

    _socket!.onConnectError((data) {
      print('Socket connect error: $data');
    });

    _socket!.onDisconnect((_) {
      print('Socket Disconnected');
      ref.read(socketConnectionStateProvider.notifier).state = false;
      ref.read(deviceOnlineStateProvider.notifier).state = false; // Reset to offline
    });

    _socket!.on('auth_error', (data) {
      print('Socket Auth Error: $data');
    });

    _socket!.on('device_status', (data) {
      print('Device Status Update: $data');
      ref.read(deviceOnlineStateProvider.notifier).state = data['online'] == true;
    });

    _socket!.on('authenticated', (data) {
      print('Socket Authenticated: $data');
    });

    _socket!.connect();
  }

  void disconnect() {
    _socket?.disconnect();
    _socket = null;
  }

  IO.Socket? get socket => _socket;
}
