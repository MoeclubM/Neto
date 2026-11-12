#![cfg(target_os = "linux")]

use adw::prelude::*;
use gtk::prelude::*;
use neto_core::{EngineSnapshot, NetoController, telemetry};
use serde_json::json;
use std::cell::Cell;
use std::rc::Rc;
use std::sync::Arc;

pub struct SettingsView {
    pub root: adw::PreferencesPage,
    port_row: adw::EntryRow,
    lan_row: adw::SwitchRow,
    ipv6_row: adw::SwitchRow,
    autostart_row: adw::SwitchRow,
    node_dns_row: adw::EntryRow,
    overseas_dns_row: adw::EntryRow,
    direct_dns_row: adw::EntryRow,
    test_url_row: adw::EntryRow,
    logs_buffer: gtk::TextBuffer,
    initialized: Cell<bool>,
}

impl SettingsView {
    pub fn new(controller: Arc<NetoController>) -> Rc<Self> {
        let page = adw::PreferencesPage::new();

        let net_group = adw::PreferencesGroup::builder().title("代理与网络").build();
        let port_row = adw::EntryRow::builder()
            .title("本地混合端口")
            .show_apply_button(true)
            .build();
        let lan_row = adw::SwitchRow::builder().title("允许局域网连接").build();
        let ipv6_row = adw::SwitchRow::builder().title("启用 IPv6").build();
        let autostart_row = adw::SwitchRow::builder().title("开机自动启动").build();
        net_group.add(&port_row);
        net_group.add(&lan_row);
        net_group.add(&ipv6_row);
        net_group.add(&autostart_row);

        let dns_group = adw::PreferencesGroup::builder().title("DNS 与连通测速").build();
        let node_dns_row = adw::EntryRow::builder()
            .title("节点解析 DoH")
            .show_apply_button(true)
            .build();
        let overseas_dns_row = adw::EntryRow::builder()
            .title("代理 DNS")
            .show_apply_button(true)
            .build();
        let direct_dns_row = adw::EntryRow::builder()
            .title("直连 DNS")
            .show_apply_button(true)
            .build();
        let test_url_row = adw::EntryRow::builder()
            .title("测速地址")
            .show_apply_button(true)
            .build();
        dns_group.add(&node_dns_row);
        dns_group.add(&overseas_dns_row);
        dns_group.add(&direct_dns_row);
        dns_group.add(&test_url_row);

        let log_group = adw::PreferencesGroup::builder().title("运行日志").build();
        let clear_btn = gtk::Button::with_label("清空");
        clear_btn.add_css_class("flat");
        log_group.set_header_suffix(Some(&clear_btn));
        let logs_view = gtk::TextView::builder()
            .editable(false)
            .monospace(true)
            .wrap_mode(gtk::WrapMode::WordChar)
            .build();
        let logs_buffer = logs_view.buffer();
        let log_scroll = gtk::ScrolledWindow::builder()
            .min_content_height(160)
            .child(&logs_view)
            .build();
        log_scroll.add_css_class("card");
        log_group.add(&log_scroll);

        page.add(&net_group);
        page.add(&dns_group);
        page.add(&log_group);

        {
            let ctrl = controller.clone();
            port_row.connect_apply(move |r| {
                if let Ok(port) = r.text().trim().parse::<u16>() {
                    let _ = ctrl.update_settings(&json!({ "mixed_port": port }));
                }
            });
        }
        {
            let ctrl = controller.clone();
            lan_row.connect_active_notify(move |r| {
                let _ = ctrl.update_settings(&json!({ "allow_lan": r.is_active() }));
            });
        }
        {
            let ctrl = controller.clone();
            ipv6_row.connect_active_notify(move |r| {
                let _ = ctrl.update_settings(&json!({ "ipv6_enabled": r.is_active() }));
            });
        }
        {
            let ctrl = controller.clone();
            autostart_row.connect_active_notify(move |r| {
                let _ = ctrl.update_settings(&json!({ "autostart": r.is_active() }));
            });
        }
        for (row, field) in [
            (&node_dns_row, "node_dns"),
            (&overseas_dns_row, "overseas_dns"),
            (&direct_dns_row, "direct_dns"),
            (&test_url_row, "node_test_url"),
        ] {
            let ctrl = controller.clone();
            row.connect_apply(move |r| {
                let _ = ctrl.update_settings(&json!({ field: r.text().to_string() }));
            });
        }
        {
            let buf = logs_buffer.clone();
            clear_btn.connect_clicked(move |_| {
                telemetry::clear_logs();
                buf.set_text("");
            });
        }

        Rc::new(Self {
            root: page,
            port_row,
            lan_row,
            ipv6_row,
            autostart_row,
            node_dns_row,
            overseas_dns_row,
            direct_dns_row,
            test_url_row,
            logs_buffer,
            initialized: Cell::new(false),
        })
    }

    pub fn apply_snapshot(&self, snap: &EngineSnapshot) {
        if !self.initialized.get() {
            self.port_row
                .set_text(&snap.settings.mixed_port.to_string());
            self.lan_row.set_active(snap.settings.allow_lan);
            self.ipv6_row.set_active(snap.settings.ipv6_enabled);
            self.autostart_row.set_active(snap.settings.autostart);
            self.node_dns_row.set_text(&snap.settings.node_dns);
            self.overseas_dns_row.set_text(&snap.settings.overseas_dns);
            self.direct_dns_row.set_text(&snap.settings.direct_dns);
            self.test_url_row.set_text(&snap.settings.node_test_url);
            self.initialized.set(true);
        }
        let logs = telemetry::list_logs(80);
        let text = logs
            .iter()
            .map(|l| format!("[{}] {}", l.level, l.message))
            .collect::<Vec<_>>()
            .join("\n");
        if self
            .logs_buffer
            .text(
                &self.logs_buffer.start_iter(),
                &self.logs_buffer.end_iter(),
                false,
            )
            .as_str()
            != text
        {
            self.logs_buffer.set_text(&text);
        }
    }
}
