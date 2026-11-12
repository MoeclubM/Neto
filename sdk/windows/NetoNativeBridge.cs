using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Neto.Sdk.Windows;

public static class NetoNativeBridge
{
    private const string DllName = "neto_ffi";

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr neto_init(IntPtr dataDir, IntPtr appId, IntPtr userAgent);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr neto_call(IntPtr method, IntPtr paramsJson);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr neto_shutdown();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void neto_free_string(IntPtr ptr);

    public static JsonNode Init(string dataDir, string appId, string userAgent)
    {
        var dirPtr = Marshal.StringToCoTaskMemUTF8(dataDir);
        var appPtr = Marshal.StringToCoTaskMemUTF8(appId);
        var uaPtr = Marshal.StringToCoTaskMemUTF8(userAgent);
        try
        {
            var resPtr = neto_init(dirPtr, appPtr, uaPtr);
            return ConsumeEnvelope(resPtr);
        }
        finally
        {
            Marshal.FreeCoTaskMem(dirPtr);
            Marshal.FreeCoTaskMem(appPtr);
            Marshal.FreeCoTaskMem(uaPtr);
        }
    }

    public static JsonNode CallSync(string method, object? parameters = null)
    {
        var jsonText = parameters switch
        {
            null => "{}",
            string raw => raw,
            JsonNode node => node.ToJsonString(),
            _ => JsonSerializer.Serialize(parameters),
        };
        var methodPtr = Marshal.StringToCoTaskMemUTF8(method);
        var paramsPtr = Marshal.StringToCoTaskMemUTF8(jsonText);
        try
        {
            var resPtr = neto_call(methodPtr, paramsPtr);
            return ConsumeEnvelope(resPtr);
        }
        finally
        {
            Marshal.FreeCoTaskMem(methodPtr);
            Marshal.FreeCoTaskMem(paramsPtr);
        }
    }

    public static Task<JsonNode> CallAsync(string method, object? parameters = null) =>
        Task.Run(() => CallSync(method, parameters));

    public static void Shutdown()
    {
        var ptr = neto_shutdown();
        if (ptr != IntPtr.Zero)
        {
            neto_free_string(ptr);
        }
    }

    private static JsonNode ConsumeEnvelope(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero)
        {
            throw new InvalidOperationException("neto_ffi returned null pointer");
        }
        string raw;
        try
        {
            raw = Marshal.PtrToStringUTF8(ptr) ?? "{}";
        }
        finally
        {
            neto_free_string(ptr);
        }
        var root = JsonNode.Parse(raw)?.AsObject()
            ?? throw new InvalidOperationException("Invalid JSON returned from neto_ffi");
        if (root["ok"]?.GetValue<bool>() != true)
        {
            var err = root["error"]?.GetValue<string>() ?? "Unknown Neto core error";
            throw new InvalidOperationException(err);
        }
        return root["result"] ?? new JsonObject();
    }
}
