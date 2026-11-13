pub mod format;

#[cfg(target_os = "linux")]
pub mod connections_view;
#[cfg(target_os = "linux")]
pub mod home_view;
#[cfg(target_os = "linux")]
pub mod nodes_view;
#[cfg(target_os = "linux")]
pub mod profiles_view;
#[cfg(target_os = "linux")]
pub mod rules_view;
#[cfg(target_os = "linux")]
pub mod settings_view;

#[cfg(target_os = "linux")]
pub fn install_neto_css() {
    let provider = gtk::CssProvider::new();
    provider.load_from_data(
        "
        .neto-dot-idle {
            background-color: alpha(currentColor, 0.35);
            border-radius: 999px;
        }
        .neto-dot-active {
            background-color: #2ec27e;
            border-radius: 999px;
        }
        ",
    );
    if let Some(display) = gtk::gdk::Display::default() {
        gtk::style_context_add_provider_for_display(
            &display,
            &provider,
            gtk::STYLE_PROVIDER_PRIORITY_APPLICATION,
        );
    }
}
