#![cfg(target_os = "linux")]

use crate::format::{format_bytes, format_duration_ms, format_speed};
use adw::prelude::*;
use gtk::prelude::*;
use neto_core::{
    ActiveConnectionEntry, EngineSnapshot, NetoController, clear_closed_connections,
    close_all_connections, close_connection, snapshot_connections, telemetry,
};
use std::cell::{Cell, RefCell};
use std::rc::Rc;
use std::sync::Arc;
use tokio::runtime::Handle;

pub struct ConnectionsView {
    pub root: gtk::Box,
    btn_active: gtk::ToggleButton,
    btn_closed: gtk::ToggleButton,
    action_all_btn: gtk::Button,
    summary_label: gtk::Label,
    conn_list_box: gtk::Box,
    sub_tab: Rc<RefCell<String>>,
    conn_filter: Rc<RefCell<String>>,
    net_filter: Rc<RefCell<String>>,
    sort_idx: Rc<Cell<u32>>,
    paused: Rc<Cell<bool>>,
    last_conn_sig: RefCell<String>,
    _controller: Arc<NetoController>,
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
        let btn_closed = gtk::ToggleButton::with_label("已关闭");
        btn_closed.set_group(Some(&btn_active));
        btn_active.set_active(true);
        seg_box.append(&btn_active);
        seg_box.append(&btn_closed);
        top_bar.append(&seg_box);

        let toolbar = gtk::Box::new(gtk::Orientation::Horizontal, 8);
        toolbar.set_margin_start(14);
        toolbar.set_margin_end(14);
        let conn_search = gtk::SearchEntry::new();
        conn_search.set_placeholder_text(Some("筛选主机、规则或出站"));
        conn_search.set_hexpand(true);
        let net_drop = gtk::DropDown::from_strings(&["全部协议", "TCP", "UDP"]);
        let sort_drop =
            gtk::DropDown::from_strings(&["最新", "下载速率", "上传速率", "总流量", "域名"]);
        let pause_btn = gtk::ToggleButton::with_label("暂停");
        let action_all_btn = gtk::Button::with_label("断开全部");
        action_all_btn.add_css_class("destructive-action");
        toolbar.append(&conn_search);
        toolbar.append(&net_drop);
        toolbar.append(&sort_drop);
        toolbar.append(&pause_btn);
        toolbar.append(&action_all_btn);

        let summary_label = gtk::Label::new(None);
        summary_label.add_css_class("caption");
        summary_label.add_css_class("dim-label");
        summary_label.add_css_class("monospace");
        summary_label.set_xalign(0.0);
        summary_label.set_margin_start(16);
        summary_label.set_margin_end(16);

        let conn_scroll = gtk::ScrolledWindow::builder()
            .hscrollbar_policy(gtk::PolicyType::Never)
            .vexpand(true)
            .build();
        let conn_clamp = adw::Clamp::builder()
            .maximum_size(780)
            .margin_top(2)
            .margin_bottom(20)
            .margin_start(14)
            .margin_end(14)
            .build();
        let conn_list_box = gtk::Box::new(gtk::Orientation::Vertical, 8);
        conn_clamp.set_child(Some(&conn_list_box));
        conn_scroll.set_child(Some(&conn_clamp));

        root.append(&top_bar);
        root.append(&toolbar);
        root.append(&summary_label);
        root.append(&conn_scroll);

        let sub_tab = Rc::new(RefCell::new("active".to_string()));
        let conn_filter = Rc::new(RefCell::new(String::new()));
        let net_filter = Rc::new(RefCell::new("all".to_string()));
        let sort_idx = Rc::new(Cell::new(0u32));
        let paused = Rc::new(Cell::new(false));

        let view = Rc::new(Self {
            root,
            btn_active: btn_active.clone(),
            btn_closed: btn_closed.clone(),
            action_all_btn: action_all_btn.clone(),
            summary_label,
            conn_list_box,
            sub_tab,
            conn_filter,
            net_filter,
            sort_idx,
            paused: paused.clone(),
            last_conn_sig: RefCell::new(String::new()),
            _controller: controller,
            _rt: rt,
        });

        {
            let v = view.clone();
            btn_active.connect_toggled(move |b| {
                if b.is_active() {
                    *v.sub_tab.borrow_mut() = "active".to_string();
                    v.action_all_btn.set_label("断开全部");
                    v.refresh_connections(true);
                }
            });
        }
        {
            let v = view.clone();
            btn_closed.connect_toggled(move |b| {
                if b.is_active() {
                    *v.sub_tab.borrow_mut() = "closed".to_string();
                    v.action_all_btn.set_label("清空记录");
                    v.refresh_connections(true);
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
            action_all_btn.connect_clicked(move |_| {
                if v.sub_tab.borrow().as_str() == "closed" {
                    clear_closed_connections();
                } else {
                    let _ = close_all_connections();
                }
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
            net_drop.connect_selected_notify(move |dd| {
                let val = match dd.selected() {
                    1 => "tcp",
                    2 => "udp",
                    _ => "all",
                };
                *v.net_filter.borrow_mut() = val.to_string();
                v.refresh_connections(true);
            });
        }
        {
            let v = view.clone();
            sort_drop.connect_selected_notify(move |dd| {
                v.sort_idx.set(dd.selected());
                v.refresh_connections(true);
            });
        }

        view.refresh_connections(true);
        view
    }

    pub fn apply_snapshot(self: &Rc<Self>, _snap: &EngineSnapshot) {
        if !self.paused.get() {
            self.refresh_connections(false);
        }
    }

    pub fn refresh_connections(self: &Rc<Self>, force: bool) {
        let snap = snapshot_connections();
        let is_closed = self.sub_tab.borrow().as_str() == "closed";
        let filter = self.conn_filter.borrow().clone();
        let net = self.net_filter.borrow().clone();
        let sort_mode = self.sort_idx.get();
        let now_ms = telemetry::now_ms();

        self.btn_active.set_label(&if snap.active.is_empty() {
            "活跃会话".to_string()
        } else {
            format!("活跃会话 ({})", snap.active.len())
        });
        self.btn_closed.set_label(&if snap.closed.is_empty() {
            "已关闭".to_string()
        } else {
            format!("已关闭 ({})", snap.closed.len())
        });

        let source = if is_closed { &snap.closed } else { &snap.active };
        self.action_all_btn.set_sensitive(!source.is_empty());
        self.summary_label.set_text(&if is_closed {
            format!(
                "已关闭 {} 条 · 累计 ↑ {} ↓ {}",
                snap.closed.len(),
                format_bytes(snap.total_upload_bytes),
                format_bytes(snap.total_download_bytes),
            )
        } else {
            format!(
                "活跃 {} · ↑ {} ↓ {} · 累计 ↑ {} ↓ {}",
                snap.active.len(),
                format_speed(snap.total_upload_speed_bps),
                format_speed(snap.total_download_speed_bps),
                format_bytes(snap.total_upload_bytes),
                format_bytes(snap.total_download_bytes),
            )
        });

        let mut filtered: Vec<ActiveConnectionEntry> = source
            .iter()
            .filter(|c| {
                if net != "all" && !c.network.eq_ignore_ascii_case(&net) {
                    return false;
                }
                if !filter.is_empty() {
                    let h = c.host.to_lowercase();
                    let r = c.rule.to_lowercase();
                    let o = c.outbound.to_lowercase();
                    if !h.contains(&filter) && !r.contains(&filter) && !o.contains(&filter) {
                        return false;
                    }
                }
                true
            })
            .cloned()
            .collect();

        match sort_mode {
            1 => filtered.sort_by(|a, b| {
                b.download_speed_bps
                    .cmp(&a.download_speed_bps)
                    .then_with(|| b.download_bytes.cmp(&a.download_bytes))
            }),
            2 => filtered.sort_by(|a, b| {
                b.upload_speed_bps
                    .cmp(&a.upload_speed_bps)
                    .then_with(|| b.upload_bytes.cmp(&a.upload_bytes))
            }),
            3 => filtered.sort_by(|a, b| {
                (b.upload_bytes + b.download_bytes).cmp(&(a.upload_bytes + a.download_bytes))
            }),
            4 => filtered.sort_by(|a, b| a.host.to_lowercase().cmp(&b.host.to_lowercase())),
            _ => {
                if is_closed {
                    filtered.sort_by(|a, b| {
                        b.closed_at_ms
                            .cmp(&a.closed_at_ms)
                            .then_with(|| b.id.cmp(&a.id))
                    });
                } else {
                    filtered.sort_by(|a, b| {
                        b.started_at_ms
                            .cmp(&a.started_at_ms)
                            .then_with(|| b.id.cmp(&a.id))
                    });
                }
            }
        }

        let sig_body = filtered
            .iter()
            .take(180)
            .map(|c| {
                let t = if is_closed {
                    c.closed_at_ms
                } else {
                    now_ms.saturating_sub(c.started_at_ms) / 1000
                };
                format!(
                    "{}:{}:{}:{}:{}:{t}",
                    c.id,
                    c.upload_bytes / 1024,
                    c.download_bytes / 1024,
                    c.upload_speed_bps / 512,
                    c.download_speed_bps / 512,
                )
            })
            .collect::<Vec<_>>()
            .join(";");
        let sig = format!(
            "{is_closed}|{net}|{filter}|{sort_mode}|{}|{}|{sig_body}",
            snap.active.len(),
            snap.closed.len()
        );
        if !force && *self.last_conn_sig.borrow() == sig {
            return;
        }
        *self.last_conn_sig.borrow_mut() = sig;

        while let Some(c) = self.conn_list_box.first_child() {
            self.conn_list_box.remove(&c);
        }

        let group = adw::PreferencesGroup::new();
        if filtered.is_empty() {
            let empty_title = if source.is_empty() {
                if is_closed {
                    "暂无已关闭会话"
                } else {
                    "暂无活跃会话"
                }
            } else {
                "无匹配会话"
            };
            group.add(&adw::ActionRow::builder().title(empty_title).build());
            self.conn_list_box.append(&group);
            return;
        }

        for item in filtered.into_iter().take(180) {
            let sub = format!(
                "[{}] {} · {}",
                item.network.to_uppercase(),
                if item.outbound.is_empty() {
                    "-"
                } else {
                    &item.outbound
                },
                if item.rule.is_empty() {
                    "-"
                } else {
                    &item.rule
                },
            );
            let row = adw::ActionRow::builder()
                .title(&item.host)
                .subtitle(&sub)
                .activatable(true)
                .build();
            row.set_use_markup(false);

            let dur_ms = if is_closed {
                item.closed_at_ms.saturating_sub(item.started_at_ms)
            } else {
                now_ms.saturating_sub(item.started_at_ms)
            };
            let stats_box = gtk::Box::new(gtk::Orientation::Vertical, 2);
            stats_box.set_valign(gtk::Align::Center);
            let top_stat = gtk::Label::new(Some(&if is_closed {
                format!(
                    "↑ {} · ↓ {}",
                    format_bytes(item.upload_bytes),
                    format_bytes(item.download_bytes)
                )
            } else {
                format!(
                    "↑ {} · ↓ {}",
                    format_speed(item.upload_speed_bps),
                    format_speed(item.download_speed_bps)
                )
            }));
            top_stat.add_css_class("caption");
            top_stat.add_css_class("monospace");
            top_stat.set_xalign(1.0);

            let bot_stat = gtk::Label::new(Some(&if is_closed {
                format_duration_ms(dur_ms)
            } else {
                format!(
                    "↑ {} ↓ {} · {}",
                    format_bytes(item.upload_bytes),
                    format_bytes(item.download_bytes),
                    format_duration_ms(dur_ms)
                )
            }));
            bot_stat.add_css_class("caption");
            bot_stat.add_css_class("dim-label");
            bot_stat.add_css_class("monospace");
            bot_stat.set_xalign(1.0);
            stats_box.append(&top_stat);
            stats_box.append(&bot_stat);
            row.add_suffix(&stats_box);

            if !is_closed {
                let cid = item.id;
                let close_btn = gtk::Button::with_label("断开");
                close_btn.add_css_class("flat");
                close_btn.set_valign(gtk::Align::Center);
                let v = self.clone();
                close_btn.connect_clicked(move |_| {
                    let _ = close_connection(cid);
                    v.refresh_connections(true);
                });
                row.add_suffix(&close_btn);
            }

            {
                let item_snap = item.clone();
                let v = self.clone();
                row.connect_activated(move |r| {
                    show_connection_detail_dialog(r, &item_snap, is_closed, &v);
                });
            }
            group.add(&row);
        }
        self.conn_list_box.append(&group);
    }
}

fn show_connection_detail_dialog(
    anchor: &adw::ActionRow,
    item: &ActiveConnectionEntry,
    is_closed: bool,
    view: &Rc<ConnectionsView>,
) {
    let now_ms = telemetry::now_ms();
    let dur_ms = if is_closed {
        item.closed_at_ms.saturating_sub(item.started_at_ms)
    } else {
        now_ms.saturating_sub(item.started_at_ms)
    };
    let box_list = gtk::Box::new(gtk::Orientation::Vertical, 8);
    box_list.set_margin_top(6);
    box_list.set_margin_bottom(6);

    let group = adw::PreferencesGroup::new();
    for (k, val) in [
        ("目标主机", item.host.clone()),
        ("传输协议", item.network.to_uppercase()),
        (
            "出站策略",
            if item.outbound.is_empty() {
                "-".to_string()
            } else {
                item.outbound.clone()
            },
        ),
        (
            "命中规则",
            if item.rule.is_empty() {
                "-".to_string()
            } else {
                item.rule.clone()
            },
        ),
        (
            "会话状态",
            if is_closed { "已关闭" } else { "活跃" }.to_string(),
        ),
        (
            "累计流量",
            format!(
                "↑ {} · ↓ {}",
                format_bytes(item.upload_bytes),
                format_bytes(item.download_bytes)
            ),
        ),
        ("持续时长", format_duration_ms(dur_ms)),
    ] {
        let r = adw::ActionRow::builder().title(k).subtitle(&val).build();
        r.set_use_markup(false);
        group.add(&r);
    }
    box_list.append(&group);

    let actions = gtk::Box::new(gtk::Orientation::Horizontal, 8);
    actions.set_halign(gtk::Align::End);
    let copy_btn = gtk::Button::with_label("复制主机");
    let host_copy = item.host.clone();
    copy_btn.connect_clicked(move |_| {
        if let Some(display) = gtk::gdk::Display::default() {
            display.clipboard().set_text(&host_copy);
        }
    });
    actions.append(&copy_btn);

    let dialog = adw::AlertDialog::builder().heading("会话详情").build();
    if !is_closed {
        let disc_btn = gtk::Button::with_label("断开连接");
        disc_btn.add_css_class("destructive-action");
        let cid = item.id;
        let v = view.clone();
        let dlg = dialog.clone();
        disc_btn.connect_clicked(move |_| {
            let _ = close_connection(cid);
            v.refresh_connections(true);
            dlg.close();
        });
        actions.append(&disc_btn);
    }
    box_list.append(&actions);

    dialog.set_extra_child(Some(&box_list));
    dialog.add_response("close", "关闭");
    dialog.present(anchor.root().as_ref());
}
