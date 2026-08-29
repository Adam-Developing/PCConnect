using System.Text.Json;
using System.Text.Json.Serialization;

namespace PCConnect.Contracts.V2;

[JsonConverter(typeof(JsonStringEnumConverter<PlatformType>))]
public enum PlatformType
{
    [JsonStringEnumMemberName("windows")] Windows,
    [JsonStringEnumMemberName("android")] Android,
    [JsonStringEnumMemberName("ios")] Ios,
    [JsonStringEnumMemberName("macos")] Macos,
    [JsonStringEnumMemberName("linux")] Linux,
    [JsonStringEnumMemberName("web")] Web,
    [JsonStringEnumMemberName("unknown")] Unknown
}

[JsonConverter(typeof(JsonStringEnumConverter<DeviceCapability>))]
public enum DeviceCapability
{
    [JsonStringEnumMemberName("lock")] Lock,
    [JsonStringEnumMemberName("sleep")] Sleep,
    [JsonStringEnumMemberName("hibernate")] Hibernate,
    [JsonStringEnumMemberName("sign_out")] SignOut,
    [JsonStringEnumMemberName("restart")] Restart,
    [JsonStringEnumMemberName("shutdown")] Shutdown,
    [JsonStringEnumMemberName("reminders")] Reminders
}

[JsonConverter(typeof(JsonStringEnumConverter<CommandType>))]
public enum CommandType
{
    [JsonStringEnumMemberName("lock")] Lock,
    [JsonStringEnumMemberName("sleep")] Sleep,
    [JsonStringEnumMemberName("hibernate")] Hibernate,
    [JsonStringEnumMemberName("sign_out")] SignOut,
    [JsonStringEnumMemberName("restart")] Restart,
    [JsonStringEnumMemberName("shutdown")] Shutdown
}

[JsonConverter(typeof(JsonStringEnumConverter<CommandStatus>))]
public enum CommandStatus
{
    [JsonStringEnumMemberName("queued")] Queued,
    [JsonStringEnumMemberName("claimed")] Claimed,
    [JsonStringEnumMemberName("accepted")] Accepted,
    [JsonStringEnumMemberName("succeeded")] Succeeded,
    [JsonStringEnumMemberName("failed")] Failed,
    [JsonStringEnumMemberName("expired")] Expired,
    [JsonStringEnumMemberName("cancelled")] Cancelled
}

[JsonConverter(typeof(JsonStringEnumConverter<CommandFailureCode>))]
public enum CommandFailureCode
{
    [JsonStringEnumMemberName("no_interactive_session")] NoInteractiveSession,
    [JsonStringEnumMemberName("unsupported")] Unsupported,
    [JsonStringEnumMemberName("permission_denied")] PermissionDenied,
    [JsonStringEnumMemberName("expired")] Expired,
    [JsonStringEnumMemberName("local_replay")] LocalReplay,
    [JsonStringEnumMemberName("execution_failed")] ExecutionFailed
}

[JsonConverter(typeof(JsonStringEnumConverter<ReminderTargetMode>))]
public enum ReminderTargetMode
{
    [JsonStringEnumMemberName("all_devices")] AllDevices,
    [JsonStringEnumMemberName("selected_devices")] SelectedDevices
}

[JsonConverter(typeof(JsonStringEnumConverter<ReminderDeliveryStatus>))]
public enum ReminderDeliveryStatus
{
    [JsonStringEnumMemberName("pending")] Pending,
    [JsonStringEnumMemberName("available")] Available,
    [JsonStringEnumMemberName("displayed")] Displayed,
    [JsonStringEnumMemberName("dismissed")] Dismissed,
    [JsonStringEnumMemberName("completed")] Completed,
    [JsonStringEnumMemberName("expired")] Expired
}

[JsonConverter(typeof(JsonStringEnumConverter<StepUpIntentType>))]
public enum StepUpIntentType
{
    [JsonStringEnumMemberName("command")] Command,
    [JsonStringEnumMemberName("account_delete")] AccountDelete,
    [JsonStringEnumMemberName("data_export")] DataExport,
    [JsonStringEnumMemberName("device_revoke")] DeviceRevoke,
    [JsonStringEnumMemberName("security_change")] SecurityChange
}

public static class ContractValues
{
    public static string WireValue<T>(this T value) where T : struct, Enum =>
        JsonSerializer.Serialize(value).Trim('"');
}
