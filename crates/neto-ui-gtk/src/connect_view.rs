#![cfg(target_os = "linux")]

use crate::format::{format_bytes, format_duration_ms, format_speed};
use gtk::prelude::*;
use neto_core::{EngineSnapshot, NetoController, RoutingMode, telemetry};
use std::rc::Rc;
use std::sync::Arc;
use tokio::runtime::Handle;

pub struct ConnectView {
    pub root: gtk::Box,
    status_dot: gtk::Box,
    status_label: gtk::Label,
    duration_label: gtk::Label,
    toggle_button: gtk::Button,
    node_button_label: gtk::Label,
    node_meta_label: gtk::Label,
    mode_rule_btn: gtk::ToggleButton,
    mode_global_btn: gtk::ToggleButton,
    mode_direct_btn: gtk::ToggleButton,
    up_speed_label: gtk::Label,
    down_speed_label: gtk::Label,
    session_total_label: gtk::Label,
    sys_proxy_switch: gtk::Switch,
    tun_switch: gtk::Switch,
    error_label: gtk::Label,
    updating: Rc<std::cell::Cell<bool>>,
}

impl ConnectView {
    pub fn new(
        controller: Arc<NetoController>,
        rt: Handle,
        on_open_nodes: Rc<dyn Fn()>,
    ) -> Rc<Self> {
        let updating = Rc::new(std::cell::Cell::new(false));

        let root = gtk::Box::new(gtk::Orientation::Vertical, 14);
        root.set_valign(gtk::Align::Center);
        root.set_halign(gtk::Align::Center);
        root.set_width_request(360);
        root.set_margin_top(16);
        root.set_margin_bottom(20);
        root.set_margin_start(20);
        root.set_margin_end(20);

        let status_row = gtk::Box::new(gtk::Orientation::Horizontal, 8);
        status_row.set_halign(gtk::Align::Center);
        let status_dot = gtk::Box::new(gtk::Orientation::Horizontal, 0);
        status_dot.set_size_request(10, 10);
        status_dot.set_valign(gtk::Align::Center);
        status_dot.add_css_class("neto-dot-idle");
        let status_label = gtk::Label::new(Some("未连接"));
        status_label.add_css_class("heading");
        status_row.append(&status_dot);
        status_row.append(&status_label);

        let duration_label = gtk::Label::new(Some("00:00:00"));
        duration_label.add_css_class("monospace");
        duration_label.add_css_class("dim-label");
        duration_label.set_halign(gtk::Align::Center);

        let toggle_button = gtk::Button::with_label("连接");
        toggle_button.set_size_request(152, 48);
        toggle_button.set_halign(gtk::Align::Center);
        toggle_button.add_css_class("pill");
        toggle_button.add_css_class("suggested-action");

        let node_btn = gtk::Button::new();
        node_btn.add_css_class("card");
        let node_box = gtk::Box::new(gtk::Orientation::Horizontal, 10);
        node_box.set_margin_top(10);
        node_box.set_margin_bottom(10);
        node_box.set_margin_start(14);
        node_box.set_margin_end(14);
        let node_button_label = gtk::Label::new(Some("未选择节点"));
        node_button_label.set_hexpand(true);
        node_button_label.set_xalign(0.0);
        node_button_label.set_ellipsize(gtk::pango::EllipsizeMode::End);
        let node_meta_label = gtk::Label::new(None);
        node_meta_label.add_css_class("dim-label");
        let chevron = gtk::Image::from_icon_name("go-next-symbolic");
        node_box.append(&node_button_label);
        node_box.append(&node_meta_label);
        node_box.append(&chevron);
        node_btn.set_child(Some(&node_box));
        {
            let open_nodes = on_open_nodes.clone();
            node_btn.connect_clicked(move |_| open_nodes());
        }

        let mode_box = gtk::Box::new(gtk::Orientation::Horizontal, 0);
        mode_box.add_css_class("linked");
        mode_box.set_halign(gtk::Align::Fill);
        let mode_rule_btn = gtk::ToggleButton::with_label("规则");
        let mode_global_btn = gtk::ToggleButton::with_label("全局");
        let mode_direct_btn = gtk::ToggleButton::with_label("直连");
        mode_global_btn.set_group(Some(&mode_rule_btn));
        mode_direct_btn.set_group(Some(&mode_rule_btn));
        for b in [&mode_rule_btn, &mode_global_btn, &mode_direct_btn] {
            b.set_hexpand(true);
            mode_box.append(b);
        }

        let metrics_grid = gtk::Grid::new();
        metrics_grid.set_column_homogeneous(true);
        metrics_grid.set_column_spacing(8);
        metrics_grid.set_row_spacing(8);

        let up_speed_label = gtk::Label::new(Some("↑ 0 B/s"));
        up_speed_label.add_css_class("monospace");
        let down_speed_label = gtk::Label::new(Some("↓ 0 B/s"));
        down_speed_label.add_css_class("monospace");
        let session_total_label = gtk::Label::new(Some("0 B"));
        session_total_label.add_css_class("monospace");
        session_total_label.add_css_class("dim-label");

        for (col, widget) in [
            (0, &up_speed_label),
            (1, &down_speed_label),
            (2, &session_total_label),
        ] {
            let cell = gtk::Box::new(gtk::Orientation::Vertical, 2);
            cell.add_css_class("card");
            cell.set_margin_top(2);
            widget.set_margin_top(8);
            widget.set_margin_bottom(8);
            widget.set_margin_start(8);
            widget.set_margin_end(8);
            cell.append(widget);
            metrics_grid.attach(&cell, col, 0, 1, 1);
        }

        let takeover_box = gtk::Box::new(gtk::Orientation::Horizontal, 12);
        takeover_box.add_css_class("card");
        let takeover_inner = gtk::Box::new(gtk::Orientation::Horizontal, 16);
        takeover_inner.set_margin_top(8);
        takeover_inner.set_margin_bottom(8);
        takeover_inner.set_margin_start(14);
        takeover_inner.set_margin_end(14);
        takeover_inner.set_hexpand(true);

        let sys_row = gtk::Box::new(gtk::Orientation::Horizontal, 8);
        sys_row.set_hexpand(true);
        let sys_lbl = gtk::Label::new(Some("系统代理"));
        sys_lbl.set_hexpand(true);
        sys_lbl.set_xalign(0.0);
        let sys_proxy_switch = gtk::Switch::new();
        sys_proxy_switch.set_valign(gtk::Align::Center);
        sys_row.append(&sys_lbl);
        sys_row.append(&sys_proxy_switch);

        let tun_row = gtk::Box::new(gtk::Orientation::Horizontal, 8);
        tun_row.set_hexpand(true);
        let tun_lbl = gtk::Label::new(Some("TUN"));
        tun_lbl.set_hexpand(true);
        tun_lbl.set_xalign(0.0);
        let tun_switch = gtk::Switch::new();
        tun_switch.set_valign(gtk::Align::Center);
        tun_row.append(&tun_lbl);
        tun_row.append(&tun_switch);

        takeover_inner.append(&sys_row);
        takeover_inner.append(&tun_row);
        takeover_box.append(&takeover_inner);

        let error_label = gtk::Label::new(None);
        error_label.add_css_class("error");
        error_label.set_wrap(true);
        error_label.set_visible(false);

        root.append(&status_row);
        root.append(&duration_label);
        root.append(&toggle_button);
        root.append(&node_btn);
        root.append(&mode_box);
        root.append(&metrics_grid);
        root.append(&takeover_box);
        root.append(&error_label);

        {
            let ctrl = controller.clone();
            let handle = rt.clone();
            toggle_button.connect_clicked(move |_| {
                let ctrl = ctrl.clone();
                handle.spawn(async move {
                    let _ = ctrl.toggle_connect().await;
                });
            });
        }

        for (btn, mode) in [
            (&mode_rule_btn, RoutingMode::Rule),
            (&mode_global_btn, RoutingMode::Global),
            (&mode_direct_btn, RoutingMode::Direct),
        ] {
            let ctrl = controller.clone();
            let handle = rt.clone();
            let upd = updating.clone();
            btn.connect_toggled(move |b| {
                if upd.get() || !b.is_active() {
                    return;
                }
                let ctrl = ctrl.clone();
                handle.spawn(async move {
                    let _ = ctrl.set_routing_mode(mode).await;
                });
            });
        }

        {
            let ctrl = controller.clone();
            let upd = updating.clone();
            sys_proxy_switch.connect_state_set(move |_, active| {
                if !upd.get() {
                    let _ = ctrl.set_system_proxy_enabled(active);
                }
                gtk::glib::Propagation::Proceed
            });
        }

        {
            let ctrl = controller;
            let handle = rt;
            let upd = updating.clone();
            tun_switch.connect_state_set(move |_, active| {
                if !upd.get() {
                    let ctrl = ctrl.clone();
                    handle.spawn(async move {
                        let _ = ctrl.set_tun_enabled(active).await;
                    });
                }
                gtk::glib::Propagation::Proceed
            });
        }

        Rc::new(Self {
            root,
            status_dot,
            status_label,
            duration_label,
            toggle_button,
            node_button_label,
            node_meta_label,
            mode_rule_btn,
            mode_global_btn,
            mode_direct_btn,
            up_speed_label,
            down_speed_label,
            session_total_label,
            sys_proxy_switch,
            tun_switch,
            error_label,
            updating,
        })
    }

    pub fn apply_snapshot(&self, snap: &EngineSnapshot) {
        self.updating.set(true);
        if snap.connection.connecting {
            self.status_label.set_text("连接中");
            self.toggle_button.set_label("连接中");
            self.toggle_button.set_sensitive(false);
        } else if snap.connection.connected {
            self.status_label.set_text("已连接");
            self.toggle_button.set_label("断开");
            self.toggle_button.set_sensitive(true);
            self.toggle_button.remove_css_class("suggested-action");
            self.toggle_button.add_css_class("destructive-action");
            self.status_dot.remove_css_class("neto-dot-idle");
            self.status_dot.add_css_class("neto-dot-active");
            let elapsed = telemetry::now_ms().saturating_sub(snap.connection.connected_at_ms);
            self.duration_label.set_text(&format_duration_ms(elapsed));
        } else {
            self.status_label.set_text("未连接");
            self.toggle_button.set_label("连接");
            self.toggle_button.set_sensitive(true);
            self.toggle_button.remove_css_class("destructive-action");
            self.toggle_button.add_css_class("suggested-action");
            self.status_dot.remove_css_class("neto-dot-active");
            self.status_dot.add_css_class("neto-dot-idle");
            self.duration_label.set_text("00:00:00");
        }

        let selected_node = snap
            .nodes
            .iter()
            .find(|n| n.index == snap.selected_node_index)
            .or_else(|| snap.nodes.first());
        if let Some(node) = selected_node {
            self.node_button_label.set_text(&node.name);
            let meta = match node.latency_ms {
                Some(ms) => format!("{} · {ms} ms", node.protocol_label),
                None => node.protocol_label.clone(),
            };
            self.node_meta_label.set_text(&meta);
        } else {
            self.node_button_label.set_text("暂无节点");
            self.node_meta_label.set_text("");
        }

        match snap.settings.routing_mode {
            RoutingMode::Rule => self.mode_rule_btn.set_active(true),
            RoutingMode::Global => self.mode_global_btn.set_active(true),
            RoutingMode::Direct => self.mode_direct_btn.set_active(true),
        }

        self.up_speed_label
            .set_text(&format!("↑ {}", format_speed(snap.telemetry.upload_speed_bps)));
        self.down_speed_label
            .set_text(&format!("↓ {}", format_speed(snap.telemetry.download_speed_bps)));
        self.session_total_label.set_text(&format_bytes(
            snap.telemetry
                .upload_bytes
                .saturating_add(snap.telemetry.download_bytes),
        ));

        if self.sys_proxy_switch.is_active() != snap.settings.system_proxy_enabled {
            self.sys_proxy_switch
                .set_active(snap.settings.system_proxy_enabled);
        }
        if self.tun_switch.is_active() != snap.settings.tun_enabled {
            self.tun_switch.set_active(snap.settings.tun_enabled);
        }

        if let Some(err) = &snap.connection.last_error {
            self.error_label.set_text(err);
            self.error_label.set_visible(!err.is_empty());
        } else {
            self.error_label.set_visible(false);
        }
        self.updating.set(false);
    }
}
