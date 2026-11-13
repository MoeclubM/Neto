#![cfg(target_os = "linux")]

use adw::prelude::*;
use gtk::prelude::*;
use neto_core::{EngineSnapshot, NetoController, telemetry};
use serde_json::json;
use std::cell::{Cell, RefCell};
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
    log_level: Rc<RefCell<String>>,
    log_filter: Rc<RefCell<String>>,
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
        let copy_env_row = adw::ActionRow::builder().title("终端代理环境变量").build();
        let copy_env_btn = gtk::Button::with_label("复制");
        copy_env_btn.add_css_class("flat");
        copy_env_btn.set_valign(gtk::Align::Center);
        copy_env_row.add_suffix(&copy_env_btn);
        net_group.add(&port_row);
        net_group.add(&lan_row);
        net_group.add(&ipv6_row);
        net_group.add(&autostart_row);
        net_group.add(&copy_env_row);

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
        let log_filter_bar = gtk::Box::new(gtk::Orientation::Horizontal, 8);
        log_filter_bar.set_margin_bottom(6);
        let lvl_box = gtk::Box::new(gtk::Orientation::Horizontal, 0);
        lvl_box.add_css_class("linked");
        let b_all = gtk::ToggleButton::with_label("全部");
        let b_info = gtk::ToggleButton::with_label("INFO");
        let b_warn = gtk::ToggleButton::with_label("WARN");
        let b_err = gtk::ToggleButton::with_label("ERR");
        b_info.set_group(Some(&b_all));
        b_warn.set_group(Some(&b_all));
        b_err.set_group(Some(&b_all));
        b_all.set_active(true);
        for b in [&b_all, &b_info, &b_warn, &b_err] {
            lvl_box.append(b);
        }
        let log_search = gtk::SearchEntry::new();
        log_search.set_placeholder_text(Some("筛选日志内容"));
        log_search.set_hexpand(true);
        log_filter_bar.append(&lvl_box);
        log_filter_bar.append(&log_search);
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
        log_group.add(&log_filter_bar);
        log_group.add(&log_scroll);

        page.add(&net_group);
        page.add(&dns_group);
        page.add(&log_group);

        let log_level = Rc::new(RefCell::new("all".to_string()));
        let log_filter = Rc::new(RefCell::new(String::new()));

        {
            let ctrl = controller.clone();
            copy_env_btn.connect_clicked(move |_| {
                let snap = ctrl.snapshot();
                let http = if snap.connection.http_proxy_addr.is_empty() {
                    format!("127.0.0.1:{}", snap.settings.mixed_port)
                } else {
                    snap.connection.http_proxy_addr.clone()
                };
                let socks = if snap.connection.socks_addr.is_empty() {
                    http.clone()
                } else {
                    snap.connection.socks_addr.clone()
                };
                let cmd = format!(
                    "export http_proxy=http://{http} https_proxy=http://{http} all_proxy=socks5://{socks}"
                );
                if let Some(display) = gtk::gdk::Display::default() {
                    display.clipboard().set_text(&cmd);
                }
            });
        }
        for (btn, key) in [(&b_all, "all"), (&b_info, "info"), (&b_warn, "warn"), (&b_err, "error")] {
            let lvl = log_level.clone();
            btn.connect_toggled(move |b| {
                if b.is_active() {
                    *lvl.borrow_mut() = key.to_string();
                }
            });
        }
        {
            let flt = log_filter.clone();
            log_search.connect_search_changed(move |entry| {
                *flt.borrow_mut() = entry.text().to_string();
            });
        }
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
            log_level,
            log_filter,
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
        let lvl = self.log_level.borrow().clone();
        let flt = self.log_filter.borrow().clone();
        let logs = telemetry::list_logs_filtered(120, Some(&lvl), Some(&flt));
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
