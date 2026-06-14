use std::io::Write;
use std::io::BufRead;
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
    // Clone I/O handles and release the AppState lock before blocking I/O
    let (stdin, stdout) = {
        let agent = state.agent.lock().map_err(|e| e.to_string())?;
        agent.io_handles().ok_or("Agent not started")?
    };

    // Write
    let mut stdin_guard = stdin.lock().map_err(|e| e.to_string())?;
    writeln!(stdin_guard, "{}", command)
        .map_err(|e| format!("Failed to write to agent: {}", e))?;
    stdin_guard.flush().map_err(|e| format!("Failed to flush: {}", e))?;
    drop(stdin_guard);

    // Read with timeout
    let (tx, rx) = std::sync::mpsc::channel();
    std::thread::spawn(move || {
        let mut reader = stdout.lock().unwrap_or_else(|e| e.into_inner());
        let mut line = String::new();
        let result = reader.read_line(&mut line)
            .map(|_| line.trim_end().to_string())
            .map_err(|e| format!("Failed to read from agent: {}", e));
        let _ = tx.send(result);
    });

    match rx.recv_timeout(std::time::Duration::from_secs(15)) {
        Ok(Ok(response)) => {
            if response.is_empty() {
                Err("Agent closed stdout unexpectedly".to_string())
            } else {
                Ok(response)
            }
        }
        Ok(Err(e)) => Err(e),
        Err(_timeout) => Err("Agent command timed out (15s)".to_string()),
    }
}

#[tauri::command]
pub fn agent_status(state: State<AppState>) -> String {
    let agent = state.agent.lock()
        .map(|a| a.status_string())
        .unwrap_or_else(|_| "error:lock_failed".to_string());
    agent
}

#[tauri::command]
pub fn get_log_path(state: State<AppState>) -> Option<String> {
    let agent = state.agent.lock().ok()?;
    agent.log_path_string()
}
