use std::io::{BufRead, BufReader, Write};
use std::process::{Child, ChildStdin, Command, Stdio};
use std::sync::{Arc, Mutex};

#[derive(Debug, Clone)]
pub enum AgentStatus {
    NotStarted,
    Starting,
    Running,
    Stopped,
    Error(String),
}

pub struct AgentManager {
    process: Option<Child>,
    stdin: Option<Arc<Mutex<ChildStdin>>>,
    stdout: Option<Arc<Mutex<BufReader<std::process::ChildStdout>>>>,
    status: Arc<Mutex<AgentStatus>>,
}

impl AgentManager {
    pub fn new() -> Self {
        Self {
            process: None,
            stdin: None,
            stdout: None,
            status: Arc::new(Mutex::new(AgentStatus::NotStarted)),
        }
    }

    pub fn start(&mut self) -> Result<(), String> {
        *self.status.lock().map_err(|e| e.to_string())? = AgentStatus::Starting;

        let (program, args) = resolve_agent_path()?;

        let mut cmd = Command::new(&program);
        cmd.args(&args)
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped());

        let mut child = cmd.spawn().map_err(|e| {
            format!("Failed to start agent ({}): {}", program, e)
        })?;

        let stdin = child.stdin.take()
            .ok_or_else(|| "Failed to capture agent stdin".to_string())?;
        let stdout = child.stdout.take()
            .ok_or_else(|| "Failed to capture agent stdout".to_string())?;

        // Spawn stderr pump
        if let Some(stderr) = child.stderr.take() {
            let status = self.status.clone();
            std::thread::spawn(move || {
                let reader = BufReader::new(stderr);
                for line in reader.lines() {
                    if let Ok(line) = line {
                        if !line.trim().is_empty() {
                            log::warn!("[omenctl stderr] {}", line.trim());
                        }
                    }
                }
                // Process exited
                if let Ok(mut s) = status.lock() {
                    *s = AgentStatus::Stopped;
                }
            });
        }

        self.stdin = Some(Arc::new(Mutex::new(stdin)));
        self.stdout = Some(Arc::new(Mutex::new(BufReader::new(stdout))));
        self.process = Some(child);
        *self.status.lock().map_err(|e| e.to_string())? = AgentStatus::Running;

        Ok(())
    }

    pub fn stop(&mut self) -> Result<(), String> {
        // Drop stdin/stdout handles first
        self.stdin = None;
        self.stdout = None;

        if let Some(mut child) = self.process.take() {
            child.kill().map_err(|e| format!("Failed to kill agent: {}", e))?;
            child.wait().map_err(|e| format!("Failed to wait for agent: {}", e))?;
        }

        *self.status.lock().map_err(|e| e.to_string())? = AgentStatus::Stopped;
        Ok(())
    }

    pub fn send_command(&self, command: &str) -> Result<String, String> {
        let stdin = self.stdin.as_ref()
            .ok_or("Agent not started")?;
        let stdout = self.stdout.as_ref()
            .ok_or("Agent not started")?;

        // Serialize writes through mutex
        let mut stdin_guard = stdin.lock().map_err(|e| e.to_string())?;
        writeln!(stdin_guard, "{}", command)
            .map_err(|e| format!("Failed to write to agent: {}", e))?;
        stdin_guard.flush().map_err(|e| format!("Failed to flush: {}", e))?;
        drop(stdin_guard);

        // Read one line response
        let mut stdout_guard = stdout.lock().map_err(|e| e.to_string())?;
        let mut response = String::new();
        stdout_guard.read_line(&mut response)
            .map_err(|e| format!("Failed to read from agent: {}", e))?;

        if response.trim().is_empty() {
            return Err("Agent closed stdout unexpectedly".to_string());
        }

        Ok(response.trim_end().to_string())
    }

    pub fn is_running(&self) -> bool {
        match *self.status.lock().unwrap_or_else(|e| e.into_inner()) {
            AgentStatus::Running => true,
            _ => false,
        }
    }

    pub fn status_string(&self) -> String {
        let status = self.status.lock().unwrap_or_else(|e| e.into_inner());
        match &*status {
            AgentStatus::NotStarted => "stopped".to_string(),
            AgentStatus::Starting => "starting".to_string(),
            AgentStatus::Running => "running".to_string(),
            AgentStatus::Stopped => "stopped".to_string(),
            AgentStatus::Error(msg) => format!("error:{}", msg),
        }
    }
}

fn resolve_agent_path() -> Result<(String, Vec<String>), String> {
    // 1. OMENCTL_AGENT_PATH env var
    if let Ok(path) = std::env::var("OMENCTL_AGENT_PATH") {
        let dotnet = find_dotnet();
        if path.ends_with(".dll") {
            return Ok((dotnet, vec![path]));
        }
        return Ok((path, vec![]));
    }

    // 2. Self-contained exe next to the Tauri binary
    if let Ok(exe_path) = std::env::current_exe() {
        if let Some(dir) = exe_path.parent() {
            let exe = dir.join("omenctl.exe");
            if exe.exists() {
                return Ok((exe.to_string_lossy().to_string(), vec![]));
            }
        }
    }

    // 3. Development fallback: agent DLL built by make.cmd
    let dev_path = std::path::PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .parent().unwrap()  // src-tauri -> omenctl.Gui
        .parent().unwrap()  // omenctl.Gui -> src
        .join("omenctl.Agent/bin/x64/Release/net10.0-windows/omenctl.dll");

    if dev_path.exists() {
        let dotnet = find_dotnet();
        return Ok((dotnet, vec![
            dev_path.to_string_lossy().to_string()
        ]));
    }

    Err("Could not locate omenctl agent binary. Set OMENCTL_AGENT_PATH env var or build the agent first.".to_string())
}

fn find_dotnet() -> String {
    // Check Scoop-installed .NET SDK first (matches make.cmd logic)
    let scoop = std::env::var("USERPROFILE")
        .map(|p| std::path::PathBuf::from(p).join("scoop/apps/dotnet-sdk/current/dotnet.exe"))
        .ok();
    if let Some(ref path) = scoop {
        if path.exists() {
            log::info!("Using Scoop dotnet: {}", path.display());
            return path.to_string_lossy().to_string();
        }
    }
    // Fall back to PATH
    "dotnet".to_string()
}
