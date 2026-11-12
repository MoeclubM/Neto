pub fn format_bytes(bytes: u64) -> String {
    const KIB: f64 = 1024.0;
    const MIB: f64 = KIB * 1024.0;
    const GIB: f64 = MIB * 1024.0;
    const TIB: f64 = GIB * 1024.0;
    let val = bytes as f64;
    if val >= TIB {
        format!("{:.2} TiB", val / TIB)
    } else if val >= GIB {
        format!("{:.2} GiB", val / GIB)
    } else if val >= MIB {
        format!("{:.1} MiB", val / MIB)
    } else if val >= KIB {
        format!("{:.1} KiB", val / KIB)
    } else {
        format!("{bytes} B")
    }
}

pub fn format_speed(bps: u64) -> String {
    format!("{}/s", format_bytes(bps))
}

pub fn format_duration_ms(elapsed_ms: u64) -> String {
    let total_secs = elapsed_ms / 1000;
    let h = total_secs / 3600;
    let m = (total_secs % 3600) / 60;
    let s = total_secs % 60;
    format!("{h:02}:{m:02}:{s:02}")
}
