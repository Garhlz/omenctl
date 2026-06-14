use std::fs;
use std::io::{BufRead, BufReader, Write};
use std::path::PathBuf;
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
    log_path: Option<PathBuf>,
}

impl AgentManager {
    pub fn new() -> Self {
        Self {
            process: None,
            stdin: None,
            stdout: None,
            status: Arc::new(Mutex::new(AgentStatus::NotStarted)),
            generation: Arc::new(AtomicU64::new(0)),
            log_path: None,
        }
    }

    pub fn start(&mut self) -> Result<(), String> {
        if self.is_running() {
            return Ok(());
        }
        if self.process.is_some() {
            self.cleanup_process();
        }

        *self.status.lock().map_err(|e| e.to_string())? = AgentStatus::Starting;

        let (program, args) = resolve_agent_path().map_err(|e| {
            *self.status.lock().unwrap_or_else(|e| e.into_inner()) = AgentStatus::Error(e.clone());
            e
        })?;

        // Prepare log file
        let log_dir = dirs_next().join("omenctl").join("logs");
        let _ = fs::create_dir_all(&log_dir);
        let stamp = chrono::Local::now();
        let log_file = log_dir.join(format!("agent-{}.log", stamp.format("%Y%m%d-%H%M%S")));
        self.log_path = Some(log_file.clone());

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

        let gen = self.generation.fetch_add(1, Ordering::SeqCst) + 1;
        let status = self.status.clone();
        let generation = self.generation.clone();

        // Spawn stderr pump — logs to file and console
        if let Some(stderr) = child.stderr.take() {
            std::thread::spawn(move || {
                let mut log_writer = fs::File::create(&log_file).ok();
                let reader = BufReader::new(stderr);
                for line in reader.lines() {
                    if let Ok(line) = line {
                        if !line.trim().is_empty() {
                            log::warn!("[omenctl stderr] {}", line.trim());
                            if let Some(ref mut w) = log_writer {
                                let _ = writeln!(w, "{}", line.trim());
                                let _ = w.flush();
                            }
                        }
                    }
                }
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

    pub fn io_handles(&self) -> Option<(Arc<Mutex<ChildStdin>>, Arc<Mutex<BufReader<std::process::ChildStdout>>>)> {
        match (&self.stdin, &self.stdout) {
            (Some(si), Some(so)) => Some((si.clone(), so.clone())),
            _ => None,
        }
    }

    pub fn log_path_string(&self) -> Option<String> {
        self.log_path.as_ref().map(|p| p.to_string_lossy().to_string())
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

impl Drop for AgentManager {
    fn drop(&mut self) {
        self.stdin = None;
        self.stdout = None;
        self.cleanup_process();
    }
}

fn resolve_agent_path() -> Result<(String, Vec<String>), String> {
    if let Ok(path) = std::env::var("OMENCTL_AGENT_PATH") {
        let dotnet = find_dotnet();
        if path.ends_with(".dll") {
            return Ok((dotnet, vec![path]));
        }
        return Ok((path, vec![]));
    }

    if let Ok(exe_path) = std::env::current_exe() {
        if let Some(dir) = exe_path.parent() {
            let exe = dir.join("omenctl.exe");
            if exe.exists() {
                return Ok((exe.to_string_lossy().to_string(), vec![]));
            }
        }
    }

    let dev_path = std::path::PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .parent().unwrap()
        .parent().unwrap()
        .join("omenctl.Agent/bin/x64/Release/net10.0-windows/omenctl.dll");

    if dev_path.exists() {
        let dotnet = find_dotnet();
        return Ok((dotnet, vec![dev_path.to_string_lossy().to_string()]));
    }

    Err("Could not locate omenctl agent binary. Set OMENCTL_AGENT_PATH env var or build the agent first.".to_string())
}

fn find_dotnet() -> String {
    let scoop = std::env::var("USERPROFILE")
        .map(|p| std::path::PathBuf::from(p).join("scoop/apps/dotnet-sdk/current/dotnet.exe"))
        .ok();
    if let Some(ref path) = scoop {
        if path.exists() {
            log::info!("Using Scoop dotnet: {}", path.display());
            return path.to_string_lossy().to_string();
        }
    }
    "dotnet".to_string()
}

fn dirs_next() -> PathBuf {
    #[cfg(target_os = "windows")]
    {
        std::env::var("APPDATA")
            .map(PathBuf::from)
            .unwrap_or_else(|_| PathBuf::from("."))
    }
    #[cfg(not(target_os = "windows"))]
    {
        std::env::var("HOME")
            .map(|h| PathBuf::from(h).join(".local").join("share"))
            .unwrap_or_else(|_| PathBuf::from("."))
    }
}
