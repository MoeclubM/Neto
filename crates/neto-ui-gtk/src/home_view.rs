#![cfg(target_os = "linux")]

use crate::format::{format_bytes, format_duration_ms, format_speed};
use adw::prelude::*;
use gtk::prelude::*;
use neto_core::{
    DashboardWidgetId, DashboardWidgetItem, EngineSnapshot, NetoController, RoutingMode,
    close_all_connections, telemetry,
};
use std::cell::{Cell, RefCell};
use std::rc::Rc;
use std::sync::Arc;
use tokio::runtime::Handle;

#[derive(Default)]
struct LiveWidgetRefs {
    speed_group: Option<adw::PreferencesGroup>,
    speed_area: Option<gtk::DrawingArea>,
    active_conn_row: Option<adw::ActionRow>,
    traffic_row: Option<adw::ActionRow>,
    quota_row: Option<adw::ActionRow>,
    latency_group: Option<adw::PreferencesGroup>,
    probe_btn: Option<gtk::Button>,
    runtime_row: Option<adw::ActionRow>,
}

pub struct HomeView {
    pub root: gtk::ScrolledWindow,
    extra_header_box: gtk::Box,
    blocked_box: gtk::Box,
    control_box: gtk::Box,
    status_dot: gtk::Box,
    status_label: gtk::Label,
    session_badge: gtk::Label,
    duration_label: gtk::Label,
    endpoint_label: gtk::Label,
    toggle_button: gtk::Button,
    node_button_label: gtk::Label,
    node_meta_label: gtk::Label,
    mode_rule_btn: gtk::ToggleButton,
    mode_global_btn: gtk::ToggleButton,
    mode_direct_btn: gtk::ToggleButton,
    sys_proxy_switch: gtk::Switch,
    tun_switch: gtk::Switch,
    error_label: gtk::Label,
    cards_box: gtk::Box,
    customize_box: gtk::Box,
    customize_list: gtk::Box,
    customizing: Rc<Cell<bool>>,
    updating: Rc<Cell<bool>>,
    show_profiles_summary: Cell<bool>,
    last_widgets: RefCell<Vec<DashboardWidgetItem>>,
    last_layout_sig: RefCell<String>,
    live_refs: RefCell<LiveWidgetRefs>,
    speed_samples: Rc<RefCell<(Vec<u64>, Vec<u64>)>>,
    controller: Arc<NetoController>,
    rt: Handle,
    on_open_connections: RefCell<Option<Rc<dyn Fn()>>>,
    on_open_profiles: RefCell<Option<Rc<dyn Fn()>>>,
}

impl HomeView {
    pub fn new(
        controller: Arc<NetoController>,
        rt: Handle,
        on_open_nodes: Rc<dyn Fn()>,
    ) -> Rc<Self> {
        let updating = Rc::new(Cell::new(false));

        let scroll = gtk::ScrolledWindow::builder()
            .hscrollbar_policy(gtk::PolicyType::Never)
            .vexpand(true)
            .build();
        let clamp = adw::Clamp::builder()
            .maximum_size(780)
            .margin_top(10)
            .margin_bottom(20)
            .margin_start(14)
            .margin_end(14)
            .build();
        let outer = gtk::Box::new(gtk::Orientation::Vertical, 10);

        let extra_header_box = gtk::Box::new(gtk::Orientation::Vertical, 8);
        extra_header_box.set_visible(false);

        let blocked_box = gtk::Box::new(gtk::Orientation::Vertical, 8);
        blocked_box.set_visible(false);

        let control_box = gtk::Box::new(gtk::Orientation::Vertical, 10);

        let conn_card = gtk::Box::new(gtk::Orientation::Horizontal, 14);
        conn_card.add_css_class("card");
        let conn_inner = gtk::Box::new(gtk::Orientation::Horizontal, 14);
        conn_inner.set_hexpand(true);
        conn_inner.set_margin_top(12);
        conn_inner.set_margin_bottom(12);
        conn_inner.set_margin_start(16);
        conn_inner.set_margin_end(16);

        let status_col = gtk::Box::new(gtk::Orientation::Vertical, 4);
        status_col.set_hexpand(true);
        let status_row = gtk::Box::new(gtk::Orientation::Horizontal, 8);
        let status_dot = gtk::Box::new(gtk::Orientation::Horizontal, 0);
        status_dot.set_size_request(10, 10);
        status_dot.set_valign(gtk::Align::Center);
        status_dot.add_css_class("neto-dot-idle");
        let status_label = gtk::Label::new(Some("未连接"));
        status_label.add_css_class("heading");
        let session_badge = gtk::Label::new(None);
        session_badge.add_css_class("caption");
        session_badge.add_css_class("dim-label");
        session_badge.set_visible(false);
        status_row.append(&status_dot);
        status_row.append(&status_label);
        status_row.append(&session_badge);

        let duration_label = gtk::Label::new(Some("00:00:00"));
        duration_label.add_css_class("title-2");
        duration_label.add_css_class("monospace");
        duration_label.set_xalign(0.0);

        let endpoint_label = gtk::Label::new(None);
        endpoint_label.add_css_class("caption");
        endpoint_label.add_css_class("dim-label");
        endpoint_label.add_css_class("monospace");
        endpoint_label.set_xalign(0.0);

        status_col.append(&status_row);
        status_col.append(&duration_label);
        status_col.append(&endpoint_label);

        let toggle_button = gtk::Button::with_label("连接");
        toggle_button.set_size_request(112, 42);
        toggle_button.set_valign(gtk::Align::Center);
        toggle_button.add_css_class("pill");
        toggle_button.add_css_class("suggested-action");

        conn_inner.append(&status_col);
        conn_inner.append(&toggle_button);
        conn_card.append(&conn_inner);

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

        let controls_row = gtk::Box::new(gtk::Orientation::Horizontal, 10);
        let mode_box = gtk::Box::new(gtk::Orientation::Horizontal, 0);
        mode_box.add_css_class("linked");
        mode_box.set_hexpand(true);
        let mode_rule_btn = gtk::ToggleButton::with_label("规则");
        let mode_global_btn = gtk::ToggleButton::with_label("全局");
        let mode_direct_btn = gtk::ToggleButton::with_label("直连");
        mode_global_btn.set_group(Some(&mode_rule_btn));
        mode_direct_btn.set_group(Some(&mode_rule_btn));
        for b in [&mode_rule_btn, &mode_global_btn, &mode_direct_btn] {
            b.set_hexpand(true);
            mode_box.append(b);
        }

        let takeover_box = gtk::Box::new(gtk::Orientation::Horizontal, 0);
        takeover_box.add_css_class("card");
        let takeover_inner = gtk::Box::new(gtk::Orientation::Horizontal, 14);
        takeover_inner.set_margin_top(6);
        takeover_inner.set_margin_bottom(6);
        takeover_inner.set_margin_start(12);
        takeover_inner.set_margin_end(12);

        let sys_row = gtk::Box::new(gtk::Orientation::Horizontal, 6);
        let sys_lbl = gtk::Label::new(Some("系统代理"));
        let sys_proxy_switch = gtk::Switch::new();
        sys_proxy_switch.set_valign(gtk::Align::Center);
        sys_row.append(&sys_lbl);
        sys_row.append(&sys_proxy_switch);

        let tun_row = gtk::Box::new(gtk::Orientation::Horizontal, 6);
        let tun_lbl = gtk::Label::new(Some("TUN"));
        let tun_switch = gtk::Switch::new();
        tun_switch.set_valign(gtk::Align::Center);
        tun_row.append(&tun_lbl);
        tun_row.append(&tun_switch);

        takeover_inner.append(&sys_row);
        takeover_inner.append(&tun_row);
        takeover_box.append(&takeover_inner);

        controls_row.append(&mode_box);
        controls_row.append(&takeover_box);

        let error_label = gtk::Label::new(None);
        error_label.add_css_class("error");
        error_label.set_wrap(true);
        error_label.set_xalign(0.0);
        error_label.set_visible(false);

        control_box.append(&conn_card);
        control_box.append(&node_btn);
        control_box.append(&controls_row);
        control_box.append(&error_label);

        let toolbar = gtk::Box::new(gtk::Orientation::Horizontal, 8);
        let spacer = gtk::Box::new(gtk::Orientation::Horizontal, 0);
        spacer.set_hexpand(true);
        let customize_btn = gtk::ToggleButton::with_label("自定义");
        customize_btn.add_css_class("flat");
        let reset_btn = gtk::Button::with_label("恢复默认");
        reset_btn.add_css_class("flat");
        reset_btn.set_visible(false);
        toolbar.append(&spacer);
        toolbar.append(&reset_btn);
        toolbar.append(&customize_btn);

        let customize_box = gtk::Box::new(gtk::Orientation::Vertical, 6);
        customize_box.set_visible(false);
        let customize_list = gtk::Box::new(gtk::Orientation::Vertical, 6);
        customize_box.append(&customize_list);

        let cards_box = gtk::Box::new(gtk::Orientation::Vertical, 10);

        outer.append(&extra_header_box);
        outer.append(&blocked_box);
        outer.append(&control_box);
        outer.append(&toolbar);
        outer.append(&customize_box);
        outer.append(&cards_box);
        clamp.set_child(Some(&outer));
        scroll.set_child(Some(&clamp));

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
            let ctrl = controller.clone();
            let handle = rt.clone();
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

        let customizing = Rc::new(Cell::new(false));
        let view = Rc::new(Self {
            root: scroll,
            extra_header_box,
            blocked_box,
            control_box,
            status_dot,
            status_label,
            session_badge,
            duration_label,
            endpoint_label,
            toggle_button,
            node_button_label,
            node_meta_label,
            mode_rule_btn,
            mode_global_btn,
            mode_direct_btn,
            sys_proxy_switch,
            tun_switch,
            error_label,
            cards_box,
            customize_box,
            customize_list,
            customizing,
            updating,
            show_profiles_summary: Cell::new(true),
            last_widgets: RefCell::new(Vec::new()),
            last_layout_sig: RefCell::new(String::new()),
            live_refs: RefCell::new(LiveWidgetRefs::default()),
            speed_samples: Rc::new(RefCell::new((Vec::new(), Vec::new()))),
            controller,
            rt,
            on_open_connections: RefCell::new(None),
            on_open_profiles: RefCell::new(None),
        });

        {
            let v = view.clone();
            let rbtn = reset_btn.clone();
            customize_btn.connect_toggled(move |btn| {
                let active = btn.is_active();
                v.customizing.set(active);
                v.customize_box.set_visible(active);
                rbtn.set_visible(active);
                v.rebuild_customize_rows(&v.controller.snapshot());
            });
        }
        {
            let v = view.clone();
            reset_btn.connect_clicked(move |_| {
                let _ = v.controller.reset_dashboard_widgets();
                let snap = v.controller.snapshot();
                v.rebuild_customize_rows(&snap);
                *v.last_layout_sig.borrow_mut() = String::new();
                v.apply_snapshot(&snap);
            });
        }

        view
    }

    pub fn set_extra_header(&self, widget: Option<&impl IsA<gtk::Widget>>) {
        while let Some(child) = self.extra_header_box.first_child() {
            self.extra_header_box.remove(&child);
        }
        if let Some(w) = widget {
            self.extra_header_box.append(w);
            self.extra_header_box.set_visible(true);
        } else {
            self.extra_header_box.set_visible(false);
        }
    }

    pub fn set_blocked_widget(&self, widget: Option<&impl IsA<gtk::Widget>>) {
        while let Some(child) = self.blocked_box.first_child() {
            self.blocked_box.remove(&child);
        }
        if let Some(w) = widget {
            self.blocked_box.append(w);
        }
    }

    pub fn set_blocked(&self, blocked: bool) {
        let has_widget = self.blocked_box.first_child().is_some();
        self.blocked_box.set_visible(blocked && has_widget);
        self.control_box.set_visible(!blocked);
    }

    pub fn set_show_profiles_summary(&self, show: bool) {
        self.show_profiles_summary.set(show);
        *self.last_layout_sig.borrow_mut() = String::new();
    }

    pub fn set_on_open_connections(&self, cb: Rc<dyn Fn()>) {
        *self.on_open_connections.borrow_mut() = Some(cb);
    }

    pub fn set_on_open_profiles(&self, cb: Rc<dyn Fn()>) {
        *self.on_open_profiles.borrow_mut() = Some(cb);
    }

    fn visible_widget(&self, id: DashboardWidgetId) -> bool {
        !(id == DashboardWidgetId::ProfilesSummary && !self.show_profiles_summary.get())
    }

    fn rebuild_customize_rows(&self, snap: &EngineSnapshot) {
        while let Some(child) = self.customize_list.first_child() {
            self.customize_list.remove(&child);
        }
        let group = adw::PreferencesGroup::new();
        let visible_items: Vec<&DashboardWidgetItem> = snap
            .settings
            .dashboard_widgets
            .iter()
            .filter(|w| self.visible_widget(w.id))
            .collect();
        let total = visible_items.len();
        for (idx, item) in visible_items.into_iter().enumerate() {
            let row = adw::ActionRow::builder().title(item.id.title_zh()).build();
            let up_btn = gtk::Button::from_icon_name("go-up-symbolic");
            up_btn.add_css_class("flat");
            up_btn.set_valign(gtk::Align::Center);
            up_btn.set_sensitive(idx > 0);
            let down_btn = gtk::Button::from_icon_name("go-down-symbolic");
            down_btn.add_css_class("flat");
            down_btn.set_valign(gtk::Align::Center);
            down_btn.set_sensitive(idx + 1 < total);
            let sw = gtk::Switch::new();
            sw.set_active(item.enabled);
            sw.set_valign(gtk::Align::Center);

            let id = item.id;
            {
                let ctrl = self.controller.clone();
                up_btn.connect_clicked(move |_| {
                    let _ = ctrl.update_dashboard_widget(id, None, Some(-1));
                });
            }
            {
                let ctrl = self.controller.clone();
                down_btn.connect_clicked(move |_| {
                    let _ = ctrl.update_dashboard_widget(id, None, Some(1));
                });
            }
            {
                let ctrl = self.controller.clone();
                sw.connect_state_set(move |_, en| {
                    let _ = ctrl.update_dashboard_widget(id, Some(en), None);
                    gtk::glib::Propagation::Proceed
                });
            }
            row.add_suffix(&up_btn);
            row.add_suffix(&down_btn);
            row.add_suffix(&sw);
            group.add(&row);
        }
        self.customize_list.append(&group);
    }

    pub fn apply_snapshot(&self, snap: &EngineSnapshot) {
        self.updating.set(true);
        if snap.connection.connecting {
            self.status_label.set_text("连接中");
            self.toggle_button.set_label("连接中");
            self.toggle_button.set_sensitive(false);
            self.session_badge.set_visible(false);
            self.endpoint_label.set_text(&format!("预设端口 {}", snap.settings.mixed_port));
        } else if snap.connection.connected {
            self.status_label.set_text("已连接");
            self.toggle_button.set_label("断开");
            self.toggle_button.set_sensitive(true);
            self.toggle_button.remove_css_class("suggested-action");
            self.toggle_button.add_css_class("destructive-action");
            self.status_dot.remove_css_class("neto-dot-idle");
            self.status_dot.add_css_class("neto-dot-active");
            let kind_lbl = match snap.connection.session_kind.as_str() {
                "tun" => "TUN",
                "route" => "分流",
                "socks" => "代理",
                _ => "",
            };
            self.session_badge.set_text(kind_lbl);
            self.session_badge.set_visible(!kind_lbl.is_empty());
            let elapsed = telemetry::now_ms().saturating_sub(snap.connection.connected_at_ms);
            self.duration_label.set_text(&format_duration_ms(elapsed));
            let addr = if snap.connection.http_proxy_addr.is_empty() {
                &snap.connection.socks_addr
            } else {
                &snap.connection.http_proxy_addr
            };
            self.endpoint_label.set_text(addr);
        } else {
            self.status_label.set_text("未连接");
            self.toggle_button.set_label("连接");
            self.toggle_button.set_sensitive(true);
            self.toggle_button.remove_css_class("destructive-action");
            self.toggle_button.add_css_class("suggested-action");
            self.status_dot.remove_css_class("neto-dot-active");
            self.status_dot.add_css_class("neto-dot-idle");
            self.session_badge.set_visible(false);
            self.duration_label.set_text("00:00:00");
            self.endpoint_label.set_text(&format!("预设端口 {}", snap.settings.mixed_port));
        }

        let selected_node = snap
            .nodes
            .iter()
            .find(|n| n.index == snap.selected_node_index)
            .or_else(|| snap.nodes.first());
        if snap.settings.routing_mode == RoutingMode::Direct {
            self.node_button_label.set_text("DIRECT");
            self.node_meta_label.set_text("直连出站");
        } else if let Some(node) = selected_node {
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

        *self.speed_samples.borrow_mut() = (
            snap.telemetry.upload_history.clone(),
            snap.telemetry.download_history.clone(),
        );

        if self.customizing.get() && *self.last_widgets.borrow() != snap.settings.dashboard_widgets
        {
            self.rebuild_customize_rows(snap);
        }
        *self.last_widgets.borrow_mut() = snap.settings.dashboard_widgets.clone();

        let active_prof = snap.profiles.iter().find(|p| p.active);
        let layout_sig = format!(
            "{:?}|{}|{}|{}|{:?}|{:?}",
            snap.settings.dashboard_widgets,
            self.show_profiles_summary.get(),
            snap.connection.connected,
            snap.settings.node_test_url,
            active_prof.map(|p| (&p.id, &p.name, p.node_count, p.rule_count, p.total_bytes)),
            snap.last_network_check.as_ref().map(|c| c.checked_at_ms),
        );

        if *self.last_layout_sig.borrow() != layout_sig {
            *self.last_layout_sig.borrow_mut() = layout_sig;
            self.rebuild_cards(snap);
        }
        self.update_live_values(snap);
    }

    fn rebuild_cards(&self, snap: &EngineSnapshot) {
        while let Some(child) = self.cards_box.first_child() {
            self.cards_box.remove(&child);
        }
        let mut refs = LiveWidgetRefs::default();

        for item in &snap.settings.dashboard_widgets {
            if !item.enabled || !self.visible_widget(item.id) {
                continue;
            }
            match item.id {
                DashboardWidgetId::NetworkSpeed => {
                    let group = adw::PreferencesGroup::builder().title("实时速率").build();
                    let area = gtk::DrawingArea::new();
                    area.set_content_height(76);
                    let samples = self.speed_samples.clone();
                    area.set_draw_func(move |_, cr, w, h| {
                        let (up, down) = samples.borrow().clone();
                        let peak = up
                            .iter()
                            .chain(down.iter())
                            .copied()
                            .max()
                            .unwrap_or(1)
                            .max(1) as f64;
                        draw_series(cr, w as f64, h as f64, &down, peak, (0.20, 0.52, 0.90));
                        draw_series(cr, w as f64, h as f64, &up, peak, (0.18, 0.72, 0.50));
                    });
                    let card = gtk::Box::new(gtk::Orientation::Vertical, 0);
                    card.add_css_class("card");
                    area.set_margin_top(8);
                    area.set_margin_bottom(8);
                    area.set_margin_start(12);
                    area.set_margin_end(12);
                    card.append(&area);
                    group.add(&card);
                    self.cards_box.append(&group);
                    refs.speed_group = Some(group);
                    refs.speed_area = Some(area);
                }
                DashboardWidgetId::ActiveConnections => {
                    let group = adw::PreferencesGroup::builder().title("活跃会话").build();
                    if snap.connection.connected {
                        let close_btn = gtk::Button::with_label("断开全部");
                        close_btn.add_css_class("flat");
                        close_btn.connect_clicked(|_| {
                            let _ = close_all_connections();
                        });
                        group.set_header_suffix(Some(&close_btn));
                    }
                    let open_cb = self.on_open_connections.borrow().clone();
                    let row = adw::ActionRow::builder()
                        .activatable(open_cb.is_some())
                        .build();
                    if let Some(cb) = open_cb {
                        row.connect_activated(move |_| cb());
                        row.add_suffix(&gtk::Image::from_icon_name("go-next-symbolic"));
                    }
                    group.add(&row);
                    self.cards_box.append(&group);
                    refs.active_conn_row = Some(row);
                }
                DashboardWidgetId::ExitCheck => {
                    let group = adw::PreferencesGroup::builder().title("出口检测").build();
                    let check_btn = gtk::Button::with_label("检测");
                    check_btn.add_css_class("flat");
                    let ctrl = self.controller.clone();
                    let rt = self.rt.clone();
                    check_btn.connect_clicked(move |btn| {
                        btn.set_sensitive(false);
                        let ctrl = ctrl.clone();
                        let (tx, rx) = std::sync::mpsc::channel();
                        rt.spawn(async move {
                            let _ = ctrl.check_connectivity().await;
                            let _ = tx.send(());
                        });
                        let btn_c = btn.clone();
                        gtk::glib::timeout_add_local(
                            std::time::Duration::from_millis(120),
                            move || {
                                if rx.try_recv().is_ok() {
                                    btn_c.set_sensitive(true);
                                    return gtk::glib::ControlFlow::Break;
                                }
                                gtk::glib::ControlFlow::Continue
                            },
                        );
                    });
                    group.set_header_suffix(Some(&check_btn));
                    let (title, sub) = match &snap.last_network_check {
                        None => ("未检测".to_string(), "检测出口 IP 与站点延迟".to_string()),
                        Some(res) => {
                            let ip_parts: Vec<&str> = [
                                res.exit_ip.as_deref(),
                                res.exit_country.as_deref(),
                                res.exit_isp.as_deref(),
                            ]
                            .into_iter()
                            .flatten()
                            .filter(|s| !s.is_empty())
                            .collect();
                            let t_str = if ip_parts.is_empty() {
                                "未识别出口 IP".to_string()
                            } else {
                                ip_parts.join(" · ")
                            };
                            let s_str = res
                                .targets
                                .iter()
                                .map(|t| match t.latency_ms {
                                    Some(ms) => format!("{} {ms}ms", t.name),
                                    None => format!("{} 超时", t.name),
                                })
                                .collect::<Vec<_>>()
                                .join(" · ");
                            (t_str, s_str)
                        }
                    };
                    let row = adw::ActionRow::builder().title(&title).subtitle(&sub).build();
                    row.set_use_markup(false);
                    group.add(&row);
                    self.cards_box.append(&group);
                }
                DashboardWidgetId::TrafficUsage => {
                    let group = adw::PreferencesGroup::builder().title("流量统计").build();
                    let row = adw::ActionRow::new();
                    group.add(&row);
                    refs.traffic_row = Some(row);
                    if let Some(active_prof) = snap.profiles.iter().find(|p| p.active)
                        && active_prof.total_bytes > 0
                    {
                        let quota_row = adw::ActionRow::builder().title("订阅配额").build();
                        group.add(&quota_row);
                        refs.quota_row = Some(quota_row);
                    }
                    self.cards_box.append(&group);
                }
                DashboardWidgetId::ProfilesSummary => {
                    let group = adw::PreferencesGroup::builder().title("订阅概览").build();
                    let active = snap.profiles.iter().find(|p| p.active);
                    let title = active.map(|p| p.name.as_str()).unwrap_or("未配置订阅");
                    let sub = active
                        .map(|p| format!("{} 个节点 · {} 条规则", p.node_count, p.rule_count))
                        .unwrap_or_else(|| "暂无订阅".to_string());
                    let open_prof = self.on_open_profiles.borrow().clone();
                    let row = adw::ActionRow::builder()
                        .title(title)
                        .subtitle(&sub)
                        .activatable(open_prof.is_some())
                        .build();
                    row.set_use_markup(false);
                    if let Some(cb) = open_prof {
                        row.connect_activated(move |_| cb());
                    }
                    if let Some(prof) = active
                        && !prof.url.is_empty()
                    {
                        let refresh_btn = gtk::Button::from_icon_name("view-refresh-symbolic");
                        refresh_btn.add_css_class("flat");
                        refresh_btn.set_valign(gtk::Align::Center);
                        let ctrl = self.controller.clone();
                        let rt = self.rt.clone();
                        let pid = prof.id.clone();
                        refresh_btn.connect_clicked(move |_| {
                            let ctrl = ctrl.clone();
                            let pid = pid.clone();
                            rt.spawn(async move {
                                let _ = ctrl.refresh_profile(Some(&pid)).await;
                            });
                        });
                        row.add_suffix(&refresh_btn);
                    }
                    group.add(&row);
                    self.cards_box.append(&group);
                }
                DashboardWidgetId::LatencyProbe => {
                    let group = adw::PreferencesGroup::builder().title("连通延迟").build();
                    let row = adw::ActionRow::builder()
                        .title("隧道连通探测")
                        .subtitle(&snap.settings.node_test_url)
                        .build();
                    row.set_use_markup(false);
                    let probe_btn = gtk::Button::with_label("探测");
                    probe_btn.add_css_class("flat");
                    probe_btn.set_valign(gtk::Align::Center);
                    let ctrl = self.controller.clone();
                    let rt = self.rt.clone();
                    probe_btn.connect_clicked(move |_| {
                        let ctrl = ctrl.clone();
                        rt.spawn(async move {
                            let _ = ctrl.probe_active_tunnel().await;
                        });
                    });
                    row.add_suffix(&probe_btn);
                    group.add(&row);
                    self.cards_box.append(&group);
                    refs.latency_group = Some(group);
                    refs.probe_btn = Some(probe_btn);
                }
                DashboardWidgetId::RuntimeInfo => {
                    let group = adw::PreferencesGroup::builder().title("运行信息").build();
                    let info_row = adw::ActionRow::new();
                    group.add(&info_row);
                    self.cards_box.append(&group);
                    refs.runtime_row = Some(info_row);
                }
            }
        }
        *self.live_refs.borrow_mut() = refs;
    }

    fn update_live_values(&self, snap: &EngineSnapshot) {
        let refs = self.live_refs.borrow();
        if let Some(group) = &refs.speed_group {
            group.set_description(Some(&format!(
                "↑ {}   ↓ {}",
                format_speed(snap.telemetry.upload_speed_bps),
                format_speed(snap.telemetry.download_speed_bps)
            )));
        }
        if let Some(area) = &refs.speed_area {
            area.queue_draw();
        }
        if let Some(row) = &refs.active_conn_row {
            row.set_title(&format!("并发 {} 个会话", snap.telemetry.active_sessions));
            row.set_subtitle(&format!(
                "累计 ↑ {} · ↓ {}",
                format_bytes(snap.telemetry.upload_bytes),
                format_bytes(snap.telemetry.download_bytes)
            ));
        }
        if let Some(row) = &refs.traffic_row {
            row.set_title(&format!(
                "会话流量 {}",
                format_bytes(
                    snap.telemetry
                        .upload_bytes
                        .saturating_add(snap.telemetry.download_bytes)
                )
            ));
            row.set_subtitle(&format!(
                "上传 {} · 下载 {}",
                format_bytes(snap.telemetry.upload_bytes),
                format_bytes(snap.telemetry.download_bytes)
            ));
        }
        if let (Some(quota_row), Some(active_prof)) =
            (&refs.quota_row, snap.profiles.iter().find(|p| p.active))
            && active_prof.total_bytes > 0
        {
            quota_row.set_subtitle(&format!(
                "{} / {}",
                format_bytes(active_prof.used_bytes),
                format_bytes(active_prof.total_bytes)
            ));
        }
        if let Some(group) = &refs.latency_group {
            let lat_str = snap
                .telemetry
                .latency_ms
                .map(|m| format!("{m} ms"))
                .unwrap_or_else(|| "- ms".to_string());
            group.set_description(Some(&lat_str));
        }
        if let Some(btn) = &refs.probe_btn {
            btn.set_sensitive(snap.connection.connected);
        }
        if let Some(row) = &refs.runtime_row {
            let ip_text = match (&snap.telemetry.intranet_ipv4, &snap.telemetry.intranet_ipv6) {
                (Some(v4), Some(v6)) => format!("{v4} · {v6}"),
                (Some(v4), None) => v4.clone(),
                (None, Some(v6)) => v6.clone(),
                (None, None) => "-".to_string(),
            };
            row.set_title(&format!("内网地址 {ip_text}"));
            row.set_subtitle(&format!(
                "活跃会话 {} · 内存 {}",
                snap.telemetry.active_sessions,
                format_bytes(snap.telemetry.memory_bytes)
            ));
        }
    }
}

fn draw_series(
    cr: &gtk::cairo::Context,
    w: f64,
    h: f64,
    values: &[u64],
    peak: f64,
    rgb: (f64, f64, f64),
) {
    if values.len() < 2 || w <= 2.0 || h <= 2.0 {
        return;
    }
    cr.set_source_rgb(rgb.0, rgb.1, rgb.2);
    cr.set_line_width(2.0);
    for (i, &val) in values.iter().enumerate() {
        let x = (i as f64 / (values.len() - 1) as f64) * w;
        let ratio = (val as f64 / peak).clamp(0.0, 1.0);
        let y = (h - 4.0) * (1.0 - ratio) + 2.0;
        if i == 0 {
            cr.move_to(x, y);
        } else {
            cr.line_to(x, y);
        }
    }
    let _ = cr.stroke();
}
