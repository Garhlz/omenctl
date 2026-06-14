mod agent_manager;
mod commands;

use commands::AppState;
use tauri::Manager;

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    let app_state = AppState {
        agent: std::sync::Mutex::new(agent_manager::AgentManager::new()),
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
            Ok(())
        })
        .on_window_event(|_window, event| {
            if let tauri::WindowEvent::Destroyed = event {
                // Kill agent subprocess when GUI window closes
                if let Some(state) = _window.try_state::<AppState>() {
                    if let Ok(mut agent) = state.agent.lock() {
                        let _ = agent.stop();
                    }
                }
            }
        })
        .invoke_handler(tauri::generate_handler![
            commands::start_agent,
            commands::stop_agent,
            commands::send_command,
            commands::agent_status,
            commands::get_log_path,
        ])
        .run(tauri::generate_context!())
        .expect("error while running omenctl GUI");
}
