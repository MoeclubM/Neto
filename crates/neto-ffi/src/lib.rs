use anyhow::{Context, Result};
use neto_core::{NetoController, PlatformHooks, init_rustls_provider};
use once_cell::sync::Lazy;
use serde_json::{Value, json};
use std::any::Any;
use std::ffi::{CStr, CString, c_char};
use std::panic::{AssertUnwindSafe, catch_unwind};
use std::sync::{Arc, Mutex};
use tokio::runtime::Runtime;

pub static TOKIO_RUNTIME: Lazy<Runtime> = Lazy::new(|| {
    init_rustls_provider();
    Runtime::new().expect("create Neto Tokio runtime")
});

static GLOBAL_CONTROLLER: Lazy<Mutex<Option<Arc<NetoController>>>> =
    Lazy::new(|| Mutex::new(None));

pub fn global_controller() -> Result<Arc<NetoController>> {
    GLOBAL_CONTROLLER
        .lock()
        .expect("global controller lock poisoned")
        .clone()
        .context("Neto controller has not been initialized")
}

pub fn initialize_controller(
    data_dir: &str,
    app_id: &str,
    user_agent: &str,
) -> Result<Arc<NetoController>> {
    let _ = &*TOKIO_RUNTIME;
    let mut guard = GLOBAL_CONTROLLER
        .lock()
        .expect("global controller lock poisoned");
    if let Some(existing) = guard.as_ref() {
        return Ok(existing.clone());
    }
    let hooks: Option<Arc<dyn PlatformHooks>> = {
        #[cfg(target_os = "windows")]
        {
            Some(neto_platform_windows::WindowsPlatformHooks::new(app_id))
        }
        #[cfg(target_os = "linux")]
        {
            Some(neto_platform_linux::LinuxWaylandPlatformHooks::new(
                app_id, app_id,
            ))
        }
        #[cfg(target_os = "android")]
        {
            Some(neto_platform_android::platform_hooks())
        }
        #[cfg(not(any(target_os = "windows", target_os = "linux", target_os = "android")))]
        {
            let _ = app_id;
            None
        }
    };
    let ua = if user_agent.trim().is_empty() {
        "Aerion"
    } else {
        user_agent.trim()
    };
    let controller = NetoController::new(data_dir, ua, hooks)?;
    *guard = Some(controller.clone());
    Ok(controller)
}

pub fn call_controller_json(method: &str, params_json: &str) -> String {
    let result = catch_unwind(AssertUnwindSafe(|| -> Result<Value> {
        let controller = global_controller()?;
        let params: Value = if params_json.trim().is_empty() {
            json!({})
        } else {
            serde_json::from_str(params_json).context("parse params JSON")?
        };
        TOKIO_RUNTIME.block_on(controller.dispatch_json(method, params))
    }));
    match result {
        Ok(Ok(val)) => json!({ "ok": true, "result": val }).to_string(),
        Ok(Err(err)) => json!({ "ok": false, "error": format!("{err:#}") }).to_string(),
        Err(payload) => json!({
            "ok": false,
            "error": format!("Rust panic: {}", panic_text(payload))
        })
        .to_string(),
    }
}

fn cstr_to_str<'a>(ptr: *const c_char) -> Result<&'a str> {
    if ptr.is_null() {
        return Ok("");
    }
    Ok(unsafe { CStr::from_ptr(ptr) }.to_str()?)
}

fn into_c_string(text: String) -> *mut c_char {
    CString::new(text.replace('\0', ""))
        .unwrap_or_else(|_| CString::new("{\"ok\":false,\"error\":\"CString nul\"}").unwrap())
        .into_raw()
}

#[unsafe(no_mangle)]
pub extern "C" fn neto_init(
    data_dir: *const c_char,
    app_id: *const c_char,
    user_agent: *const c_char,
) -> *mut c_char {
    let out = catch_unwind(AssertUnwindSafe(|| -> Result<Value> {
        let dir = cstr_to_str(data_dir)?;
        let app = cstr_to_str(app_id)?;
        let ua = cstr_to_str(user_agent)?;
        let ctrl = initialize_controller(dir, app, ua)?;
        Ok(serde_json::to_value(ctrl.snapshot())?)
    }));
    let json_text = match out {
        Ok(Ok(val)) => json!({ "ok": true, "result": val }).to_string(),
        Ok(Err(err)) => json!({ "ok": false, "error": format!("{err:#}") }).to_string(),
        Err(payload) => json!({
            "ok": false,
            "error": format!("Rust panic: {}", panic_text(payload))
        })
        .to_string(),
    };
    into_c_string(json_text)
}

#[unsafe(no_mangle)]
pub extern "C" fn neto_call(method: *const c_char, params_json: *const c_char) -> *mut c_char {
    let json_text = match (cstr_to_str(method), cstr_to_str(params_json)) {
        (Ok(m), Ok(p)) => call_controller_json(m, p),
        (Err(e), _) | (_, Err(e)) => json!({ "ok": false, "error": e.to_string() }).to_string(),
    };
    into_c_string(json_text)
}

#[unsafe(no_mangle)]
pub extern "C" fn neto_shutdown() -> *mut c_char {
    let out = catch_unwind(AssertUnwindSafe(|| -> Result<()> {
        if let Ok(ctrl) = global_controller() {
            TOKIO_RUNTIME.block_on(ctrl.disconnect())?;
        }
        #[cfg(any(target_os = "windows", target_os = "linux"))]
        {
            TOKIO_RUNTIME.block_on(neto_core::shutdown_vpns())?;
        }
        Ok(())
    }));
    let json_text = match out {
        Ok(Ok(())) => json!({ "ok": true }).to_string(),
        Ok(Err(err)) => json!({ "ok": false, "error": format!("{err:#}") }).to_string(),
        Err(payload) => json!({
            "ok": false,
            "error": format!("Rust panic: {}", panic_text(payload))
        })
        .to_string(),
    };
    into_c_string(json_text)
}

#[unsafe(no_mangle)]
pub extern "C" fn neto_free_string(ptr: *mut c_char) {
    if !ptr.is_null() {
        unsafe {
            let _ = CString::from_raw(ptr);
        }
    }
}

fn panic_text(payload: Box<dyn Any + Send + 'static>) -> String {
    if let Some(s) = payload.downcast_ref::<&str>() {
        return (*s).to_string();
    }
    if let Some(s) = payload.downcast_ref::<String>() {
        return s.clone();
    }
    "unknown panic".to_string()
}

#[cfg(target_os = "android")]
mod android_jni {
    use super::*;
    use jni::errors::{Result as JniResult, ThrowRuntimeExAndDefault};
    use jni::objects::{JClass, JObject, JString};
    use jni::EnvUnowned;

    #[unsafe(no_mangle)]
    pub extern "system" fn Java_moe_telecom_neto_sdk_NetoBridge_nativeInit<'local>(
        mut env: EnvUnowned<'local>,
        _object: JObject<'local>,
        data_dir: JString<'local>,
        app_id: JString<'local>,
        user_agent: JString<'local>,
        service_class: JClass<'local>,
    ) -> JString<'local> {
        neto_platform_android::init_android_logger("Neto");
        env.with_env(|env| -> JniResult<JString<'local>> {
            neto_platform_android::register_vpn_service_class(env, &service_class)?;
            let dir = data_dir.try_to_string(env).unwrap_or_default();
            let app = app_id.try_to_string(env).unwrap_or_else(|_| "Neto".into());
            let ua = user_agent
                .try_to_string(env)
                .unwrap_or_else(|_| "Aerion".into());
            let text = match initialize_controller(&dir, &app, &ua) {
                Ok(ctrl) => match serde_json::to_value(ctrl.snapshot()) {
                    Ok(v) => json!({ "ok": true, "result": v }).to_string(),
                    Err(e) => json!({ "ok": false, "error": e.to_string() }).to_string(),
                },
                Err(e) => json!({ "ok": false, "error": format!("{e:#}") }).to_string(),
            };
            JString::from_str(env, text)
        })
        .resolve::<ThrowRuntimeExAndDefault>()
    }

    #[unsafe(no_mangle)]
    pub extern "system" fn Java_moe_telecom_neto_sdk_NetoBridge_nativeCall<'local>(
        mut env: EnvUnowned<'local>,
        _object: JObject<'local>,
        method: JString<'local>,
        params_json: JString<'local>,
    ) -> JString<'local> {
        env.with_env(|env| -> JniResult<JString<'local>> {
            let m = method.try_to_string(env).unwrap_or_default();
            let p = params_json.try_to_string(env).unwrap_or_default();
            let out = call_controller_json(&m, &p);
            JString::from_str(env, out)
        })
        .resolve::<ThrowRuntimeExAndDefault>()
    }
}
