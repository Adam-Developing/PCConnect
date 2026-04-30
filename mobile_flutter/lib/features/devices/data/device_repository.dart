import 'package:flutter_riverpod/flutter_riverpod.dart';
import '../../../app/providers.dart';
import '../../../core/network/api_client.dart';
import '../../../core/security/secure_storage_service.dart';

final deviceRepositoryProvider = Provider<DeviceRepository>((ref) {
  return DeviceRepository(
    ref.watch(apiClientProvider),
    ref.watch(secureStorageProvider),
  );
});

class DeviceRepository {
  final ApiClient _apiClient;
  final SecureStorageService _storageService;

  DeviceRepository(this._apiClient, this._storageService);

  Future<List<String>> getDevices() async {
    final response = await _apiClient.get('v1/devices');
    final data = response['data'];
    if (data != null && data['PCNames'] != null) {
        return List<String>.from(data['PCNames']);
    }
    return [];
  }

  Future<void> setActiveDevice(String pcName) async {
    await _storageService.writePcName(pcName);
    // Optionally ping the POST endpoint to ensure the device exists
    try {
      await _apiClient.post('v1/devices', {});
    } catch (_) {
      // ignore
    }
  }

  Future<String?> getActiveDevice() async {
    return await _storageService.readPcName();
  }
}
