use tauri::{
    AppHandle, Emitter, Manager,
    menu::{MenuBuilder, MenuItemBuilder},
    tray::TrayIconBuilder,
};
use crate::commands::AppState;

pub fn build_tray(app: &AppHandle) -> Result<(), Box<dyn std::error::Error>> {
    let start_curve = MenuItemBuilder::with_id("tray_start_curve", "Start Curve").build(app)?;
    let stop_curve = MenuItemBuilder::with_id("tray_stop_curve", "Stop Curve").build(app)?;

    let menu = MenuBuilder::new(app)
        .item(&MenuItemBuilder::with_id("tray_snapshot", "Snapshot").build(app)?)
        .item(&MenuItemBuilder::with_id("tray_manual_45", "Manual 45/45").build(app)?)
        .item(&MenuItemBuilder::with_id("tray_max", "Max").build(app)?)
        .separator()
        .item(&start_curve)
        .item(&stop_curve)
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
                "tray_exit" => {
                    // Stop agent if running, then quit
                    if let Some(state) = app.try_state::<AppState>() {
                        if let Ok(mut agent) = state.agent.lock() {
                            // Stop curve first
                            let _ = agent.stop();
                        }
                    }
                    app.exit(0);
                }
                _ => {
                    let _ = app.emit(id, ());
                }
            }
        })
        .build(app)?;

    Ok(())
}
