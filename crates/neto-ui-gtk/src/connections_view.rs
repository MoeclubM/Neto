#![cfg(target_os = "linux")]

use crate::format::format_bytes;
use adw::prelude::*;
use gtk::prelude::*;
use neto_core::{
    EngineSnapshot, NetoController, close_all_connections, close_connection, parse_rule_items,
    snapshot_connections,
};
use serde_json::json;
use std::cell::{Cell, RefCell};
use std::rc::Rc;
use std::sync::Arc;
use tokio::runtime::Handle;

pub struct ConnectionsView {
    pub root: gtk::Box,
    sub_stack: gtk::Stack,
    conn_list_box: gtk::Box,
    rules_list_box: gtk::Box,
    conn_filter: Rc<RefCell<String>>,
    rule_filter: Rc<RefCell<String>>,
    paused: Rc<Cell<bool>>,
    last_conn_sig: RefCell<String>,
    last_rule_sig: RefCell<String>,
    controller: Arc<NetoController>,
    _rt: Handle,
}

impl ConnectionsView {
    pub fn new(controller: Arc<NetoController>, rt: Handle) -> Rc<Self> {
        let root = gtk::Box::new(gtk::Orientation::Vertical, 8);
        root.set_vexpand(true);

        let top_bar = gtk::Box::new(gtk::Orientation::Horizontal, 8);
        top_bar.set_halign(gtk::Align::Center);
        top_bar.set_margin_top(10);
        let seg_box = gtk::Box::new(gtk::Orientation::Horizontal, 0);
        seg_box.add_css_class("linked");
        let btn_active = gtk::ToggleButton::with_label("活跃会话");
        let btn_rules = gtk::ToggleButton::with_label("分流规则");
        btn_rules.set_group(Some(&btn_active));
        btn_active.set_active(true);
        seg_box.append(&btn_active);
        seg_box.append(&btn_rules);
        top_bar.append(&seg_box);

        let sub_stack = gtk::Stack::new();
        sub_stack.set_vexpand(true);

        let active_page = gtk::Box::new(gtk::Orientation::Vertical, 8);
        let conn_bar = gtk::Box::new(gtk::Orientation::Horizontal, 8);
        conn_bar.set_margin_start(14);
        conn_bar.set_margin_end(14);
        let conn_search = gtk::SearchEntry::new();
        conn_search.set_placeholder_text(Some("筛选主机、规则或出站"));
        conn_search.set_hexpand(true);
        let pause_btn = gtk::ToggleButton::with_label("暂停");
        let close_all_btn = gtk::Button::with_label("断开全部");
        close_all_btn.add_css_class("destructive-action");
        conn_bar.append(&conn_search);
        conn_bar.append(&pause_btn);
        conn_bar.append(&close_all_btn);

        let conn_scroll = gtk::ScrolledWindow::builder()
            .hscrollbar_policy(gtk::PolicyType::Never)
            .vexpand(true)
            .build();
        let conn_clamp = adw::Clamp::builder()
            .maximum_size(760)
            .margin_top(4)
            .margin_bottom(20)
            .margin_start(14)
            .margin_end(14)
            .build();
        let conn_list_box = gtk::Box::new(gtk::Orientation::Vertical, 8);
        conn_clamp.set_child(Some(&conn_list_box));
        conn_scroll.set_child(Some(&conn_clamp));
        active_page.append(&conn_bar);
        active_page.append(&conn_scroll);

        let rules_scroll = gtk::ScrolledWindow::builder()
            .hscrollbar_policy(gtk::PolicyType::Never)
            .vexpand(true)
            .build();
        let rules_clamp = adw::Clamp::builder()
            .maximum_size(760)
            .margin_top(4)
            .margin_bottom(20)
            .margin_start(14)
            .margin_end(14)
            .build();
        let rules_outer = gtk::Box::new(gtk::Orientation::Vertical, 10);

        let tester_group = adw::PreferencesGroup::builder().title("规则匹配测试").build();
        let target_row = adw::EntryRow::builder().title("域名或 IP").build();
        let net_combo = adw::ComboRow::builder()
            .title("传输协议")
            .model(&gtk::StringList::new(&["TCP", "UDP"]))
            .selected(0)
            .build();
        let run_test_btn = gtk::Button::with_label("匹配测试");
        run_test_btn.add_css_class("suggested-action");
        run_test_btn.set_valign(gtk::Align::Center);
        target_row.add_suffix(&run_test_btn);
        let result_row = adw::ActionRow::builder().title("匹配结果").subtitle("-").build();
        tester_group.add(&target_row);
        tester_group.add(&net_combo);
        tester_group.add(&result_row);

        let rule_search = gtk::SearchEntry::new();
        rule_search.set_placeholder_text(Some("筛选规则类型、内容或目标策略"));
        let rules_list_box = gtk::Box::new(gtk::Orientation::Vertical, 8);
        rules_outer.append(&tester_group);
        rules_outer.append(&rule_search);
        rules_outer.append(&rules_list_box);
        rules_clamp.set_child(Some(&rules_outer));
        rules_scroll.set_child(Some(&rules_clamp));

        sub_stack.add_named(&active_page, Some("active"));
        sub_stack.add_named(&rules_scroll, Some("rules"));
        root.append(&top_bar);
        root.append(&sub_stack);

        let conn_filter = Rc::new(RefCell::new(String::new()));
        let rule_filter = Rc::new(RefCell::new(String::new()));
        let paused = Rc::new(Cell::new(false));

        let view = Rc::new(Self {
            root,
            sub_stack: sub_stack.clone(),
            conn_list_box,
            rules_list_box,
            conn_filter: conn_filter.clone(),
            rule_filter: rule_filter.clone(),
            paused: paused.clone(),
            last_conn_sig: RefCell::new(String::new()),
            last_rule_sig: RefCell::new(String::new()),
            controller: controller.clone(),
            _rt: rt.clone(),
        });

        {
            let st = sub_stack.clone();
            let v = view.clone();
            btn_active.connect_toggled(move |b| {
                if b.is_active() {
                    st.set_visible_child_name("active");
                    v.refresh_connections(true);
                }
            });
        }
        {
            let st = sub_stack;
            let v = view.clone();
            btn_rules.connect_toggled(move |b| {
                if b.is_active() {
                    st.set_visible_child_name("rules");
                    v.refresh_rules(true);
                }
            });
        }
        {
            let p = paused;
            pause_btn.connect_toggled(move |b| {
                p.set(b.is_active());
                b.set_label(if b.is_active() { "继续" } else { "暂停" });
            });
        }
        {
            let v = view.clone();
            close_all_btn.connect_clicked(move |_| {
                let _ = close_all_connections();
                v.refresh_connections(true);
            });
        }
        {
            let v = view.clone();
            conn_search.connect_search_changed(move |entry| {
                *v.conn_filter.borrow_mut() = entry.text().to_lowercase();
                v.refresh_connections(true);
            });
        }
        {
            let v = view.clone();
            rule_search.connect_search_changed(move |entry| {
                *v.rule_filter.borrow_mut() = entry.text().to_string();
                v.refresh_rules(true);
            });
        }
        {
            let ctrl = controller;
            let handle = rt;
            let trow = target_row;
            let ncombo = net_combo;
            let rrow = result_row;
            run_test_btn.connect_clicked(move |_| {
                let target = trow.text().trim().to_string();
                if target.is_empty() {
                    return;
                }
                let net = if ncombo.selected() == 1 { "udp" } else { "tcp" };
                let ctrl = ctrl.clone();
                let (tx, rx) = std::sync::mpsc::channel();
                handle.spawn(async move {
                    let out = ctrl
                        .dispatch_json("test_route", json!({ "target": target, "network": net }))
                        .await
                        .map(|val| {
                            let dec = val.get("decision").and_then(|x| x.as_str()).unwrap_or("-");
                            let ob = val.get("outbound").and_then(|x| x.as_str()).unwrap_or("");
                            let rule = val.get("matched_rule").and_then(|x| x.as_str()).unwrap_or("-");
                            if ob.is_empty() {
                                format!("{dec} · {rule}")
                            } else {
                                format!("{dec} ({ob}) · {rule}")
                            }
                        })
                        .unwrap_or_else(|e| format!("{e:#}"));
                    let _ = tx.send(out);
                });
                let rrow_c = rrow.clone();
                gtk::glib::timeout_add_local(std::time::Duration::from_millis(80), move || {
                    if let Ok(msg) = rx.try_recv() {
                        rrow_c.set_subtitle(&msg);
                        return gtk::glib::ControlFlow::Break;
                    }
                    gtk::glib::ControlFlow::Continue
                });
            });
        }

        view
    }

    pub fn apply_snapshot(&self, snap: &EngineSnapshot) {
        let vis = self.sub_stack.visible_child_name();
        if vis.as_deref() == Some("rules") {
            let rule_sig = format!(
                "{:?}:{}:{}:{}",
                snap.active_profile_id,
                snap.rule_count,
                snap.settings.custom_route_yaml.len(),
                self.rule_filter.borrow()
            );
            if *self.last_rule_sig.borrow() != rule_sig {
                self.refresh_rules(true);
            }
        } else if !self.paused.get() {
            self.refresh_connections(false);
        }
    }

    fn refresh_connections(&self, force: bool) {
        let csnap = snapshot_connections();
        let q = self.conn_filter.borrow().clone();
        let filtered: Vec<_> = csnap
            .active
            .iter()
            .filter(|c| {
                q.is_empty()
                    || c.host.to_lowercase().contains(&q)
                    || c.rule.to_lowercase().contains(&q)
                    || c.outbound.to_lowercase().contains(&q)
            })
            .collect();

        let sig = format!(
            "{q}:{}",
            filtered
                .iter()
                .map(|c| format!("{}:{}:{}", c.id, c.upload_bytes / 1024, c.download_bytes / 1024))
                .collect::<Vec<_>>()
                .join(",")
        );
        if !force && *self.last_conn_sig.borrow() == sig {
            return;
        }
        *self.last_conn_sig.borrow_mut() = sig;

        while let Some(c) = self.conn_list_box.first_child() {
            self.conn_list_box.remove(&c);
        }

        let group = adw::PreferencesGroup::builder()
            .title("活跃连接")
            .description(&format!(
                "并发 {} · ↑ {} · ↓ {}",
                csnap.active.len(),
                format_bytes(csnap.total_upload_bytes),
                format_bytes(csnap.total_download_bytes)
            ))
            .build();

        if filtered.is_empty() {
            let empty = adw::ActionRow::builder()
                .title(if csnap.active.is_empty() { "暂无活跃会话" } else { "无匹配会话" })
                .build();
            group.add(&empty);
        } else {
            for item in filtered.into_iter().rev().take(150) {
                let sub = format!(
                    "{} · {} · {} · ↑ {} ↓ {}",
                    item.network.to_uppercase(),
                    if item.outbound.is_empty() { "-" } else { &item.outbound },
                    if item.rule.is_empty() { "-" } else { &item.rule },
                    format_bytes(item.upload_bytes),
                    format_bytes(item.download_bytes)
                );
                let row = adw::ActionRow::builder()
                    .title(&item.host)
                    .subtitle(&sub)
                    .build();
                let close_btn = gtk::Button::with_label("断开");
                close_btn.add_css_class("flat");
                close_btn.set_valign(gtk::Align::Center);
                let cid = item.id;
                close_btn.connect_clicked(move |btn| {
                    let _ = close_connection(cid);
                    btn.set_sensitive(false);
                });
                row.add_suffix(&close_btn);
                group.add(&row);
            }
        }
        self.conn_list_box.append(&group);
    }

    fn refresh_rules(&self, force: bool) {
        let snap = self.controller.snapshot();
        let filter = self.rule_filter.borrow().clone();
        let sig = format!(
            "{:?}:{}:{}:{filter}",
            snap.active_profile_id,
            snap.rule_count,
            snap.settings.custom_route_yaml.len()
        );
        if !force && *self.last_rule_sig.borrow() == sig {
            return;
        }
        *self.last_rule_sig.borrow_mut() = sig;

        while let Some(c) = self.rules_list_box.first_child() {
            self.rules_list_box.remove(&c);
        }

        let yaml = self.controller.active_route_yaml();
        let (total, items) = parse_rule_items(&yaml, &filter, 250).unwrap_or_default();
        let group = adw::PreferencesGroup::builder()
            .title("分流规则列表")
            .description(&if filter.is_empty() {
                format!("共 {total} 条")
            } else {
                format!("{} / {total} 条", items.len())
            })
            .build();
        if items.is_empty() {
            group.add(&adw::ActionRow::builder().title(if total == 0 { "暂无分流规则" } else { "无匹配规则" }).build());
        } else {
            for r in items {
                let payload = if r.payload.is_empty() { "全匹配" } else { &r.payload };
                let row = adw::ActionRow::builder()
                    .title(&format!("[{}] {payload}", r.rule_type))
                    .subtitle(&r.target)
                    .build();
                group.add(&row);
            }
        }
        self.rules_list_box.append(&group);
    }
}
