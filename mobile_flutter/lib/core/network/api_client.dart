import 'package:dio/dio.dart';
import '../security/secure_storage_service.dart';
import '../config/app_config.dart';

class ApiClient {
  final Dio dio;
  final SecureStorageService storageService;

  ApiClient(this.storageService) : dio = Dio(BaseOptions(
    baseUrl: AppConfig.baseUrl,
    connectTimeout: const Duration(seconds: 10),
    receiveTimeout: const Duration(seconds: 10),
  )) {
    dio.interceptors.add(InterceptorsWrapper(
      onRequest: (options, handler) async {
        String? apiKey = await storageService.readApiKey();
        if (apiKey != null && apiKey.isNotEmpty) {
          options.headers['X-API-Key'] = apiKey;
        }
        
        String? pcName = await storageService.readPcName();
        // Fallback to PCName logic if needed
        if (pcName != null && pcName.isNotEmpty) {
          options.headers['PCName'] = pcName;
          options.headers['pcname'] = pcName; // Also add lowercase just in case
        }
        
        return handler.next(options);
      },
    ));
  }

  Future<dynamic> post(String path, dynamic data, {Map<String, dynamic>? headers}) async {
    try {
      final response = await dio.post(path, data: data, options: Options(headers: headers));
      return response.data;
    } on DioException catch (e) {
      _handleError(e);
    }
  }

  Future<dynamic> get(String path, {Map<String, dynamic>? headers}) async {
    try {
      final response = await dio.get(path, options: Options(headers: headers));
      return response.data;
    } on DioException catch (e) {
      _handleError(e);
    }
  }

  Future<dynamic> put(String path, dynamic data, {Map<String, dynamic>? headers}) async {
    try {
      final response = await dio.put(path, data: data, options: Options(headers: headers));
      return response.data;
    } on DioException catch (e) {
      _handleError(e);
    }
  }

  void _handleError(DioException e) {
    if (e.response != null) {
      final data = e.response?.data;
      if (data is Map<String, dynamic> && data.containsKey('message')) {
        throw Exception(data['message']);
      }
      throw Exception('API Error: ${e.response?.statusCode}');
    } else {
      throw Exception(e.message ?? 'Unknown connection error');
    }
  }
}
