#[cfg(target_os = "android")]
mod imp {
    use anyhow::{Context, Result, ensure};
    use jni::objects::{Global, JClass};
    use jni::{Env, JValue, JavaVM, jni_sig, jni_str};
    use neto_core::{PlatformHooks, set_event_callback, set_log_callback};
    use once_cell::sync::Lazy;
    use std::sync::{Arc, Mutex};

    static VPN_SERVICE_CLASS: Lazy<Mutex<Option<Global<JClass<'static>>>>> =
        Lazy::new(|| Mutex::new(None));

    pub struct AndroidPlatformHooks;
    impl PlatformHooks for AndroidPlatformHooks {}

    pub fn platform_hooks() -> Arc<dyn PlatformHooks> {
        Arc::new(AndroidPlatformHooks)
    }

    pub fn init_android_logger(tag: &'static str) {
        android_logger::init_once(
            android_logger::Config::default()
                .with_tag(tag)
                .with_max_level(log::LevelFilter::Info),
        );
    }

    pub fn register_vpn_service_class(
        env: &Env<'_>,
        service_class: &JClass<'_>,
    ) -> jni::errors::Result<()> {
        let global = env.new_global_ref(service_class)?;
        *VPN_SERVICE_CLASS
            .lock()
            .expect("Android VpnService class lock poisoned") = Some(global);
        aerion::socket_protect::set_socket_protector(protect_android_socket);
        set_log_callback(|level, message| {
            let _ = emit_android_log(&level, &message);
        });
        set_event_callback(|_, event_json| {
            let _ = emit_android_event(&event_json);
        });
        Ok(())
    }

    fn protect_android_socket(fd: i32) -> Result<()> {
        let protected = JavaVM::singleton()
            .context("get JavaVM for socket protection")?
            .attach_current_thread(|env| -> Result<bool> {
                let guard = VPN_SERVICE_CLASS
                    .lock()
                    .expect("Android VpnService class lock poisoned");
                let class = guard
                    .as_ref()
                    .context("NetoVpnService class not initialized")?;
                Ok(env
                    .call_static_method(
                        class,
                        jni_str!("protectSocketFd"),
                        jni_sig!("(I)Z"),
                        &[JValue::Int(fd)],
                    )?
                    .z()?)
            })
            .map_err(|err| anyhow::anyhow!("protect Android socket fd {fd}: {err}"))?;
        ensure!(protected, "Android VPN socket protection returned false");
        Ok(())
    }

    fn emit_android_log(level: &str, message: &str) -> Result<()> {
        JavaVM::singleton()
            .context("get JavaVM for log callback")?
            .attach_current_thread(|env| -> Result<()> {
                let guard = VPN_SERVICE_CLASS
                    .lock()
                    .expect("Android VpnService class lock poisoned");
                let Some(class) = guard.as_ref() else {
                    return Ok(());
                };
                let jlevel = env.new_string(level)?;
                let jmsg = env.new_string(message)?;
                env.call_static_method(
                    class,
                    jni_str!("onLog"),
                    jni_sig!("(Ljava/lang/String;Ljava/lang/String;)V"),
                    &[
                        JValue::Object(jlevel.as_ref()),
                        JValue::Object(jmsg.as_ref()),
                    ],
                )?;
                Ok(())
            })
            .map_err(|err| anyhow::anyhow!("Android log callback failed: {err}"))
    }

    fn emit_android_event(event_json: &str) -> Result<()> {
        JavaVM::singleton()
            .context("get JavaVM for event callback")?
            .attach_current_thread(|env| -> Result<()> {
                let guard = VPN_SERVICE_CLASS
                    .lock()
                    .expect("Android VpnService class lock poisoned");
                let Some(class) = guard.as_ref() else {
                    return Ok(());
                };
                let jev = env.new_string(event_json)?;
                env.call_static_method(
                    class,
                    jni_str!("onEvent"),
                    jni_sig!("(Ljava/lang/String;)V"),
                    &[JValue::Object(jev.as_ref())],
                )?;
                Ok(())
            })
            .map_err(|err| anyhow::anyhow!("Android event callback failed: {err}"))
    }
}

#[cfg(target_os = "android")]
pub use imp::*;
