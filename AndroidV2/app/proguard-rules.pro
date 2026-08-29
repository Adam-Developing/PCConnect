-keepattributes Signature,*Annotation*
-keep class com.adamkhattab.pcconnect.v2.data.api.** { *; }
# The SignalR Java client intentionally treats an SLF4J backend as optional and
# falls back to its no-op logger when Android does not provide one.
-dontwarn org.slf4j.impl.StaticLoggerBinder
