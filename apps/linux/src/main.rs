#[cfg(not(target_os = "linux"))]
fn main() {
    eprintln!("neto-linux is only built on Linux Wayland/X11 targets");
}

#[cfg(target_os = "linux")]
fn main() -> anyhow::Result<()> {
    use adw::prelude::*;
    use gtk::prelude::*;
    use neto_core::{NetoController, init_rustls_provider};
    use neto_platform_linux::{
        LinuxWaylandPlatformHooks, WaylandTrayAction, WaylandTrayHandle, WaylandTrayState,
        prefer_wayland_backend,
    };
    use neto_ui_gtk::{
        connections_view::ConnectionsView, home_view::HomeView, install_neto_css,
        nodes_view::NodesView, profiles_view::ProfilesView, rules_view::RulesView,
        settings_view::SettingsView,
    };
    use std::path::PathBuf;
    use std::rc::Rc;
    use std::time::Duration;

    init_rustls_provider();
    prefer_wayland_backend();

    let rt = tokio::runtime::Runtime::new()?;
    let handle = rt.handle().clone();

    let app_id = "io.github.moeclubm.Neto";
    let data_dir = std::env::var_os("XDG_DATA_HOME")
        .map(PathBuf::from)
        .or_else(|| std::env::var_os("HOME").map(|h| PathBuf::from(h).join(".local/share")))
        .unwrap_or_else(|| PathBuf::from("/tmp"))
        .join("neto");
    let hooks = LinuxWaylandPlatformHooks::new(app_id, "Neto");
    let controller = NetoController::new(data_dir, "Aerion", Some(hooks))?;
    let start_minimized = std::env::args().any(|a| a == "--minimized");

    let app = adw::Application::builder().application_id(app_id).build();
    let ctrl_activate = controller.clone();
    let handle_activate = handle.clone();

    app.connect_activate(move |app| {
        install_neto_css();

        let stack = adw::ViewStack::new();
        let stack_for_connect = stack.clone();
        let open_nodes: Rc<dyn Fn()> = Rc::new(move || {
            stack_for_connect.set_visible_child_name("nodes");
        });
        let stack_for_conns = stack.clone();
        let open_connections: Rc<dyn Fn()> = Rc::new(move || {
            stack_for_conns.set_visible_child_name("connections");
        });
        let stack_for_profiles = stack.clone();
        let open_profiles: Rc<dyn Fn()> = Rc::new(move || {
            stack_for_profiles.set_visible_child_name("profiles");
        });

        let home_view = HomeView::new(
            ctrl_activate.clone(),
            handle_activate.clone(),
            open_nodes,
        );
        home_view.set_on_open_connections(open_connections);
        home_view.set_on_open_profiles(open_profiles);
        let nodes_view = NodesView::new(ctrl_activate.clone(), handle_activate.clone());
        let connections_view = ConnectionsView::new(ctrl_activate.clone(), handle_activate.clone());
        let rules_view = RulesView::new(ctrl_activate.clone(), handle_activate.clone());
        let profiles_view = ProfilesView::new(ctrl_activate.clone(), handle_activate.clone());
        let settings_view = SettingsView::new(ctrl_activate.clone());
        settings_view.configure_updater(
            "MoeclubM/Neto",
            option_env!("NETO_APP_VERSION").unwrap_or(env!("CARGO_PKG_VERSION")),
        );

        stack.add_titled_with_icon(
            &home_view.root,
            Some("home"),
            "主页",
            "go-home-symbolic",
        );
        stack.add_titled_with_icon(
            &nodes_view.root,
            Some("nodes"),
            "节点",
            "network-server-symbolic",
        );
        stack.add_titled_with_icon(
            &connections_view.root,
            Some("connections"),
            "会话",
            "network-transmit-receive-symbolic",
        );
        stack.add_titled_with_icon(
            &rules_view.root,
            Some("rules"),
            "规则",
            "view-list-symbolic",
        );
        stack.add_titled_with_icon(
            &profiles_view.root,
            Some("profiles"),
            "订阅",
            "folder-download-symbolic",
        );
        stack.add_titled_with_icon(
            &settings_view.root,
            Some("settings"),
            "设置",
            "emblem-system-symbolic",
        );

        let header_title = gtk::Label::new(None);
        header_title.add_css_class("title");
        let header = adw::HeaderBar::builder()
            .title_widget(&header_title)
            .build();
        header.add_css_class("flat");

        let bottom_bar = adw::ViewSwitcherBar::builder()
            .stack(&stack)
            .reveal(true)
            .build();

        let toolbar_view = adw::ToolbarView::new();
        toolbar_view.add_top_bar(&header);
        toolbar_view.set_content(Some(&stack));
        toolbar_view.add_bottom_bar(&bottom_bar);

        {
            let title_lbl = header_title.clone();
            let pv = profiles_view.clone();
            let sv = settings_view.clone();
            stack.connect_visible_child_name_notify(move |s| {
                let name = s.visible_child_name();
                if name.as_deref() == Some("profiles") {
                    pv.reset_to_root();
                } else if name.as_deref() == Some("settings") {
                    sv.reset_to_root();
                }
                let text = match name.as_deref() {
                    Some("home") | None => "",
                    Some("nodes") => "节点",
                    Some("connections") => "会话",
                    Some("rules") => "规则",
                    Some("profiles") => "订阅",
                    Some("settings") => "设置",
                    _ => "",
                };
                title_lbl.set_text(text);
            });
        }

        let window = adw::ApplicationWindow::builder()
            .application(app)
            .icon_name(app_id)
            .default_width(820)
            .default_height(600)
            .content(&toolbar_view)
            .build();

        window.connect_close_request(|win| {
            win.set_visible(false);
            gtk::glib::Propagation::Stop
        });

        let snap0 = ctrl_activate.snapshot();
        home_view.apply_snapshot(&snap0);
        nodes_view.apply_snapshot(&snap0);
        connections_view.apply_snapshot(&snap0);
        rules_view.apply_snapshot(&snap0);
        profiles_view.apply_snapshot(&snap0);
        settings_view.apply_snapshot(&snap0);

        let (tray_event_tx, tray_event_rx) = std::sync::mpsc::channel::<WaylandTrayAction>();
        let tray_holder: Rc<std::cell::RefCell<Option<WaylandTrayHandle>>> =
            Rc::new(std::cell::RefCell::new(None));
        {
            let init_state = WaylandTrayState {
                app_id: app_id.to_string(),
                app_name: "Neto".to_string(),
                connected: snap0.connection.connected,
                active_node: snap0.connection.active_node_name.clone(),
                routing_mode: snap0.settings.routing_mode,
                system_proxy_enabled: snap0.settings.system_proxy_enabled,
                tun_enabled: snap0.settings.tun_enabled,
            };
            let (ready_tx, ready_rx) = std::sync::mpsc::channel();
            handle_activate.spawn(async move {
                if let Ok((tray_handle, mut rx)) = WaylandTrayHandle::spawn(init_state).await {
                    let _ = ready_tx.send(tray_handle);
                    while let Some(action) = rx.recv().await {
                        let _ = tray_event_tx.send(action);
                    }
                }
            });
            let holder = tray_holder.clone();
            gtk::glib::timeout_add_local(Duration::from_millis(250), move || {
                if let Ok(th) = ready_rx.try_recv() {
                    *holder.borrow_mut() = Some(th);
                    return gtk::glib::ControlFlow::Break;
                }
                gtk::glib::ControlFlow::Continue
            });
        }

        {
            let ctrl = ctrl_activate.clone();
            let rt_h = handle_activate.clone();
            let win = window.clone();
            let app_c = app.clone();
            let holder = tray_holder;
            gtk::glib::timeout_add_local(Duration::from_millis(800), move || {
                while let Ok(act) = tray_event_rx.try_recv() {
                    match act {
                        WaylandTrayAction::ShowWindow => {
                            win.set_visible(true);
                            win.present();
                        }
                        WaylandTrayAction::ToggleConnect => {
                            let c = ctrl.clone();
                            rt_h.spawn(async move {
                                let _ = c.toggle_connect().await;
                            });
                        }
                        WaylandTrayAction::SetRoutingMode(m) => {
                            let c = ctrl.clone();
                            rt_h.spawn(async move {
                                let _ = c.set_routing_mode(m).await;
                            });
                        }
                        WaylandTrayAction::ToggleSystemProxy => {
                            let next = !ctrl.snapshot().settings.system_proxy_enabled;
                            let _ = ctrl.set_system_proxy_enabled(next);
                        }
                        WaylandTrayAction::ToggleTun => {
                            let next = !ctrl.snapshot().settings.tun_enabled;
                            let c = ctrl.clone();
                            rt_h.spawn(async move {
                                let _ = c.set_tun_enabled(next).await;
                            });
                        }
                        WaylandTrayAction::Quit => {
                            let c = ctrl.clone();
                            let _ = rt_h.block_on(c.disconnect());
                            app_c.quit();
                            return gtk::glib::ControlFlow::Break;
                        }
                    }
                }
                let snap = ctrl.snapshot();
                home_view.apply_snapshot(&snap);
                nodes_view.apply_snapshot(&snap);
                connections_view.apply_snapshot(&snap);
                rules_view.apply_snapshot(&snap);
                profiles_view.apply_snapshot(&snap);
                settings_view.apply_snapshot(&snap);
                if let Some(th) = holder.borrow().clone() {
                    let tstate = WaylandTrayState {
                        app_id: app_id.to_string(),
                        app_name: "Neto".to_string(),
                        connected: snap.connection.connected,
                        active_node: snap.connection.active_node_name,
                        routing_mode: snap.settings.routing_mode,
                        system_proxy_enabled: snap.settings.system_proxy_enabled,
                        tun_enabled: snap.settings.tun_enabled,
                    };
                    rt_h.spawn(async move {
                        th.update(tstate).await;
                    });
                }
                gtk::glib::ControlFlow::Continue
            });
        }

        if !start_minimized {
            window.present();
        }
    });

    app.run_with_args::<&str>(&[]);
    let _ = rt.block_on(controller.disconnect());
    let _ = rt.block_on(neto_core::shutdown_vpns());
    Ok(())
}
