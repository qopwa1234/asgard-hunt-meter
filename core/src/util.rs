#![allow(dead_code)]

use std::sync::atomic::AtomicBool;

pub static STOP: AtomicBool = AtomicBool::new(false);

#[cfg(windows)]
#[repr(C)]
#[derive(Default, Clone, Copy)]
pub struct WinSystemTime {
    pub year: u16,
    pub month: u16,
    pub day_of_week: u16,
    pub day: u16,
    pub hour: u16,
    pub minute: u16,
    pub second: u16,
    pub milliseconds: u16,
}

#[cfg(windows)]
extern "system" {
    fn GetLocalTime(lp_system_time: *mut WinSystemTime);
    fn SetConsoleOutputCP(code_page: u32) -> i32;
    fn SetConsoleCP(code_page: u32) -> i32;
    fn SetConsoleCtrlHandler(
        handler: Option<unsafe extern "system" fn(u32) -> i32>,
        add: i32,
    ) -> i32;
}

#[cfg(windows)]
unsafe extern "system" fn ctrl_handler(_ctrl_type: u32) -> i32 {
    use std::sync::atomic::Ordering;
    STOP.store(true, Ordering::SeqCst);
    1
}

/// UTF-8 console output + Ctrl+C stop flag.
pub fn init() {
    #[cfg(windows)]
    unsafe {
        SetConsoleOutputCP(65001);
        SetConsoleCP(65001);
        SetConsoleCtrlHandler(Some(ctrl_handler), 1);
    }
}

pub fn stopped() -> bool {
    use std::sync::atomic::Ordering;
    STOP.load(Ordering::SeqCst)
}

pub fn local_hms() -> (u16, u16, u16) {
    #[cfg(windows)]
    {
        let mut st = WinSystemTime::default();
        unsafe { GetLocalTime(&mut st) };
        (st.hour, st.minute, st.second)
    }
    #[cfg(not(windows))]
    {
        (0, 0, 0)
    }
}

pub fn ts_string() -> String {
    let (h, m, s) = local_hms();
    format!("{:02}:{:02}:{:02}", h, m, s)
}

pub fn now_unix_ms() -> u128 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_millis())
        .unwrap_or(0)
}

pub fn hex_short(data: &[u8], max: usize) -> String {
    let mut s = String::with_capacity(max * 2);
    for &b in data.iter().take(max) {
        s.push_str(&format!("{:02x}", b));
    }
    s
}

pub fn printable_asc(data: &[u8], max: usize) -> String {
    data.iter()
        .take(max)
        .map(|&b| if (0x20..0x7f).contains(&b) { b as char } else { '.' })
        .collect()
}

pub fn json_escape(s: &str) -> String {
    let mut out = String::with_capacity(s.len() + 8);
    for c in s.chars() {
        match c {
            '"' => out.push_str("\\\""),
            '\\' => out.push_str("\\\\"),
            '\n' => out.push_str("\\n"),
            '\r' => out.push_str("\\r"),
            '\t' => out.push_str("\\t"),
            c if (c as u32) < 0x20 => out.push_str(&format!("\\u{:04x}", c as u32)),
            c => out.push(c),
        }
    }
    out
}

pub struct Args {
    flags: Vec<(String, String)>,
    bools: Vec<String>,
    pub pos: Vec<String>,
}

pub fn parse_args(args: &[String]) -> Args {
    let mut a = Args {
        flags: Vec::new(),
        bools: Vec::new(),
        pos: Vec::new(),
    };
    let mut i = 0;
    while i < args.len() {
        let t = &args[i];
        if let Some(rest) = t.strip_prefix("--") {
            if let Some(eq) = rest.find('=') {
                a.flags
                    .push((rest[..eq].to_string(), rest[eq + 1..].to_string()));
            } else if i + 1 < args.len() && !args[i + 1].starts_with("--") {
                a.flags.push((rest.to_string(), args[i + 1].clone()));
                i += 1;
            } else {
                a.bools.push(rest.to_string());
            }
        } else {
            a.pos.push(t.clone());
        }
        i += 1;
    }
    a
}

impl Args {
    pub fn get(&self, name: &str) -> Option<&str> {
        self.flags
            .iter()
            .rev()
            .find(|(k, _)| k == name)
            .map(|(_, v)| v.as_str())
    }
    pub fn all(&self, name: &str) -> Vec<&str> {
        self.flags
            .iter()
            .filter(|(k, _)| k == name)
            .map(|(_, v)| v.as_str())
            .collect()
    }
    pub fn flag(&self, name: &str) -> bool {
        self.bools.iter().any(|k| k == name)
    }
}

pub fn parse_key(s: &str) -> Option<[u8; 9]> {
    let parts: Vec<&str> = s.split(',').collect();
    if parts.len() != 9 {
        return None;
    }
    let mut k = [0u8; 9];
    for (i, p) in parts.iter().enumerate() {
        let t = p.trim().trim_start_matches("0x").trim_start_matches("0X");
        k[i] = u8::from_str_radix(t, 16).ok()?;
    }
    Some(k)
}

pub fn default_stem(path: &str) -> String {
    let name = path
        .rsplit(|c| c == '\\' || c == '/')
        .next()
        .unwrap_or(path);
    match name.rfind('.') {
        Some(i) => name[..i].to_string(),
        None => name.to_string(),
    }
}
