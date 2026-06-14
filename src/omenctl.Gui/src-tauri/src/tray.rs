use tauri::{
    AppHandle, Emitter, Manager,
    menu::{MenuBuilder, MenuItemBuilder},
    tray::TrayIconBuilder,
};

pub fn build_tray(app: &AppHandle) -> Result<(), Box<dyn std::error::Error>> {
    let menu = MenuBuilder::new(app)
        .item(&MenuItemBuilder::with_id("tray_open", "Open Dashboard").build(app)?)
        .item(&MenuItemBuilder::with_id("tray_snapshot", "Snapshot").build(app)?)
        .separator()
        .item(&MenuItemBuilder::with_id("tray_start_curve", "Start Curve").build(app)?)
        .item(&MenuItemBuilder::with_id("tray_stop_curve", "Stop Curve").build(app)?)
        .item(&MenuItemBuilder::with_id("tray_manual_35", "Manual 35/35").build(app)?)
        .item(&MenuItemBuilder::with_id("tray_manual_45", "Manual 45/45").build(app)?)
        .item(&MenuItemBuilder::with_id("tray_manual_52", "Manual 52/52").build(app)?)
        .item(&MenuItemBuilder::with_id("tray_manual_64", "Manual 64/64").build(app)?)
        .item(&MenuItemBuilder::with_id("tray_max", "Max Fan").build(app)?)
        .item(&MenuItemBuilder::with_id("tray_logs", "Open Logs").build(app)?)
        .item(&MenuItemBuilder::with_id("tray_copy_json", "Copy Snapshot JSON").build(app)?)
        .separator()
        .item(&MenuItemBuilder::with_id("tray_exit", "Exit").build(app)?)
        .build()?;

    let _tray = TrayIconBuilder::with_id("omenctl-tray")
        .tooltip("omenctl")
        .icon(app.default_window_icon().unwrap().clone())
        .menu(&menu)
        .on_menu_event(|app, event| {
            let id = event.id().as_ref();
            match id {
                "tray_open" => {
                    if let Some(window) = app.get_webview_window("main") {
                        let _ = window.show();
                        let _ = window.set_focus();
                    }
                }
                "tray_logs" => {
                    // Emit event for frontend to handle (needs log path)
                    let _ = app.emit("tray_logs", ());
                }
                "tray_exit" => {
                    // Emit event — frontend handles curve stop confirmation
                    let _ = app.emit("tray_exit", ());
                }
                _ => {
                    let _ = app.emit(id, ());
                }
            }
        })
        .build(app)?;

    Ok(())
}
