import 'package:flutter_secure_storage/flutter_secure_storage.dart';

class SecureStorageService {
  final _storage = const FlutterSecureStorage();

  Future<void> writeApiKey(String key) async {
    await _storage.write(key: 'api_key', value: key);
  }

  Future<String?> readApiKey() async {
    return await _storage.read(key: 'api_key');
  }

  Future<void> deleteApiKey() async {
    await _storage.delete(key: 'api_key');
  }

  Future<void> writePcName(String pcName) async {
    await _storage.write(key: 'pc_name', value: pcName);
  }

  Future<String?> readPcName() async {
    return await _storage.read(key: 'pc_name');
  }

  Future<void> writeBiometricEnabled(bool enabled) async {
    await _storage.write(key: 'biometric_enabled', value: enabled.toString());
  }

  Future<bool> readBiometricEnabled() async {
    final value = await _storage.read(key: 'biometric_enabled');
    return value == 'true';
  }
}
