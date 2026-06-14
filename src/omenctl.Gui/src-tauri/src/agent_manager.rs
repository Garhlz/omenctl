use std::io::{BufRead, BufReader};
use std::process::{Child, ChildStdin, Command, Stdio};
use std::sync::{Arc, Mutex, atomic::{AtomicU64, Ordering}};

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
    generation: Arc<AtomicU64>,
}

impl AgentManager {
    pub fn new() -> Self {
        Self {
            process: None,
            stdin: None,
            stdout: None,
            status: Arc::new(Mutex::new(AgentStatus::NotStarted)),
            generation: Arc::new(AtomicU64::new(0)),
        }
    }

    pub fn start(&mut self) -> Result<(), String> {
        // Idempotent: don't spawn a second agent if one is already running
        if self.is_running() {
            return Ok(());
        }
        // If a dead process handle is still around, clean it up
        if self.process.is_some() {
            self.cleanup_process();
        }

        *self.status.lock().map_err(|e| e.to_string())? = AgentStatus::Starting;

        let (program, args) = resolve_agent_path().map_err(|e| {
            *self.status.lock().unwrap_or_else(|e| e.into_inner()) = AgentStatus::Error(e.clone());
            e
        })?;

        let mut cmd = Command::new(&program);
        cmd.args(&args)
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped());

        let mut child = cmd.spawn().map_err(|e| {
            let msg = format!("Failed to start agent ({}): {}", program, e);
            *self.status.lock().unwrap_or_else(|e| e.into_inner()) = AgentStatus::Error(msg.clone());
            msg
        })?;

        let stdin = child.stdin.take()
            .ok_or_else(|| "Failed to capture agent stdin".to_string())?;
        let stdout = child.stdout.take()
            .ok_or_else(|| "Failed to capture agent stdout".to_string())?;

        // Bump generation so old stderr threads know they're stale
        let gen = self.generation.fetch_add(1, Ordering::SeqCst) + 1;
        let status = self.status.clone();
        let generation = self.generation.clone();

        // Spawn stderr pump
        if let Some(stderr) = child.stderr.take() {
            std::thread::spawn(move || {
                let reader = BufReader::new(stderr);
                for line in reader.lines() {
                    if let Ok(line) = line {
                        if !line.trim().is_empty() {
                            log::warn!("[omenctl stderr] {}", line.trim());
                        }
                    }
                }
                // Process exited — only set Stopped if we're still the current generation
                if generation.load(Ordering::SeqCst) == gen {
                    if let Ok(mut s) = status.lock() {
                        *s = AgentStatus::Stopped;
                    }
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
        self.stdin = None;
        self.stdout = None;
        self.cleanup_process();
        *self.status.lock().map_err(|e| e.to_string())? = AgentStatus::Stopped;
        Ok(())
    }

    fn cleanup_process(&mut self) {
        if let Some(mut child) = self.process.take() {
            let _ = child.kill();
            let _ = child.wait();
        }
    }

    /// Clone the I/O handles so callers can do I/O without holding the outer AppState lock.
    pub fn io_handles(&self) -> Option<(Arc<Mutex<ChildStdin>>, Arc<Mutex<BufReader<std::process::ChildStdout>>>)> {
        match (&self.stdin, &self.stdout) {
            (Some(si), Some(so)) => Some((si.clone(), so.clone())),
            _ => None,
        }
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
