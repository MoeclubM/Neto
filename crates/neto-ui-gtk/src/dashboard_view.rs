#![cfg(target_os = "linux")]

use crate::format::{format_bytes, format_speed};
use adw::prelude::*;
use gtk::prelude::*;
use neto_core::{
    DashboardWidgetId, DashboardWidgetItem, EngineSnapshot, NetoController, RoutingMode,
    close_all_connections,
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

pub struct DashboardView {
    pub root: gtk::ScrolledWindow,
    cards_box: gtk::Box,
    customize_box: gtk::Box,
    customize_list: gtk::Box,
    customizing: Rc<Cell<bool>>,
    last_widgets: RefCell<Vec<DashboardWidgetItem>>,
    last_layout_sig: RefCell<String>,
    live_refs: RefCell<LiveWidgetRefs>,
    speed_samples: Rc<RefCell<(Vec<u64>, Vec<u64>)>>,
    controller: Arc<NetoController>,
    rt: Handle,
    on_open_nodes: Rc<dyn Fn()>,
    on_open_connections: RefCell<Option<Rc<dyn Fn()>>>,
}

impl DashboardView {
    pub fn new(
        controller: Arc<NetoController>,
        rt: Handle,
        on_open_nodes: Rc<dyn Fn()>,
    ) -> Rc<Self> {
        let scroll = gtk::ScrolledWindow::builder()
            .hscrollbar_policy(gtk::PolicyType::Never)
            .vexpand(true)
            .build();
        let clamp = adw::Clamp::builder()
            .maximum_size(760)
            .margin_top(12)
            .margin_bottom(20)
            .margin_start(14)
            .margin_end(14)
            .build();
        let outer = gtk::Box::new(gtk::Orientation::Vertical, 10);

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
        outer.append(&toolbar);
        outer.append(&customize_box);
        outer.append(&cards_box);
        clamp.set_child(Some(&outer));
        scroll.set_child(Some(&clamp));

        let customizing = Rc::new(Cell::new(false));
        let view = Rc::new(Self {
            root: scroll,
            cards_box,
            customize_box,
            customize_list,
            customizing,
            last_widgets: RefCell::new(Vec::new()),
            last_layout_sig: RefCell::new(String::new()),
            live_refs: RefCell::new(LiveWidgetRefs::default()),
            speed_samples: Rc::new(RefCell::new((Vec::new(), Vec::new()))),
            controller,
            rt,
            on_open_nodes,
            on_open_connections: RefCell::new(None),
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

    pub fn set_on_open_connections(&self, cb: Rc<dyn Fn()>) {
        *self.on_open_connections.borrow_mut() = Some(cb);
    }

    fn rebuild_customize_rows(&self, snap: &EngineSnapshot) {
        while let Some(child) = self.customize_list.first_child() {
            self.customize_list.remove(&child);
        }
        let group = adw::PreferencesGroup::new();
        let total = snap.settings.dashboard_widgets.len();
        for (idx, item) in snap.settings.dashboard_widgets.iter().enumerate() {
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
        *self.speed_samples.borrow_mut() = (
            snap.telemetry.upload_history.clone(),
            snap.telemetry.download_history.clone(),
        );

        if self.customizing.get() && *self.last_widgets.borrow() != snap.settings.dashboard_widgets {
            self.rebuild_customize_rows(snap);
        }
        *self.last_widgets.borrow_mut() = snap.settings.dashboard_widgets.clone();

        let selected_node = snap
            .nodes
            .iter()
            .find(|n| n.index == snap.selected_node_index)
            .or_else(|| snap.nodes.first());
        let active_prof = snap.profiles.iter().find(|p| p.active);
        let layout_sig = format!(
            "{:?}|{:?}|{}|{}|{}|{}|{}|{:?}|{:?}|{:?}",
            snap.settings.dashboard_widgets,
            snap.settings.routing_mode,
            snap.settings.system_proxy_enabled,
            snap.settings.tun_enabled,
            snap.connection.connected,
            snap.connection.http_proxy_addr,
            snap.settings.node_test_url,
            selected_node.map(|n| (&n.name, &n.protocol_label, n.latency_ms)),
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
            if !item.enabled {
                continue;
            }
            match item.id {
                DashboardWidgetId::NetworkSpeed => {
                    let group = adw::PreferencesGroup::builder().title("实时速率").build();
                    let area = gtk::DrawingArea::new();
                    area.set_content_height(78);
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
                DashboardWidgetId::OutboundMode => {
                    let group = adw::PreferencesGroup::builder().title("出站模式").build();
                    let row_box = gtk::Box::new(gtk::Orientation::Horizontal, 0);
                    row_box.add_css_class("linked");
                    let b_rule = gtk::ToggleButton::with_label("规则");
                    let b_global = gtk::ToggleButton::with_label("全局");
                    let b_direct = gtk::ToggleButton::with_label("直连");
                    b_global.set_group(Some(&b_rule));
                    b_direct.set_group(Some(&b_rule));
                    match snap.settings.routing_mode {
                        RoutingMode::Rule => b_rule.set_active(true),
                        RoutingMode::Global => b_global.set_active(true),
                        RoutingMode::Direct => b_direct.set_active(true),
                    }
                    for (btn, mode) in [
                        (&b_rule, RoutingMode::Rule),
                        (&b_global, RoutingMode::Global),
                        (&b_direct, RoutingMode::Direct),
                    ] {
                        btn.set_hexpand(true);
                        let ctrl = self.controller.clone();
                        let rt = self.rt.clone();
                        btn.connect_toggled(move |b| {
                            if b.is_active() {
                                let ctrl = ctrl.clone();
                                rt.spawn(async move {
                                    let _ = ctrl.set_routing_mode(mode).await;
                                });
                            }
                        });
                        row_box.append(btn);
                    }
                    group.add(&row_box);
                    self.cards_box.append(&group);
                }
                DashboardWidgetId::CurrentOutbound => {
                    let group = adw::PreferencesGroup::builder().title("当前节点").build();
                    let selected = snap
                        .nodes
                        .iter()
                        .find(|n| n.index == snap.selected_node_index)
                        .or_else(|| snap.nodes.first());
                    let title = selected.map(|n| n.name.as_str()).unwrap_or("暂无节点");
                    let sub = selected
                        .map(|n| match n.latency_ms {
                            Some(ms) => format!("{} · {ms} ms", n.protocol_label),
                            None => n.protocol_label.clone(),
                        })
                        .unwrap_or_default();
                    let row = adw::ActionRow::builder()
                        .title(title)
                        .subtitle(&sub)
                        .activatable(true)
                        .build();
                    let open_nodes = self.on_open_nodes.clone();
                    row.connect_activated(move |_| open_nodes());
                    row.add_suffix(&gtk::Image::from_icon_name("go-next-symbolic"));
                    group.add(&row);
                    self.cards_box.append(&group);
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
                        gtk::glib::timeout_add_local(std::time::Duration::from_millis(120), move || {
                            if rx.try_recv().is_ok() {
                                btn_c.set_sensitive(true);
                                return gtk::glib::ControlFlow::Break;
                            }
                            gtk::glib::ControlFlow::Continue
                        });
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
                    group.add(&row);
                    self.cards_box.append(&group);
                }
                DashboardWidgetId::NetworkTakeover => {
                    let group = adw::PreferencesGroup::builder().title("网络接管").build();
                    let sys_row = adw::SwitchRow::builder()
                        .title("系统代理")
                        .subtitle(&if snap.connection.http_proxy_addr.is_empty() {
                            format!("预设端口 {}", snap.settings.mixed_port)
                        } else {
                            snap.connection.http_proxy_addr.clone()
                        })
                        .active(snap.settings.system_proxy_enabled)
                        .build();
                    let ctrl = self.controller.clone();
                    sys_row.connect_active_notify(move |r| {
                        let _ = ctrl.set_system_proxy_enabled(r.is_active());
                    });
                    let tun_row = adw::SwitchRow::builder()
                        .title("TUN 网卡接管")
                        .subtitle(&format!("DNS: {} · MTU {}", snap.settings.vpn_dns_mode, snap.settings.mtu))
                        .active(snap.settings.tun_enabled)
                        .build();
                    let ctrl2 = self.controller.clone();
                    let rt = self.rt.clone();
                    tun_row.connect_active_notify(move |r| {
                        let on = r.is_active();
                        let ctrl2 = ctrl2.clone();
                        rt.spawn(async move {
                            let _ = ctrl2.set_tun_enabled(on).await;
                        });
                    });
                    group.add(&sys_row);
                    group.add(&tun_row);
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
                    let row = adw::ActionRow::builder().title(title).subtitle(&sub).build();
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
