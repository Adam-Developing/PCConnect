import 'package:flutter_riverpod/flutter_riverpod.dart';
import '../data/auth_repository.dart';

final authControllerProvider = StateNotifierProvider<AuthController, AsyncValue<void>>((ref) {
  return AuthController(ref.watch(authRepositoryProvider));
});

class AuthController extends StateNotifier<AsyncValue<void>> {
  final AuthRepository _authRepository;

  AuthController(this._authRepository) : super(const AsyncValue.data(null));

  Future<bool> login(String username, String password) async {
    state = const AsyncValue.loading();
    try {
      await _authRepository.login(username, password);
      // Now save that we have successfully authenticated this session without biometrics
      state = const AsyncValue.data(null);
      return true;
    } catch (e, st) {
      state = AsyncValue.error(e, st);
      return false;
    }
  }

  Future<bool> checkBiometricOrSession() async {
    final isAuth = await _authRepository.isAuthenticated();
    if (!isAuth) return false;

    final biometricsEnabled = await _authRepository.isBiometricEnabled();
    if (biometricsEnabled) {
      final success = await _authRepository.authenticateWithBiometrics();
      return success;
    }
    return true; // Already authenticated and no biometrics required
  }

  Future<void> logout() async {
    await _authRepository.logout();
    state = const AsyncValue.data(null);
  }
}
