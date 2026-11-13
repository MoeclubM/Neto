#![cfg(target_os = "linux")]

use adw::prelude::*;
use gtk::prelude::*;
use neto_core::{EngineSnapshot, NetoController, parse_rule_items};
use serde_json::json;
use std::cell::{Cell, RefCell};
use std::rc::Rc;
use std::sync::Arc;
use tokio::runtime::Handle;

const RULE_TYPES: [&str; 5] = [
    "DOMAIN-SUFFIX",
    "DOMAIN",
    "DOMAIN-KEYWORD",
    "IP-CIDR",
    "DST-PORT",
];

pub struct RulesView {
    pub root: gtk::Box,
    rules_list_box: gtk::Box,
    rule_count_label: gtk::Label,
    target_combo: adw::ComboRow,
    target_options: RefCell<Vec<String>>,
    route_buffer: gtk::TextBuffer,
    override_dirty: Rc<Cell<bool>>,
    override_initialized: Cell<bool>,
    rule_filter: Rc<RefCell<String>>,
    last_rule_sig: RefCell<String>,
    last_targets_sig: RefCell<String>,
    controller: Arc<NetoController>,
    rt: Handle,
}

impl RulesView {
    pub fn new(controller: Arc<NetoController>, rt: Handle) -> Rc<Self> {
        let root = gtk::Box::new(gtk::Orientation::Vertical, 8);
        root.set_vexpand(true);

        let top_bar = gtk::Box::new(gtk::Orientation::Horizontal, 8);
        top_bar.set_halign(gtk::Align::Center);
        top_bar.set_margin_top(10);
        let seg_box = gtk::Box::new(gtk::Orientation::Horizontal, 0);
        seg_box.add_css_class("linked");
        let btn_rules = gtk::ToggleButton::with_label("分流规则");
        let btn_override = gtk::ToggleButton::with_label("规则覆写");
        btn_override.set_group(Some(&btn_rules));
        btn_rules.set_active(true);
        seg_box.append(&btn_rules);
        seg_box.append(&btn_override);
        top_bar.append(&seg_box);

        let sub_stack = gtk::Stack::new();
        sub_stack.set_vexpand(true);

        let rules_scroll = gtk::ScrolledWindow::builder()
            .hscrollbar_policy(gtk::PolicyType::Never)
            .vexpand(true)
            .build();
        let rules_clamp = adw::Clamp::builder()
            .maximum_size(780)
            .margin_top(4)
            .margin_bottom(20)
            .margin_start(14)
            .margin_end(14)
            .build();
        let rules_outer = gtk::Box::new(gtk::Orientation::Vertical, 10);

        let filter_bar = gtk::Box::new(gtk::Orientation::Horizontal, 8);
        let rule_search = gtk::SearchEntry::new();
        rule_search.set_placeholder_text(Some("筛选规则类型、匹配内容或目标策略"));
        rule_search.set_hexpand(true);
        let rule_count_label = gtk::Label::new(None);
        rule_count_label.add_css_class("caption");
        rule_count_label.add_css_class("dim-label");
        let toggle_tester_btn = gtk::ToggleButton::with_label("匹配测试");
        let refresh_btn = gtk::Button::with_label("刷新");
        filter_bar.append(&rule_search);
        filter_bar.append(&rule_count_label);
        filter_bar.append(&toggle_tester_btn);
        filter_bar.append(&refresh_btn);

        let tester_group = adw::PreferencesGroup::builder().title("规则匹配测试").build();
        tester_group.set_visible(false);
        let target_row = adw::EntryRow::builder().title("域名或 IP").build();
        let net_combo = adw::ComboRow::builder()
            .title("传输协议")
            .model(&gtk::StringList::new(&["TCP", "UDP"]))
            .selected(0)
            .build();
        let run_test_btn = gtk::Button::with_label("匹配");
        run_test_btn.add_css_class("suggested-action");
        run_test_btn.set_valign(gtk::Align::Center);
        target_row.add_suffix(&run_test_btn);
        let result_row = adw::ActionRow::builder().title("匹配结果").subtitle("-").build();
        result_row.set_use_markup(false);
        tester_group.add(&target_row);
        tester_group.add(&net_combo);
        tester_group.add(&result_row);

        let rules_list_box = gtk::Box::new(gtk::Orientation::Vertical, 8);
        rules_outer.append(&filter_bar);
        rules_outer.append(&tester_group);
        rules_outer.append(&rules_list_box);
        rules_clamp.set_child(Some(&rules_outer));
        rules_scroll.set_child(Some(&rules_clamp));

        let override_scroll = gtk::ScrolledWindow::builder()
            .hscrollbar_policy(gtk::PolicyType::Never)
            .vexpand(true)
            .build();
        let override_clamp = adw::Clamp::builder()
            .maximum_size(780)
            .margin_top(4)
            .margin_bottom(20)
            .margin_start(14)
            .margin_end(14)
            .build();
        let override_outer = gtk::Box::new(gtk::Orientation::Vertical, 12);

        let quick_group = adw::PreferencesGroup::builder().title("快捷添加规则").build();
        let type_combo = adw::ComboRow::builder()
            .title("规则类型")
            .model(&gtk::StringList::new(&RULE_TYPES))
            .selected(0)
            .build();
        let match_row = adw::EntryRow::builder().title("匹配内容").build();
        let default_targets = ["DIRECT", "REJECT", "PROXY"];
        let target_combo = adw::ComboRow::builder()
            .title("目标策略")
            .model(&gtk::StringList::new(&default_targets))
            .selected(0)
            .build();
        let add_rule_btn = gtk::Button::with_label("添加并生效");
        add_rule_btn.add_css_class("suggested-action");
        add_rule_btn.set_valign(gtk::Align::Center);
        match_row.add_suffix(&add_rule_btn);
        quick_group.add(&type_combo);
        quick_group.add(&target_combo);
        quick_group.add(&match_row);

        let quick_feedback = gtk::Label::new(None);
        quick_feedback.add_css_class("caption");
        quick_feedback.set_xalign(0.0);
        quick_feedback.set_visible(false);

        let yaml_group = adw::PreferencesGroup::builder()
            .title("自定义 YAML 规则（留空使用当前订阅规则）")
            .build();
        let route_view = gtk::TextView::builder()
            .monospace(true)
            .wrap_mode(gtk::WrapMode::None)
            .top_margin(10)
            .bottom_margin(10)
            .left_margin(10)
            .right_margin(10)
            .build();
        let route_buffer = route_view.buffer();
        let route_scroll = gtk::ScrolledWindow::builder()
            .min_content_height(260)
            .child(&route_view)
            .build();
        route_scroll.add_css_class("card");

        let yaml_feedback = gtk::Label::new(None);
        yaml_feedback.add_css_class("caption");
        yaml_feedback.set_xalign(0.0);
        yaml_feedback.set_visible(false);

        let route_btns = gtk::Box::new(gtk::Orientation::Horizontal, 8);
        route_btns.set_halign(gtk::Align::End);
        let reset_route_btn = gtk::Button::with_label("恢复默认");
        let save_route_btn = gtk::Button::with_label("保存规则");
        save_route_btn.add_css_class("suggested-action");
        route_btns.append(&reset_route_btn);
        route_btns.append(&save_route_btn);

        yaml_group.add(&route_scroll);
        override_outer.append(&quick_group);
        override_outer.append(&quick_feedback);
        override_outer.append(&yaml_group);
        override_outer.append(&yaml_feedback);
        override_outer.append(&route_btns);
        override_clamp.set_child(Some(&override_outer));
        override_scroll.set_child(Some(&override_clamp));

        sub_stack.add_named(&rules_scroll, Some("rules"));
        sub_stack.add_named(&override_scroll, Some("override"));
        root.append(&top_bar);
        root.append(&sub_stack);

        let rule_filter = Rc::new(RefCell::new(String::new()));
        let override_dirty = Rc::new(Cell::new(false));
        {
            let dirty = override_dirty.clone();
            route_buffer.connect_changed(move |_| {
                dirty.set(true);
            });
        }

        let view = Rc::new(Self {
            root,
            rules_list_box,
            rule_count_label,
            target_combo: target_combo.clone(),
            target_options: RefCell::new(default_targets.iter().map(|s| (*s).to_string()).collect()),
            route_buffer: route_buffer.clone(),
            override_dirty: override_dirty.clone(),
            override_initialized: Cell::new(false),
            rule_filter,
            last_rule_sig: RefCell::new(String::new()),
            last_targets_sig: RefCell::new(String::new()),
            controller: controller.clone(),
            rt: rt.clone(),
        });

        {
            let st = sub_stack.clone();
            let v = view.clone();
            btn_rules.connect_toggled(move |b| {
                if b.is_active() {
                    st.set_visible_child_name("rules");
                    v.refresh_rules(true);
                }
            });
        }
        {
            let st = sub_stack;
            btn_override.connect_toggled(move |b| {
                if b.is_active() {
                    st.set_visible_child_name("override");
                }
            });
        }
        {
            let tg = tester_group;
            toggle_tester_btn.connect_toggled(move |b| {
                tg.set_visible(b.is_active());
            });
        }
        {
            let v = view.clone();
            rule_search.connect_search_changed(move |entry| {
                *v.rule_filter.borrow_mut() = entry.text().to_lowercase();
                v.refresh_rules(true);
            });
        }
        {
            let v = view.clone();
            refresh_btn.connect_clicked(move |_| {
                v.refresh_rules(true);
            });
        }
        {
            let ctrl = controller.clone();
            let handle = rt.clone();
            run_test_btn.connect_clicked(move |_| {
                let target = target_row.text().trim().to_string();
                if target.is_empty() {
                    return;
                }
                let net = if net_combo.selected() == 1 { "udp" } else { "tcp" };
                let ctrl = ctrl.clone();
                let (tx, rx) = std::sync::mpsc::channel();
                handle.spawn(async move {
                    let res = ctrl
                        .dispatch_json("test_route", json!({ "target": target, "network": net }))
                        .await
                        .map_err(|e| format!("{e:#}"));
                    let _ = tx.send(res);
                });
                let row_c = result_row.clone();
                gtk::glib::timeout_add_local(std::time::Duration::from_millis(80), move || {
                    match rx.try_recv() {
                        Ok(Ok(val)) => {
                            let dec = val.get("decision").and_then(|v| v.as_str()).unwrap_or("-");
                            let out = val
                                .get("outbound")
                                .and_then(|v| v.as_str())
                                .filter(|s| !s.is_empty())
                                .map(|o| format!("{dec} ({o})"))
                                .unwrap_or_else(|| dec.to_string());
                            let matched = val
                                .get("matched_rule")
                                .and_then(|v| v.as_str())
                                .unwrap_or("-");
                            row_c.set_subtitle(&format!("{out} · {matched}"));
                            gtk::glib::ControlFlow::Break
                        }
                        Ok(Err(err)) => {
                            row_c.set_subtitle(&err);
                            gtk::glib::ControlFlow::Break
                        }
                        Err(std::sync::mpsc::TryRecvError::Empty) => {
                            gtk::glib::ControlFlow::Continue
                        }
                        Err(_) => gtk::glib::ControlFlow::Break,
                    }
                });
            });
        }
        {
            let v = view.clone();
            let fb = quick_feedback;
            add_rule_btn.connect_clicked(move |_| {
                let payload = match_row.text().trim().to_string();
                if payload.is_empty() {
                    fb.add_css_class("error");
                    fb.set_text("请填写规则匹配内容");
                    fb.set_visible(true);
                    return;
                }
                let rtype = RULE_TYPES
                    .get(type_combo.selected() as usize)
                    .copied()
                    .unwrap_or("DOMAIN-SUFFIX");
                let target = v
                    .target_options
                    .borrow()
                    .get(target_combo.selected() as usize)
                    .cloned()
                    .unwrap_or_else(|| "DIRECT".to_string());
                let rule_line = format!("{rtype},{payload},{target}");
                let ctrl = v.controller.clone();
                let (tx, rx) = std::sync::mpsc::channel();
                v.rt.spawn(async move {
                    let res = ctrl
                        .dispatch_json("add_custom_rule", json!({ "rule": rule_line }))
                        .await
                        .map_err(|e| format!("{e:#}"));
                    let _ = tx.send(res);
                });
                let v_c = v.clone();
                let fb_c = fb.clone();
                let mr_c = match_row.clone();
                gtk::glib::timeout_add_local(std::time::Duration::from_millis(80), move || {
                    match rx.try_recv() {
                        Ok(Ok(_)) => {
                            mr_c.set_text("");
                            let snap = v_c.controller.snapshot();
                            v_c.route_buffer.set_text(&snap.settings.custom_route_yaml);
                            v_c.override_dirty.set(false);
                            fb_c.remove_css_class("error");
                            fb_c.set_text("规则已添加并生效");
                            fb_c.set_visible(true);
                            v_c.refresh_rules(true);
                            gtk::glib::ControlFlow::Break
                        }
                        Ok(Err(err)) => {
                            fb_c.add_css_class("error");
                            fb_c.set_text(&err);
                            fb_c.set_visible(true);
                            gtk::glib::ControlFlow::Break
                        }
                        Err(std::sync::mpsc::TryRecvError::Empty) => {
                            gtk::glib::ControlFlow::Continue
                        }
                        Err(_) => gtk::glib::ControlFlow::Break,
                    }
                });
            });
        }
        {
            let v = view.clone();
            let fb = yaml_feedback.clone();
            save_route_btn.connect_clicked(move |_| {
                let yaml = v
                    .route_buffer
                    .text(
                        &v.route_buffer.start_iter(),
                        &v.route_buffer.end_iter(),
                        false,
                    )
                    .to_string();
                match v.controller.update_settings(&json!({ "custom_route_yaml": yaml })) {
                    Ok(_) => {
                        v.override_dirty.set(false);
                        fb.remove_css_class("error");
                        fb.set_text(if yaml.trim().is_empty() {
                            "已恢复订阅默认规则"
                        } else {
                            "规则覆写已保存并生效"
                        });
                        fb.set_visible(true);
                        if v.controller.snapshot().connection.connected {
                            let ctrl = v.controller.clone();
                            v.rt.spawn(async move {
                                let _ = ctrl.connect().await;
                            });
                        }
                        v.refresh_rules(true);
                    }
                    Err(err) => {
                        fb.add_css_class("error");
                        fb.set_text(&format!("{err:#}"));
                        fb.set_visible(true);
                    }
                }
            });
        }
        {
            let v = view.clone();
            let fb = yaml_feedback;
            reset_route_btn.connect_clicked(move |_| {
                v.route_buffer.set_text("");
                v.override_dirty.set(false);
                let _ = v.controller.update_settings(&json!({ "custom_route_yaml": "" }));
                fb.remove_css_class("error");
                fb.set_text("已恢复订阅默认规则");
                fb.set_visible(true);
                if v.controller.snapshot().connection.connected {
                    let ctrl = v.controller.clone();
                    v.rt.spawn(async move {
                        let _ = ctrl.connect().await;
                    });
                }
                v.refresh_rules(true);
            });
        }

        view.refresh_rules(true);
        view
    }

    pub fn apply_snapshot(&self, snap: &EngineSnapshot) {
        if !self.override_initialized.get() || !self.override_dirty.get() {
            let cur = self
                .route_buffer
                .text(
                    &self.route_buffer.start_iter(),
                    &self.route_buffer.end_iter(),
                    false,
                )
                .to_string();
            if cur != snap.settings.custom_route_yaml {
                self.route_buffer.set_text(&snap.settings.custom_route_yaml);
                self.override_dirty.set(false);
            }
            self.override_initialized.set(true);
        }

        let mut opts = vec![
            "DIRECT".to_string(),
            "REJECT".to_string(),
            "PROXY".to_string(),
        ];
        for g in &snap.proxy_groups {
            if !g.name.is_empty() && !opts.contains(&g.name) {
                opts.push(g.name.clone());
            }
        }
        for n in snap.nodes.iter().take(30) {
            if !n.name.is_empty() && !opts.contains(&n.name) {
                opts.push(n.name.clone());
            }
        }
        let t_sig = opts.join("\x1f");
        if *self.last_targets_sig.borrow() != t_sig {
            *self.last_targets_sig.borrow_mut() = t_sig;
            let prev_sel = self
                .target_options
                .borrow()
                .get(self.target_combo.selected() as usize)
                .cloned();
            let refs: Vec<&str> = opts.iter().map(String::as_str).collect();
            self.target_combo
                .set_model(Some(&gtk::StringList::new(&refs)));
            let next_idx = prev_sel
                .and_then(|p| opts.iter().position(|o| o == &p))
                .unwrap_or(0) as u32;
            self.target_combo.set_selected(next_idx);
            *self.target_options.borrow_mut() = opts;
        }

        self.refresh_rules(false);
    }

    pub fn refresh_rules(&self, force: bool) {
        let yaml = self.controller.active_route_yaml();
        let filter = self.rule_filter.borrow().clone();
        let sig = format!("{}|{}|{}", yaml.len(), filter, &yaml[..yaml.len().min(64)]);
        if !force && *self.last_rule_sig.borrow() == sig {
            return;
        }
        *self.last_rule_sig.borrow_mut() = sig;

        while let Some(c) = self.rules_list_box.first_child() {
            self.rules_list_box.remove(&c);
        }

        let (total, items) = parse_rule_items(&yaml, &filter, 300).unwrap_or_default();
        self.rule_count_label.set_text(&if filter.is_empty() {
            format!("共 {total} 条")
        } else {
            format!("{} / {total} 条", items.len())
        });

        let group = adw::PreferencesGroup::new();
        if items.is_empty() {
            group.add(
                &adw::ActionRow::builder()
                    .title(if total == 0 {
                        "暂无分流规则"
                    } else {
                        "无匹配规则"
                    })
                    .build(),
            );
        } else {
            for r in items {
                let payload = if r.payload.is_empty() {
                    "全匹配"
                } else {
                    &r.payload
                };
                let row = adw::ActionRow::builder()
                    .title(&format!("[{}] {payload}", r.rule_type))
                    .subtitle(&r.target)
                    .build();
                row.set_use_markup(false);
                group.add(&row);
            }
        }
        self.rules_list_box.append(&group);
    }
}
