package moe.telecom.neto.sdk

import android.content.Context
import org.json.JSONObject
import java.util.concurrent.atomic.AtomicBoolean

object NetoBridge {
    private val loaded = AtomicBoolean(false)
    private val initialized = AtomicBoolean(false)

    private fun ensureLibrary() {
        if (loaded.compareAndSet(false, true)) {
            System.loadLibrary("neto_ffi")
        }
    }

    fun init(context: Context, appId: String = "Neto", userAgent: String = "Aerion"): JSONObject {
        ensureLibrary()
        val dataDir = context.filesDir.resolve("neto").apply { mkdirs() }.absolutePath
        val raw = nativeInit(dataDir, appId, userAgent, NetoVpnService::class.java)
        initialized.set(true)
        return parseEnvelope(raw)
    }

    fun call(method: String, params: JSONObject = JSONObject()): JSONObject {
        ensureLibrary()
        val raw = nativeCall(method, params.toString())
        return parseEnvelope(raw)
    }

    private fun parseEnvelope(raw: String): JSONObject {
        val obj = JSONObject(raw)
        if (!obj.optBoolean("ok", false)) {
            throw IllegalStateException(obj.optString("error", "Neto native call failed"))
        }
        return obj
    }

    private external fun nativeInit(
        dataDir: String,
        appId: String,
        userAgent: String,
        serviceClass: Class<*>,
    ): String

    private external fun nativeCall(method: String, paramsJson: String): String
}
