# kotlinx.serialization keeps the generated serializers reachable.
-keepattributes *Annotation*, InnerClasses
-dontnote kotlinx.serialization.**
-keepclassmembers class kotlinx.serialization.json.** { *** Companion; }
-keepclasseswithmembers class kotlinx.serialization.json.** { kotlinx.serialization.KSerializer serializer(...); }
-keep,includedescriptorclasses class uk.co.adamkhattab.pcconnect.**$$serializer { *; }
-keepclassmembers class uk.co.adamkhattab.pcconnect.** { *** Companion; }
-keepclasseswithmembers class uk.co.adamkhattab.pcconnect.** { kotlinx.serialization.KSerializer serializer(...); }

# OkHttp and the SignalR client.
-dontwarn okhttp3.**
-dontwarn okio.**
-dontwarn org.slf4j.**
-keep class com.microsoft.signalr.** { *; }
