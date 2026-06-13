use std::sync::Mutex;
use tauri::State;

use crate::agent_manager::AgentManager;

pub struct AppState {
    pub agent: Mutex<AgentManager>,
}

#[tauri::command]
pub fn start_agent(state: State<AppState>) -> Result<String, String> {
    let mut agent = state.agent.lock().map_err(|e| e.to_string())?;
    agent.start()?;
    Ok("ok".to_string())
}

#[tauri::command]
pub fn stop_agent(state: State<AppState>) -> Result<String, String> {
    let mut agent = state.agent.lock().map_err(|e| e.to_string())?;
    agent.stop()?;
    Ok("ok".to_string())
}

#[tauri::command]
pub fn send_command(state: State<AppState>, command: String) -> Result<String, String> {
    let agent = state.agent.lock().map_err(|e| e.to_string())?;
    agent.send_command(&command)
}

#[tauri::command]
pub fn agent_status(state: State<AppState>) -> String {
    let agent = state.agent.lock()
        .map(|a| a.status_string())
        .unwrap_or_else(|_| "error:lock_failed".to_string());
    agent
}
