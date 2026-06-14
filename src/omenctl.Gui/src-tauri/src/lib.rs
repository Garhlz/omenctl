mod agent_manager;
mod commands;

use commands::AppState;

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    let app_state = AppState {
        agent: std::sync::Mutex::new(agent_manager::AgentManager::new()),
    };

    tauri::Builder::default()
        .manage(app_state)
        .plugin(tauri_plugin_store::Builder::default().build())
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
        .invoke_handler(tauri::generate_handler![
            commands::start_agent,
            commands::stop_agent,
            commands::send_command,
            commands::agent_status,
        ])
        .run(tauri::generate_context!())
        .expect("error while running omenctl GUI");
}
