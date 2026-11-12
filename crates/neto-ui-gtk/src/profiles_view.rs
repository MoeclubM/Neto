#![cfg(target_os = "linux")]

use crate::format::format_bytes;
use adw::prelude::*;
use gtk::prelude::*;
use neto_core::{EngineSnapshot, NetoController};
use std::cell::RefCell;
use std::rc::Rc;
use std::sync::Arc;
use tokio::runtime::Handle;

pub struct ProfilesView {
    pub root: gtk::ScrolledWindow,
    list_box: gtk::Box,
    status_label: gtk::Label,
    last_sig: RefCell<String>,
    controller: Arc<NetoController>,
    rt: Handle,
}

impl ProfilesView {
    pub fn new(controller: Arc<NetoController>, rt: Handle) -> Rc<Self> {
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
        let outer = gtk::Box::new(gtk::Orientation::Vertical, 12);

        let add_group = adw::PreferencesGroup::builder().title("导入订阅").build();
        let name_row = adw::EntryRow::builder().title("名称（可选）").build();
        let url_row = adw::EntryRow::builder().title("订阅链接 URL").build();
        let import_btn = gtk::Button::with_label("拉取并添加");
        import_btn.add_css_class("suggested-action");
        import_btn.set_valign(gtk::Align::Center);
        url_row.add_suffix(&import_btn);
        add_group.add(&name_row);
        add_group.add(&url_row);

        let yaml_expander = gtk::Expander::new(Some("导入本地 YAML 配置"));
        let yaml_box = gtk::Box::new(gtk::Orientation::Vertical, 8);
        yaml_box.set_margin_top(6);
        let yaml_view = gtk::TextView::new();
        yaml_view.set_monospace(true);
        let yaml_scroll = gtk::ScrolledWindow::builder()
            .min_content_height(120)
            .child(&yaml_view)
            .build();
        yaml_scroll.add_css_class("card");
        let add_yaml_btn = gtk::Button::with_label("保存本地 YAML");
        add_yaml_btn.set_halign(gtk::Align::End);
        yaml_box.append(&yaml_scroll);
        yaml_box.append(&add_yaml_btn);
        yaml_expander.set_child(Some(&yaml_box));

        let status_label = gtk::Label::new(None);
        status_label.set_xalign(0.0);
        status_label.set_wrap(true);
        status_label.set_visible(false);

        let list_box = gtk::Box::new(gtk::Orientation::Vertical, 10);
        outer.append(&add_group);
        outer.append(&yaml_expander);
        outer.append(&status_label);
        outer.append(&list_box);
        clamp.set_child(Some(&outer));
        scroll.set_child(Some(&clamp));

        {
            let ctrl = controller.clone();
            let handle = rt.clone();
            let nrow = name_row.clone();
            let urow = url_row.clone();
            let slbl = status_label.clone();
            import_btn.connect_clicked(move |btn| {
                let name = nrow.text().to_string();
                let url = urow.text().to_string();
                if url.trim().is_empty() {
                    return;
                }
                btn.set_sensitive(false);
                let ctrl = ctrl.clone();
                let (tx, rx) = std::sync::mpsc::channel();
                handle.spawn(async move {
                    let res = ctrl
                        .add_remote_profile(&name, &url, "aerion", 60)
                        .await
                        .map_err(|e| format!("{e:#}"));
                    let _ = tx.send(res);
                });
                let btn_c = btn.clone();
                let urow_c = urow.clone();
                let slbl_c = slbl.clone();
                gtk::glib::timeout_add_local(std::time::Duration::from_millis(120), move || {
                    match rx.try_recv() {
                        Ok(Ok(_)) => {
                            btn_c.set_sensitive(true);
                            urow_c.set_text("");
                            slbl_c.set_visible(false);
                            gtk::glib::ControlFlow::Break
                        }
                        Ok(Err(err)) => {
                            btn_c.set_sensitive(true);
                            slbl_c.set_text(&err);
                            slbl_c.set_visible(true);
                            gtk::glib::ControlFlow::Break
                        }
                        Err(std::sync::mpsc::TryRecvError::Empty) => {
                            gtk::glib::ControlFlow::Continue
                        }
                        Err(_) => {
                            btn_c.set_sensitive(true);
                            gtk::glib::ControlFlow::Break
                        }
                    }
                });
            });
        }

        {
            let ctrl = controller.clone();
            let handle = rt.clone();
            let nrow = name_row;
            let buf = yaml_view.buffer();
            let slbl = status_label.clone();
            add_yaml_btn.connect_clicked(move |_| {
                let name = nrow.text().to_string();
                let yaml = buf
                    .text(&buf.start_iter(), &buf.end_iter(), false)
                    .to_string();
                if yaml.trim().is_empty() {
                    return;
                }
                let ctrl = ctrl.clone();
                let (tx, rx) = std::sync::mpsc::channel();
                handle.spawn(async move {
                    let res = ctrl
                        .import_local_profile(&name, &yaml)
                        .await
                        .map_err(|e| format!("{e:#}"));
                    let _ = tx.send(res);
                });
                let buf_c = buf.clone();
                let slbl_c = slbl.clone();
                gtk::glib::timeout_add_local(std::time::Duration::from_millis(100), move || {
                    match rx.try_recv() {
                        Ok(Ok(_)) => {
                            buf_c.set_text("");
                            slbl_c.set_visible(false);
                            gtk::glib::ControlFlow::Break
                        }
                        Ok(Err(err)) => {
                            slbl_c.set_text(&err);
                            slbl_c.set_visible(true);
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

        Rc::new(Self {
            root: scroll,
            list_box,
            status_label,
            last_sig: RefCell::new(String::new()),
            controller,
            rt,
        })
    }

    pub fn apply_snapshot(&self, snap: &EngineSnapshot) {
        let _ = &self.status_label;
        let sig = snap
            .profiles
            .iter()
            .map(|p| {
                format!(
                    "{}:{}:{}:{}:{}:{}",
                    p.id, p.active, p.node_count, p.rule_count, p.updated_at_unix, p.used_bytes
                )
            })
            .collect::<Vec<_>>()
            .join("|");
        if *self.last_sig.borrow() == sig {
            return;
        }
        *self.last_sig.borrow_mut() = sig;

        while let Some(c) = self.list_box.first_child() {
            self.list_box.remove(&c);
        }
        if snap.profiles.is_empty() {
            return;
        }
        let group = adw::PreferencesGroup::builder().title("已保存订阅").build();
        for prof in &snap.profiles {
            let quota = if prof.total_bytes > 0 {
                format!(
                    " · {} / {}",
                    format_bytes(prof.used_bytes),
                    format_bytes(prof.total_bytes)
                )
            } else {
                String::new()
            };
            let row = adw::ActionRow::builder()
                .title(&prof.name)
                .subtitle(&format!("{} 节点 · {} 规则{quota}", prof.node_count, prof.rule_count))
                .activatable(true)
                .build();
            if prof.active {
                row.add_prefix(&gtk::Image::from_icon_name("object-select-symbolic"));
            }
            let pid = prof.id.clone();
            if !prof.url.is_empty() {
                let rbtn = gtk::Button::from_icon_name("view-refresh-symbolic");
                rbtn.add_css_class("flat");
                rbtn.set_valign(gtk::Align::Center);
                let ctrl = self.controller.clone();
                let rt = self.rt.clone();
                let id_c = pid.clone();
                rbtn.connect_clicked(move |_| {
                    let ctrl = ctrl.clone();
                    let id_c = id_c.clone();
                    rt.spawn(async move {
                        let _ = ctrl.refresh_profile(Some(&id_c)).await;
                    });
                });
                row.add_suffix(&rbtn);
            }
            let dbtn = gtk::Button::from_icon_name("user-trash-symbolic");
            dbtn.add_css_class("flat");
            dbtn.set_valign(gtk::Align::Center);
            {
                let ctrl = self.controller.clone();
                let rt = self.rt.clone();
                let id_c = pid.clone();
                dbtn.connect_clicked(move |_| {
                    let ctrl = ctrl.clone();
                    let id_c = id_c.clone();
                    rt.spawn(async move {
                        let _ = ctrl.delete_profile(&id_c).await;
                    });
                });
            }
            row.add_suffix(&dbtn);
            {
                let ctrl = self.controller.clone();
                let rt = self.rt.clone();
                row.connect_activated(move |_| {
                    let ctrl = ctrl.clone();
                    let pid = pid.clone();
                    rt.spawn(async move {
                        let _ = ctrl.select_profile(&pid).await;
                    });
                });
            }
            group.add(&row);
        }
        self.list_box.append(&group);
    }
}
