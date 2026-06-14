mod agent_manager;
mod commands;
mod tray;

use commands::AppState;
use tauri::Manager;

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    let app_state = AppState {
        agent: std::sync::Mutex::new(agent_manager::AgentManager::new()),
        command_lock: std::sync::Mutex::new(()),
    };

    tauri::Builder::default()
        .manage(app_state)
        .plugin(tauri_plugin_store::Builder::default().build())
        .plugin(tauri_plugin_opener::init())
        .setup(|app| {
            if cfg!(debug_assertions) {
                app.handle().plugin(
                    tauri_plugin_log::Builder::default()
                        .level(log::LevelFilter::Info)
                        .build(),
                )?;
            }
            tray::build_tray(app.handle())?;
            Ok(())
        })
        .on_window_event(|window, event| {
            match event {
                tauri::WindowEvent::CloseRequested { api, .. } => {
                    // Hide to tray instead of closing
                    let _ = window.hide();
                    api.prevent_close();
                }
                tauri::WindowEvent::Destroyed => {
                    if let Some(state) = window.try_state::<AppState>() {
                        if let Ok(mut agent) = state.agent.lock() {
                            let _ = agent.stop();
                        }
                    }
                }
                _ => {}
            }
        })
        .invoke_handler(tauri::generate_handler![
            commands::start_agent,
            commands::stop_agent,
            commands::send_command,
            commands::agent_status,
            commands::get_log_path,
            commands::set_tray_tooltip,
            commands::quit_app,
        ])
        .run(tauri::generate_context!())
        .expect("error while running omenctl GUI");
}
