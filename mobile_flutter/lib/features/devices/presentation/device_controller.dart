import 'package:flutter_riverpod/flutter_riverpod.dart';
import '../data/device_repository.dart';

final devicesProvider = FutureProvider<List<String>>((ref) async {
  final repo = ref.watch(deviceRepositoryProvider);
  return repo.getDevices();
});

final activeDeviceProvider = StateNotifierProvider<ActiveDeviceController, String?>((ref) {
  return ActiveDeviceController(ref.watch(deviceRepositoryProvider));
});

class ActiveDeviceController extends StateNotifier<String?> {
  final DeviceRepository _repository;

  ActiveDeviceController(this._repository) : super(null) {
    _init();
  }

  Future<void> _init() async {
    state = await _repository.getActiveDevice();
  }

  Future<void> setDevice(String pcName) async {
    await _repository.setActiveDevice(pcName);
    state = pcName;
  }
  
  // Expose an auto-select method
  Future<void> autoSelectIfNull(List<String> devices) async {
    if ((state == null || state!.isEmpty) && devices.isNotEmpty) {
      await setDevice(devices.first);
    }
  }
}
