import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:local_auth/local_auth.dart';
import 'package:crypto/crypto.dart';
import 'dart:convert';
import '../../../app/providers.dart';
import '../../../core/network/api_client.dart';
import '../../../core/security/secure_storage_service.dart';

final authRepositoryProvider = Provider<AuthRepository>((ref) {
  return AuthRepository(
    ref.watch(apiClientProvider),
    ref.watch(secureStorageProvider),
  );
});

class AuthRepository {
  final ApiClient _apiClient;
  final SecureStorageService _storageService;
  final LocalAuthentication _localAuth = LocalAuthentication();

  AuthRepository(this._apiClient, this._storageService);

  Future<void> login(String username, String password) async {
    // legacy desktop client hashes the password with SHA-256 before sending to the backend
    final bytes = utf8.encode(password);
    final digest = sha256.convert(bytes);
    final hashedPassword = digest.toString().toLowerCase();

    final response = await _apiClient.post('auth/login', {
      'loginUsername': username,
      'loginPassword': hashedPassword,
    });
    
    if (response['success'] == true && response['data'] != null) {
      final apiKey = response['data']['api_key'];
      await _storageService.writeApiKey(apiKey);
    } else {
      throw Exception('Invalid response format or api_key missing');
    }
  }

  Future<void> logout() async {
    await _storageService.deleteApiKey();
    await _storageService.writePcName(''); // clear selected PC
  }

  Future<bool> isAuthenticated() async {
    final apiKey = await _storageService.readApiKey();
    return apiKey != null && apiKey.isNotEmpty;
  }

  Future<bool> isBiometricEnabled() async {
    return await _storageService.readBiometricEnabled();
  }

  Future<bool> authenticateWithBiometrics() async {
    try {
      final bool canAuthenticateWithBiometrics = await _localAuth.canCheckBiometrics;
      final bool canAuthenticate = canAuthenticateWithBiometrics || await _localAuth.isDeviceSupported();

      if (!canAuthenticate) return false;

      return await _localAuth.authenticate(
        localizedReason: 'Please authenticate to access PCConnect',
        options: const AuthenticationOptions(
          stickyAuth: true,
          biometricOnly: false,
        ),
      );
    } catch (e) {
      return false;
    }
  }
}
