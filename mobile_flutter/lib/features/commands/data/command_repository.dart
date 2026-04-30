import 'package:flutter_riverpod/flutter_riverpod.dart';
import '../../../app/providers.dart';
import '../../../core/network/api_client.dart';

final commandRepositoryProvider = Provider<CommandRepository>((ref) {
  return CommandRepository(ref.watch(apiClientProvider));
});

class CommandRepository {
  final ApiClient _apiClient;

  CommandRepository(this._apiClient);

  Future<void> sendCommand(String command) async {
    // The node API specification expects:
    // POST /v1/devices/requests/exchange
    // Payload: { "command": command }
    // The PCName and API key is included in the ApiClient interceptor implicitly.
    // However, explicitly ensuring it is added as a safety net.
    final pcName = await _apiClient.storageService.readPcName();
    
    await _apiClient.post(
      'v1/devices/requests/exchange', 
      {
        'Request': command,
      },
      headers: pcName != null ? {
        'PCName': pcName,
        'pcname': pcName,
      } : null,
    );
  }
}
