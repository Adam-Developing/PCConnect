# PCConnect Mobile (Flutter)

This directory contains the new Flutter-based mobile application replacing the legacy Android-only Java app.

## Implemented So Far (Day 1 Vertical Slice)
- Scaffolded standard Flutter project structure with feature-based architecture (`lib/features`).
- Wired GoRouter for navigation: `LoginScreen` -> `DashboardScreen`.
- Designed initial `LoginScreen` and `DashboardScreen` adaptively with Material 3 theming (supporting dark/light mode automatically).
- Created modern command cards replacing the legacy grid of buttons in `MainActivity.java`.
- Set up Riverpod provider scope placeholder.

## How to Proceed
1. Ensure the `flutter` SDK is installed and added to your system `PATH`.
2. Open a terminal in this directory (`cd mobile_flutter`) and run `flutter pub get`.
3. Run the app on an Android emulator or iOS simulator using `flutter run`.

## Next Steps (Networking & Data)
1. Add Retrofit/Dio client to handle `X-API-Key` and `PCName` requests mapped from `./api_node/api_spec.md`.
2. Connect `LoginScreen` to backend and persist the returned API key using `flutter_secure_storage`.
3. Wire up the websockets using `socket_io_client` to handle realtime command exchange and reminder updates.
