#![cfg(target_os = "linux")]

use adw::prelude::*;
use gtk::prelude::*;
use neto_core::{EngineSnapshot, NetoController, NodeSortMode};
use std::cell::RefCell;
use std::rc::Rc;
use std::sync::Arc;
use tokio::runtime::Handle;

pub struct NodesView {
    pub root: gtk::Box,
    search_entry: gtk::SearchEntry,
    test_all_btn: gtk::Button,
    list_box: gtk::Box,
    filter_text: Rc<RefCell<String>>,
    last_signature: RefCell<String>,
    controller: Arc<NetoController>,
    rt: Handle,
}

impl NodesView {
    pub fn new(controller: Arc<NetoController>, rt: Handle) -> Rc<Self> {
        let root = gtk::Box::new(gtk::Orientation::Vertical, 8);
        root.set_vexpand(true);

        let bar = gtk::Box::new(gtk::Orientation::Horizontal, 8);
        bar.set_margin_top(10);
        bar.set_margin_start(14);
        bar.set_margin_end(14);

        let search_entry = gtk::SearchEntry::new();
        search_entry.set_placeholder_text(Some("筛选节点"));
        search_entry.set_hexpand(true);

        let sort_box = gtk::Box::new(gtk::Orientation::Horizontal, 0);
        sort_box.add_css_class("linked");
        let s_def = gtk::ToggleButton::with_label("默认");
        let s_lat = gtk::ToggleButton::with_label("延迟");
        let s_name = gtk::ToggleButton::with_label("名称");
        s_lat.set_group(Some(&s_def));
        s_name.set_group(Some(&s_def));
        s_def.set_active(true);
        sort_box.append(&s_def);
        sort_box.append(&s_lat);
        sort_box.append(&s_name);

        let test_all_btn = gtk::Button::with_label("测速");
        test_all_btn.add_css_class("suggested-action");

        bar.append(&search_entry);
        bar.append(&sort_box);
        bar.append(&test_all_btn);

        let scroll = gtk::ScrolledWindow::builder()
            .hscrollbar_policy(gtk::PolicyType::Never)
            .vexpand(true)
            .build();
        let clamp = adw::Clamp::builder()
            .maximum_size(760)
            .margin_top(6)
            .margin_bottom(20)
            .margin_start(14)
            .margin_end(14)
            .build();
        let list_box = gtk::Box::new(gtk::Orientation::Vertical, 10);
        clamp.set_child(Some(&list_box));
        scroll.set_child(Some(&clamp));

        root.append(&bar);
        root.append(&scroll);

        let filter_text = Rc::new(RefCell::new(String::new()));
        let view = Rc::new(Self {
            root,
            search_entry: search_entry.clone(),
            test_all_btn: test_all_btn.clone(),
            list_box,
            filter_text: filter_text.clone(),
            last_signature: RefCell::new(String::new()),
            controller: controller.clone(),
            rt: rt.clone(),
        });

        {
            let v = view.clone();
            search_entry.connect_search_changed(move |entry| {
                *v.filter_text.borrow_mut() = entry.text().to_lowercase();
                *v.last_signature.borrow_mut() = String::new();
                v.apply_snapshot(&v.controller.snapshot());
            });
        }
        for (btn, mode) in [
            (&s_def, NodeSortMode::Default),
            (&s_lat, NodeSortMode::Latency),
            (&s_name, NodeSortMode::Name),
        ] {
            let ctrl = controller.clone();
            btn.connect_toggled(move |b| {
                if b.is_active() {
                    let _ = ctrl.set_node_sort(mode);
                }
            });
        }
        {
            let ctrl = controller;
            let handle = rt;
            test_all_btn.connect_clicked(move |_| {
                let ctrl = ctrl.clone();
                handle.spawn(async move {
                    let _ = ctrl.test_all_nodes().await;
                });
            });
        }

        view
    }

    pub fn apply_snapshot(&self, snap: &EngineSnapshot) {
        self.test_all_btn.set_sensitive(!snap.nodes_testing && !snap.nodes.is_empty());
        self.test_all_btn
            .set_label(if snap.nodes_testing { "测速中" } else { "测速" });

        let filter = self.filter_text.borrow().clone();
        let sig = format!(
            "{:?}:{}:{}:{}:{}",
            snap.node_sort_mode,
            snap.selected_node_index,
            filter,
            snap.nodes.len(),
            snap.nodes
                .iter()
                .map(|n| format!("{}:{:?}:{:?}", n.index, n.latency_ms, n.last_error.is_some()))
                .collect::<Vec<_>>()
                .join(",")
        );
        if *self.last_signature.borrow() == sig {
            return;
        }
        *self.last_signature.borrow_mut() = sig;

        while let Some(c) = self.list_box.first_child() {
            self.list_box.remove(&c);
        }
        let _ = &self.search_entry;

        if snap.nodes.is_empty() {
            let empty = adw::StatusPage::builder()
                .icon_name("network-server-symbolic")
                .title("暂无可用节点")
                .build();
            self.list_box.append(&empty);
            return;
        }

        let group = adw::PreferencesGroup::new();
        for node in &snap.nodes {
            if !filter.is_empty()
                && !node.name.to_lowercase().contains(&filter)
                && !node.protocol_label.to_lowercase().contains(&filter)
            {
                continue;
            }
            let is_selected = node.index == snap.selected_node_index;
            let subtitle = if node.tags.is_empty() {
                node.protocol_label.clone()
            } else {
                format!("{} · {}", node.protocol_label, node.tags.join(" · "))
            };
            let row = adw::ActionRow::builder()
                .title(&node.name)
                .subtitle(&subtitle)
                .activatable(node.supported)
                .build();

            if is_selected {
                let check = gtk::Image::from_icon_name("object-select-symbolic");
                row.add_prefix(&check);
            }

            let lat_label = gtk::Label::new(Some(&match (node.latency_ms, &node.last_error) {
                (Some(ms), _) => format!("{ms} ms"),
                (None, Some(_)) => "超时".to_string(),
                (None, None) => "-".to_string(),
            }));
            lat_label.add_css_class("monospace");
            lat_label.add_css_class("dim-label");
            row.add_suffix(&lat_label);

            let test_btn = gtk::Button::from_icon_name("view-refresh-symbolic");
            test_btn.add_css_class("flat");
            test_btn.set_valign(gtk::Align::Center);
            let idx = node.index;
            {
                let ctrl = self.controller.clone();
                let rt = self.rt.clone();
                test_btn.connect_clicked(move |_| {
                    let ctrl = ctrl.clone();
                    rt.spawn(async move {
                        let _ = ctrl.test_single_node(idx).await;
                    });
                });
            }
            row.add_suffix(&test_btn);

            {
                let ctrl = self.controller.clone();
                let rt = self.rt.clone();
                row.connect_activated(move |_| {
                    let ctrl = ctrl.clone();
                    rt.spawn(async move {
                        let _ = ctrl.select_node(idx).await;
                    });
                });
            }
            group.add(&row);
        }
        self.list_box.append(&group);
    }
}
