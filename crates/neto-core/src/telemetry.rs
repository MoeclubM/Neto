use once_cell::sync::Lazy;
use serde::{Deserialize, Serialize};
use std::collections::VecDeque;
use std::net::{IpAddr, UdpSocket};
use std::sync::Mutex;
use std::time::{SystemTime, UNIX_EPOCH};

const MAX_SERIES_SAMPLES: usize = 60;
const MAX_LOG_ENTRIES: usize = 500;

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct LogEntry {
    pub timestamp_ms: u64,
    pub level: String,
    pub message: String,
}

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
pub struct TelemetrySnapshot {
    pub upload_bytes: u64,
    pub download_bytes: u64,
    pub upload_speed_bps: u64,
    pub download_speed_bps: u64,
    pub upload_history: Vec<u64>,
    pub download_history: Vec<u64>,
    pub latency_ms: Option<u64>,
    pub latency_history: Vec<i64>,
    pub active_sessions: usize,
    pub memory_bytes: u64,
    pub intranet_ipv4: Option<String>,
    pub intranet_ipv6: Option<String>,
}

struct TelemetryInner {
    upload_bytes: u64,
    download_bytes: u64,
    last_sampled_upload: u64,
    last_sampled_download: u64,
    last_sampled_ms: u64,
    upload_speed_bps: u64,
    download_speed_bps: u64,
    upload_history: VecDeque<u64>,
    download_history: VecDeque<u64>,
    latency_ms: Option<u64>,
    latency_history: VecDeque<i64>,
    active_sessions: usize,
    logs: VecDeque<LogEntry>,
}

static TELEMETRY: Lazy<Mutex<TelemetryInner>> = Lazy::new(|| {
    Mutex::new(TelemetryInner {
        upload_bytes: 0,
        download_bytes: 0,
        last_sampled_upload: 0,
        last_sampled_download: 0,
        last_sampled_ms: now_ms(),
        upload_speed_bps: 0,
        download_speed_bps: 0,
        upload_history: VecDeque::with_capacity(MAX_SERIES_SAMPLES),
        download_history: VecDeque::with_capacity(MAX_SERIES_SAMPLES),
        latency_ms: None,
        latency_history: VecDeque::with_capacity(MAX_SERIES_SAMPLES),
        active_sessions: 0,
        logs: VecDeque::with_capacity(MAX_LOG_ENTRIES),
    })
});

pub fn now_ms() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_millis() as u64)
        .unwrap_or(0)
}

pub fn reset_session_telemetry() {
    let mut inner = TELEMETRY.lock().expect("telemetry lock poisoned");
    inner.upload_bytes = 0;
    inner.download_bytes = 0;
    inner.last_sampled_upload = 0;
    inner.last_sampled_download = 0;
    inner.last_sampled_ms = now_ms();
    inner.upload_speed_bps = 0;
    inner.download_speed_bps = 0;
    inner.upload_history.clear();
    inner.download_history.clear();
    inner.latency_ms = None;
    inner.latency_history.clear();
    inner.active_sessions = 0;
}

pub fn record_core_event(event: &aerion::CoreEvent) {
    let mut inner = TELEMETRY.lock().expect("telemetry lock poisoned");
    match event {
        aerion::CoreEvent::SessionOpened { .. } => {
            inner.active_sessions = inner.active_sessions.saturating_add(1);
        }
        aerion::CoreEvent::SessionClosed { .. } | aerion::CoreEvent::SessionCancelled { .. } => {
            inner.active_sessions = inner.active_sessions.saturating_sub(1);
        }
        aerion::CoreEvent::TrafficRecorded {
            direction, bytes, ..
        } => match direction {
            aerion::TrafficDirection::Upload => {
                inner.upload_bytes = inner.upload_bytes.saturating_add(*bytes);
            }
            aerion::TrafficDirection::Download => {
                inner.download_bytes = inner.download_bytes.saturating_add(*bytes);
            }
        },
        aerion::CoreEvent::UsersReplaced { .. } => {}
    }
}

pub fn record_external_bytes(rx_bytes: u64, tx_bytes: u64) {
    let mut inner = TELEMETRY.lock().expect("telemetry lock poisoned");
    inner.download_bytes = rx_bytes;
    inner.upload_bytes = tx_bytes;
}

pub fn record_latency_sample(ms: Option<u64>) {
    let mut inner = TELEMETRY.lock().expect("telemetry lock poisoned");
    inner.latency_ms = ms;
    if inner.latency_history.len() >= MAX_SERIES_SAMPLES {
        inner.latency_history.pop_front();
    }
    inner
        .latency_history
        .push_back(ms.map(|v| v as i64).unwrap_or(-1));
}

pub fn push_log(level: &str, message: &str) {
    let mut inner = TELEMETRY.lock().expect("telemetry lock poisoned");
    if inner.logs.len() >= MAX_LOG_ENTRIES {
        inner.logs.pop_front();
    }
    inner.logs.push_back(LogEntry {
        timestamp_ms: now_ms(),
        level: level.to_string(),
        message: message.to_string(),
    });
}

pub fn list_logs(limit: usize) -> Vec<LogEntry> {
    let inner = TELEMETRY.lock().expect("telemetry lock poisoned");
    let cap = if limit == 0 { MAX_LOG_ENTRIES } else { limit };
    inner
        .logs
        .iter()
        .rev()
        .take(cap)
        .cloned()
        .collect::<Vec<_>>()
        .into_iter()
        .rev()
        .collect()
}

pub fn clear_logs() {
    TELEMETRY
        .lock()
        .expect("telemetry lock poisoned")
        .logs
        .clear();
}

pub fn sample_telemetry(connected: bool) -> TelemetrySnapshot {
    let mut inner = TELEMETRY.lock().expect("telemetry lock poisoned");
    let now = now_ms();
    if connected {
        let elapsed_ms = now.saturating_sub(inner.last_sampled_ms);
        if elapsed_ms >= 500 {
            let up_delta = inner.upload_bytes.saturating_sub(inner.last_sampled_upload);
            let down_delta = inner
                .download_bytes
                .saturating_sub(inner.last_sampled_download);
            inner.upload_speed_bps = (up_delta.saturating_mul(1000)) / elapsed_ms.max(1);
            inner.download_speed_bps = (down_delta.saturating_mul(1000)) / elapsed_ms.max(1);
            inner.last_sampled_upload = inner.upload_bytes;
            inner.last_sampled_download = inner.download_bytes;
            inner.last_sampled_ms = now;
            if inner.upload_history.len() >= MAX_SERIES_SAMPLES {
                inner.upload_history.pop_front();
            }
            if inner.download_history.len() >= MAX_SERIES_SAMPLES {
                inner.download_history.pop_front();
            }
            inner.upload_history.push_back(inner.upload_speed_bps);
            inner.download_history.push_back(inner.download_speed_bps);
        }
    } else {
        inner.upload_speed_bps = 0;
        inner.download_speed_bps = 0;
    }
    let (intranet_ipv4, intranet_ipv6) = detect_intranet_ips();
    TelemetrySnapshot {
        upload_bytes: inner.upload_bytes,
        download_bytes: inner.download_bytes,
        upload_speed_bps: inner.upload_speed_bps,
        download_speed_bps: inner.download_speed_bps,
        upload_history: inner.upload_history.iter().copied().collect(),
        download_history: inner.download_history.iter().copied().collect(),
        latency_ms: inner.latency_ms,
        latency_history: inner.latency_history.iter().copied().collect(),
        active_sessions: inner.active_sessions,
        memory_bytes: current_process_memory_bytes(),
        intranet_ipv4,
        intranet_ipv6,
    }
}

pub fn detect_intranet_ips() -> (Option<String>, Option<String>) {
    let v4 = UdpSocket::bind("0.0.0.0:0")
        .and_then(|sock| {
            sock.connect("223.5.5.5:53")?;
            sock.local_addr()
        })
        .ok()
        .map(|addr| addr.ip())
        .filter(|ip| !ip.is_loopback() && !ip.is_unspecified())
        .map(|ip| ip.to_string());

    let v6 = UdpSocket::bind("[::]:0")
        .and_then(|sock| {
            sock.connect("[2400:3200::1]:53")?;
            sock.local_addr()
        })
        .ok()
        .map(|addr| addr.ip())
        .filter(|ip| matches!(ip, IpAddr::V6(v) if !v.is_loopback() && !v.is_unspecified()))
        .map(|ip| ip.to_string());

    (v4, v6)
}

pub fn current_process_memory_bytes() -> u64 {
    #[cfg(any(target_os = "linux", target_os = "android"))]
    {
        if let Ok(statm) = std::fs::read_to_string("/proc/self/statm")
            && let Some(rss_pages) = statm.split_whitespace().nth(1)
            && let Ok(pages) = rss_pages.parse::<u64>()
        {
            return pages * 4096;
        }
        0
    }
    #[cfg(target_os = "windows")]
    {
        #[repr(C)]
        struct ProcessMemoryCounters {
            cb: u32,
            page_fault_count: u32,
            peak_working_set_size: usize,
            working_set_size: usize,
            quota_peak_paged_pool_usage: usize,
            quota_paged_pool_usage: usize,
            quota_peak_non_paged_pool_usage: usize,
            quota_non_paged_pool_usage: usize,
            pagefile_usage: usize,
            peak_pagefile_usage: usize,
        }
        #[link(name = "psapi")]
        unsafe extern "system" {
            fn K32GetProcessMemoryInfo(
                process: *mut core::ffi::c_void,
                ppsmemcounters: *mut ProcessMemoryCounters,
                cb: u32,
            ) -> i32;
        }
        unsafe {
            let mut counters = core::mem::zeroed::<ProcessMemoryCounters>();
            counters.cb = core::mem::size_of::<ProcessMemoryCounters>() as u32;
            let current_process = -1isize as *mut core::ffi::c_void;
            if K32GetProcessMemoryInfo(current_process, &mut counters, counters.cb) != 0 {
                return counters.working_set_size as u64;
            }
        }
        0
    }
    #[cfg(not(any(target_os = "linux", target_os = "android", target_os = "windows")))]
    {
        0
    }
}
