import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';
import '../../auth/presentation/auth_controller.dart';
import '../../commands/data/command_repository.dart';
import '../../devices/presentation/device_controller.dart';
import '../../../core/websocket/socket_client.dart';

class DashboardScreen extends ConsumerStatefulWidget {
  const DashboardScreen({super.key});

  @override
  ConsumerState<DashboardScreen> createState() => _DashboardScreenState();
}

class _DashboardScreenState extends ConsumerState<DashboardScreen> {
  @override
  void initState() {
    super.initState();
    // Initialize socket connection for realtime updates if needed
    WidgetsBinding.instance.addPostFrameCallback((_) {
      ref.read(socketClientProvider).connect(ref);
    });
  }

  @override
  void dispose() {
    ref.read(socketClientProvider).disconnect();
    super.dispose();
  }

  void _sendCommand(String command) async {
    final activeDevice = ref.read(activeDeviceProvider);
    if (activeDevice == null || activeDevice.isEmpty) {
      ScaffoldMessenger.of(context).showSnackBar(
        const SnackBar(content: Text('Please select an active PC first.')),
      );
      return;
    }

    try {
      await ref.read(commandRepositoryProvider).sendCommand(command);
      if (!mounted) return;
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(content: Text('Sent command $command to $activeDevice.')),
      );
    } catch (e) {
      if (!mounted) return;
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(content: Text('Failed to send command: $e', style: const TextStyle(color: Colors.white)), backgroundColor: Colors.red),
      );
    }
  }

  @override
  Widget build(BuildContext context) {
    final connectionState = ref.watch(socketConnectionStateProvider);
    final isDeviceOnline = ref.watch(deviceOnlineStateProvider);

    return Scaffold(
      appBar: AppBar(
        title: const Text('PCConnect'),
        actions: [
          IconButton(
            icon: const Icon(Icons.settings),
            onPressed: () {
              // context.push('/settings');
            },
          ),
          IconButton(
            icon: const Icon(Icons.logout),
            onPressed: () async {
              await ref.read(authControllerProvider.notifier).logout();
              if (mounted) context.go('/login');
            },
          ),
        ],
      ),
      body: Padding(
        padding: const EdgeInsets.all(16.0),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            if (!connectionState)
              Container(
                width: double.infinity,
                padding: const EdgeInsets.all(8),
                margin: const EdgeInsets.only(bottom: 16),
                color: Colors.orange.shade100,
                child: Row(
                  children: const [
                    Icon(Icons.warning, color: Colors.deepOrange),
                    SizedBox(width: 8),
                    Expanded(
                      child: Text(
                        'WebSocket disconnected, REST fallback active.',
                        style: TextStyle(color: Colors.deepOrange, fontWeight: FontWeight.bold),
                      ),
                    ),
                  ],
                ),
              )
            else if (!isDeviceOnline)
              Container(
                width: double.infinity,
                padding: const EdgeInsets.all(8),
                margin: const EdgeInsets.only(bottom: 16),
                color: Colors.red.shade100,
                child: Row(
                  children: const [
                    Icon(Icons.cloud_off, color: Colors.red),
                    SizedBox(width: 8),
                    Expanded(
                      child: Text(
                        'Target PC is Offline',
                        style: TextStyle(color: Colors.red, fontWeight: FontWeight.bold),
                      ),
                    ),
                  ],
                ),
              ),
            const _DeviceSelector(),
            const SizedBox(height: 24),
            Expanded(
              child: GridView.count(
                crossAxisCount: 2,
                crossAxisSpacing: 16,
                mainAxisSpacing: 16,
                children: [
                  _CommandCard(title: 'Sleep', command: 'Sleep', icon: Icons.nightlight_round, onSend: isDeviceOnline ? _sendCommand : null),
                  _CommandCard(title: 'Hibernate', command: 'Hibernate', icon: Icons.bed_rounded, onSend: isDeviceOnline ? _sendCommand : null),
                  _CommandCard(title: 'Shutdown', command: 'Shut_Down', icon: Icons.power_settings_new, onSend: isDeviceOnline ? _sendCommand : null),
                  _CommandCard(title: 'Lock', command: 'Lock', icon: Icons.lock_outline, onSend: isDeviceOnline ? _sendCommand : null),
                  _CommandCard(title: 'Sign Out', command: 'Sign_Out', icon: Icons.person_off, onSend: isDeviceOnline ? _sendCommand : null),
                  _CommandCard(title: 'Restart', command: 'Restart', icon: Icons.restart_alt, onSend: isDeviceOnline ? _sendCommand : null),
                ],
              ),
            ),
            const SizedBox(height: 16),
            SizedBox(
              width: double.infinity,
              height: 50,
              child: FilledButton.tonal(
                onPressed: () {
                  // context.push('/reminders');
                },
                child: const Text('View Reminders'),
              ),
            )
          ],
        ),
      ),
    );
  }
}

class _DeviceSelector extends ConsumerWidget {
  const _DeviceSelector();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final devicesAsync = ref.watch(devicesProvider);
    final activeDevice = ref.watch(activeDeviceProvider);

    return devicesAsync.when(
      data: (devices) {
        if (devices.isEmpty) {
          return const Text('No active devices found. Try adding a PC.');
        }
        
        WidgetsBinding.instance.addPostFrameCallback((_) {
          ref.read(activeDeviceProvider.notifier).autoSelectIfNull(devices).then((_) {
              if (ref.read(socketConnectionStateProvider) == false) {
                 ref.read(socketClientProvider).connect(ref);
              }
          });
        });

        return DropdownButtonFormField<String>(
          decoration: const InputDecoration(
            labelText: 'Active PC',
            border: OutlineInputBorder(),
          ),
          value: devices.contains(activeDevice) ? activeDevice : null,
          items: devices.map((device) {
            return DropdownMenuItem(value: device, child: Text(device));
          }).toList(),
          onChanged: (value) {
            if (value != null) {
              ref.read(activeDeviceProvider.notifier).setDevice(value);
              // Reconnect socket with new PCName
              ref.read(socketClientProvider).disconnect();
              ref.read(socketClientProvider).connect(ref);
            }
          },
        );
      },
      loading: () => const CircularProgressIndicator(),
      error: (err, stack) => Text('Error loading devices: $err'),
    );
  }
}

class _CommandCard extends StatelessWidget {
  final String title;
  final String command;
  final IconData icon;
  final Function(String)? onSend;

  const _CommandCard({
    required this.title,
    required this.command,
    required this.icon,
    this.onSend,
  });

  @override
  Widget build(BuildContext context) {
    final isEnabled = onSend != null;
    return Card(
      elevation: isEnabled ? 2 : 0,
      color: isEnabled ? null : Colors.grey.shade200,
      child: InkWell(
        onTap: isEnabled ? () => onSend!(command) : null,
        child: Column(
          mainAxisAlignment: MainAxisAlignment.center,
          children: [
            Icon(icon, size: 48, color: isEnabled ? Theme.of(context).colorScheme.primary : Colors.grey),
            const SizedBox(height: 8),
            Text(
              title,
              style: TextStyle(
                fontSize: 16, 
                fontWeight: FontWeight.bold,
                color: isEnabled ? null : Colors.grey
              ),
            ),
          ],
        ),
      ),
    );
  }
}
