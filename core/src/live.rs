#![allow(dead_code)]

use std::collections::HashMap;
use std::fs::{self, File};
use std::io::{BufRead, BufReader, Write};
use std::process::{Command, Stdio};
use std::time::Instant;

use crate::agg::Agg;
use crate::frame::{Dir, Frame};
use crate::keyrec;
use crate::pcap::{CapReader, Pkt};
use crate::pipeline::{ConnId, Pipeline};
use crate::report::{self, MsgKind, MsgStats, Stats};
use crate::util::{self, parse_args, parse_key};

/// Keeps dumpcap attached to this process lifetime: killed on any exit path
/// (normal exit, early return, or panic unwind).
struct ChildGuard(std::process::Child);

impl Drop for ChildGuard {
    fn drop(&mut self) {
        let _ = self.0.kill();
    }
}

fn find_dumpcap() -> String {
    let p = "C:\\Program Files\\Wireshark\\dumpcap.exe";
    if std::path::Path::new(p).exists() {
        p.to_string()
    } else {
        "dumpcap".to_string()
    }
}

fn list_ifaces(dumpcap: &str) -> Vec<(String, String)> {
    let mut v = Vec::new();
    if let Ok(o) = Command::new(dumpcap).arg("-D").output() {
        let text = String::from_utf8_lossy(&o.stdout).to_string();
        for line in text.lines() {
            let t = line.trim();
            if let Some(rest) = t.split_once(". ") {
                let r = rest.1;
                if let Some(open) = r.find(" (") {
                    let dev = r[..open].trim().to_string();
                    let desc = r[open + 2..].trim_end_matches(')').to_string();
                    v.push((dev, desc));
                } else {
                    v.push((r.trim().to_string(), String::new()));
                }
            }
        }
    }
    v
}

fn autopick(ifaces: &[(String, String)]) -> Option<String> {
    let mut best: Option<(i32, String)> = None;
    for (dev, desc) in ifaces {
        let d = desc.to_lowercase();
        let mut score = 0i32;
        for kw in [
            "ethernet", "이더넷", "wi-fi", "wifi", "wireless", "realtek", "intel", "killer",
            "marvell", "무선",
        ] {
            if d.contains(kw) {
                score += 10;
            }
        }
        for kw in [
            "loopback", "bluetooth", "vpn", "virtual", "hyper-v", "vmware", "tunnel", "npcap",
        ] {
            if d.contains(kw) {
                score -= 50;
            }
        }
        if score > best.as_ref().map(|b| b.0).unwrap_or(i32::MIN) {
            best = Some((score, dev.clone()));
        }
    }
    best.map(|b| b.1)
}

fn kind_str(k: MsgKind) -> &'static str {
    match k {
        MsgKind::Gold => "gold",
        MsgKind::Exp => "exp",
        MsgKind::Fame => "fame",
        MsgKind::Item => "item",
        MsgKind::Chat => "chat",
    }
}

struct ListenStats {
    frames: u64,
    s2c_frames: u64,
    c2s_frames: u64,
    by_op: [u64; 256],
    msgs: MsgStats,
    stats: Stats,
}

impl ListenStats {
    fn new() -> Self {
        ListenStats {
            frames: 0,
            s2c_frames: 0,
            c2s_frames: 0,
            by_op: [0u64; 256],
            msgs: MsgStats::new(),
            stats: Stats::new(),
        }
    }

    fn on_frame(&mut self, dir: Dir, f: &Frame) {
        self.frames += 1;
        match dir {
            Dir::S2C => self.s2c_frames += 1,
            Dir::C2S => self.c2s_frames += 1,
            Dir::Unknown => {}
        }
        self.by_op[f.op as usize] += 1;
        self.stats.add(f);
    }

    fn print_status(&self, secs: u64) {
        let mut tops: Vec<(u8, u64)> = (0..256)
            .map(|i| (i as u8, self.by_op[i]))
            .filter(|(_, n)| *n > 0)
            .collect();
        tops.sort_by(|a, b| b.1.cmp(&a.1));
        let s: Vec<String> = tops
            .iter()
            .take(5)
            .map(|(op, n)| format!("0x{:02x} {} {}", op, crate::opcode::s2c_name(*op), n))
            .collect();
        println!(
            "[상태] {}초 frames={} (s2c {} / c2s {}) gold={} exp={} | top: {}",
            secs,
            self.frames,
            self.s2c_frames,
            self.c2s_frames,
            self.msgs.gold,
            self.msgs.exp,
            s.join(", ")
        );
    }

    fn write_opstats(&self, out_dir: &str) {
        let _ = self.stats.write_opstats(format!("{}\\opstats.txt", out_dir));
    }
}

pub fn run(args: &[String]) -> i32 {
    let a = parse_args(args);
    let dumpcap = a
        .get("dumpcap")
        .map(|s| s.to_string())
        .unwrap_or_else(find_dumpcap);
    let iface_opt = a.get("iface").map(|s| s.to_string());
    let ifaces = if iface_opt.is_none() {
        list_ifaces(&dumpcap)
    } else {
        Vec::new()
    };
    let iface = match iface_opt {
        Some(i) => i,
        None => match autopick(&ifaces) {
            Some(i) => i,
            None => {
                eprintln!("[listen] 캡처 인터페이스를 찾지 못했습니다. dumpcap: {}", dumpcap);
                for (d, desc) in &ifaces {
                    eprintln!("    {}  ({})", d, desc);
                }
                eprintln!("[listen] Wireshark/Npcap 설치 및 관리자 권한을 확인하거나 --iface 를 지정하세요.");
                return 1;
            }
        },
    };

    let hosts: Vec<String> = a.all("host").iter().map(|s| s.to_string()).collect();
    let filter = if let Some(f) = a.get("filter") {
        f.to_string()
    } else if !hosts.is_empty() {
        if hosts.len() == 1 {
            format!("tcp and host {}", hosts[0])
        } else {
            let parts: Vec<String> = hosts.iter().map(|h| format!("host {}", h)).collect();
            format!("tcp and ({})", parts.join(" or "))
        }
    } else {
        "tcp".to_string()
    };

    // --port 는 단일 값과 범위("45000..45100")를 모두 허용한다.
    let mut ports = parse_ports(&a.all("port"));
    if ports.is_empty() {
        ports = vec![5010, 45016, 45017];
    }
    let key = a.get("key").and_then(|s| parse_key(s));
    let json_mode = a.flag("json");
    let quiet = a.flag("quiet") || json_mode;
    let seconds: Option<u64> = a.get("seconds").and_then(|s| s.parse().ok());
    let out_dir = a
        .get("out")
        .map(|s| s.to_string())
        .unwrap_or_else(|| format!("asgard_session_{}", util::now_unix_ms() / 1000));
    if let Err(e) = fs::create_dir_all(&out_dir) {
        eprintln!("[listen] 출력 폴더 생성 실패 {}: {}", out_dir, e);
        return 1;
    }

    if json_mode {
        println!(
            "{{\"type\":\"hello\",\"version\":\"{}\",\"proto\":1,\"out\":\"{}\"}}",
            env!("CARGO_PKG_VERSION"),
            util::json_escape(&out_dir)
        );
    } else {
        println!("[listen] dumpcap = {}", dumpcap);
        println!("[listen] iface   = {}", iface);
        println!("[listen] filter  = {}", filter);
        println!("[listen] ports   = {:?}", ports);
        println!("[listen] out     = {}", out_dir);
        println!("[listen] 시작합니다... (Ctrl+C 로 종료)");
    }

    let mut cmd = Command::new(&dumpcap);
    cmd.arg("-i")
        .arg(&iface)
        .arg("-w")
        .arg("-")
        .arg("-F")
        .arg("pcap")
        .arg("-q")
        .arg("-s")
        .arg("262144")
        .arg("-f")
        .arg(&filter)
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped());
    let mut child = ChildGuard(match cmd.spawn() {
        Ok(c) => c,
        Err(e) => {
            eprintln!("[listen] dumpcap 실행 실패: {} (설치 경로/관리자 권한 확인)", e);
            return 1;
        }
    });
    if let Some(err) = child.0.stderr.take() {
        std::thread::spawn(move || {
            let r = BufReader::new(err);
            for line in r.lines().flatten() {
                eprintln!("[dumpcap] {}", line);
            }
        });
    }
    let stdout = match child.0.stdout.take() {
        Some(s) => s,
        None => {
            eprintln!("[listen] dumpcap stdout 없음");
            let _ = child.0.kill();
            return 1;
        }
    };
    let mut reader = match CapReader::new(BufReader::with_capacity(1 << 20, stdout)) {
        Ok(r) => r,
        Err(e) => {
            eprintln!("[listen] 캡처 스트림 열기 실패: {} (관리자 권한 확인)", e);
            let _ = child.0.kill();
            return 1;
        }
    };

    let start_ms = util::now_unix_ms();
    let start = Instant::now();
    let mut last_report = Instant::now();
    let mut pipe = Pipeline::new(ports.clone(), key);
    // 2026-10-19 서버는 ctr 상위비트가 플래그(low7 인덱스)다 — 기본 low7 로 시작.
    // 구 동작(전체 바이트) 캡처를 라이브로 받을 때만 --full.
    pipe.ctr_half0 = !a.flag("full");
    let mut pkt = Pkt::default();
    let mut stats = ListenStats::new();
    let mut recs: HashMap<ConnId, keyrec::RecoverBuf> = HashMap::new();
    let mut rec_pending: Vec<(ConnId, u8, u8, Vec<u8>)> = Vec::new();
    let mut rec_reset: Vec<ConnId> = Vec::new();

    // 비버퍼 File: 실시간으로 즉시 기록(GUI/사용자가 파일을 바로 읽을 수 있게). flush() 는 no-op 로 남겨둔다.
    let mut f_s2c = File::create(format!("{}\\frames_s2c.bin", out_dir)).unwrap();
    let mut f_c2s = File::create(format!("{}\\frames_c2s.bin", out_dir)).unwrap();
    let mut events = File::create(format!("{}\\events.log", out_dir)).unwrap();
    let mut agg = Agg::new();
    let mut f_agg = File::create(format!("{}\\agg.jsonl", out_dir)).unwrap();
    let mut f_trace: Option<File> = if a.flag("trace") {
        Some(File::create(format!("{}\\trace.log", out_dir)).unwrap())
    } else {
        None
    };

    loop {
        if util::stopped() {
            println!("[listen] Ctrl+C 감지, 정리 중...");
            break;
        }
        if let Some(s) = seconds {
            if start.elapsed().as_secs() >= s {
                break;
            }
        }
        match reader.next(&mut pkt) {
            Ok(Some(())) => {
                let mut emit = |dir: Dir, ts: u64, cid: ConnId, f: &Frame| {
                    stats.on_frame(dir, f);
                    if let Some(tf) = f_trace.as_mut() {
                        let _ = writeln!(
                            tf,
                            "{}|{}|{}|{:?}|0x{:02x}|{}|ctr={}|clr={}|{}",
                            util::ts_string(),
                            ts,
                            match dir {
                                Dir::S2C => "S",
                                Dir::C2S => "C",
                                Dir::Unknown => "U",
                            },
                            cid,
                            f.op,
                            f.data.len(),
                            f.ctr,
                            if f.clr { 1 } else { 0 },
                            util::hex_short(&f.data, 24)
                        );
                    }
                    if dir == Dir::S2C && f.op == 0x0a {
                        rec_pending.push((cid, f.op, f.ctr, f.data.clone()));
                    }
                    let writer = match dir {
                        Dir::S2C => Some(&mut f_s2c),
                        Dir::C2S => Some(&mut f_c2s),
                        Dir::Unknown => None,
                    };
                    if let Some(w) = writer {
                        let _ = w.write_all(&[f.op]);
                        let _ = w.write_all(&(f.data.len() as u16).to_le_bytes());
                        let _ = w.write_all(&f.data);
                    }
                    if f.clr && f.op == 0x10 && f.data.len() >= 12 && f.data[0] == 0x02 {
                        if json_mode {
                            println!(
                                "{{\"type\":\"key\",\"t\":{},\"kind\":\"exchange\",\"mode\":{},\"key\":\"{}\"}}",
                                util::now_unix_ms(),
                                f.data[1],
                                util::hex_short(&f.data[3..12], 9)
                            );
                        } else {
                            println!(
                                "[{}] KEY 교환 감지: mode={} keylen={} key={}",
                                util::ts_string(),
                                f.data[1],
                                f.data[2],
                                util::hex_short(&f.data[3..12], 9)
                            );
                        }
                        rec_reset.push(cid);
                    }
                    if let Some((kind, amount, text)) = report::scan_msg(f.op, &f.data, &mut stats.msgs)
                    {
                        let line =
                            report::json_event_line(&util::ts_string(), kind_str(kind), amount, &text);
                        let _ = writeln!(events, "{}", line);
                        if !quiet {
                            match kind {
                                MsgKind::Gold => {
                                    println!("[{}] GOLD +{} | {}", util::ts_string(), amount, text)
                                }
                                MsgKind::Exp => {
                                    println!("[{}] EXP  +{} | {}", util::ts_string(), amount, text)
                                }
                                MsgKind::Fame => {
                                    println!("[{}] FAME +{} | {}", util::ts_string(), amount, text)
                                }
                                MsgKind::Item => println!("[{}] ITEM | {}", util::ts_string(), text),
                                MsgKind::Chat => println!("[{}] CHAT | {}", util::ts_string(), text),
                            }
                        }
                    }
                    if json_mode {
                        let mut lines: Vec<String> = Vec::new();
                        agg.on_frame(dir, ts / 1000, f, &mut lines);
                        for l in &lines {
                            let _ = writeln!(f_agg, "{}", l);
                            println!("{}", l);
                        }
                    }
                };
                pipe.feed(pkt.ts_us, pkt.linktype, &pkt.data, &mut emit);
                if !rec_pending.is_empty() || !rec_reset.is_empty() {
                    for cid in rec_reset.drain(..) {
                        recs.remove(&cid);
                    }
                    for (cid, op, ctr, data) in rec_pending.drain(..) {
                        let (base_key, base_half) = match pipe.conns.get(&cid) {
                            Some(c) => (c.codec.key, c.codec.ctr_half),
                            None => continue,
                        };
                        let st = recs
                            .entry(cid)
                            .or_insert_with(|| keyrec::RecoverBuf::new(base_key, base_half));
                        if st.base_key != base_key || st.base_half != base_half {
                            st.reset(base_key, base_half);
                        }
                        st.push(op, ctr, &data);
                        if st.ready() {
                            st.mark_tried();
                            if let Some(app) = keyrec::decide(st.base_key, st.base_half, &st.frames) {
                                let changed = app.key != base_key || app.ctr_half != base_half;
                                if changed {
                                    if let Some(c) = pipe.conns.get_mut(&cid) {
                                        c.codec.set_key(app.key);
                                        c.codec.set_ctr_half(app.ctr_half);
                                    }
                                    if json_mode {
                                        println!(
                                            "{{\"type\":\"key\",\"t\":{},\"kind\":\"restored\",\"key\":\"{}\",\"ctr\":\"{}\",\"frames\":{},\"hits\":{}}}",
                                            util::now_unix_ms(),
                                            util::hex_short(&app.key, 9),
                                            if app.ctr_half { "low7" } else { "full" },
                                            app.frames,
                                            app.hit_frames
                                        );
                                    } else {
                                        println!(
                                            "[{}] KEY 자동 복원: key={} ctr={} (msgFrames={}, hits={})",
                                            util::ts_string(),
                                            util::hex_short(&app.key, 9),
                                            if app.ctr_half { "low7" } else { "full" },
                                            app.frames,
                                            app.hit_frames
                                        );
                                    }
                                    st.reset(app.key, app.ctr_half);
                                    st.reported = true;
                                } else if !st.reported {
                                    st.reported = true;
                                    if json_mode {
                                        println!(
                                            "{{\"type\":\"key\",\"t\":{},\"kind\":\"verified\",\"key\":\"{}\",\"ctr\":\"{}\",\"frames\":{},\"hits\":{}}}",
                                            util::now_unix_ms(),
                                            util::hex_short(&app.key, 9),
                                            if app.ctr_half { "low7" } else { "full" },
                                            app.frames,
                                            app.hit_frames
                                        );
                                    } else {
                                        println!(
                                            "[{}] KEY 검증 완료: key={} ctr={} (msgFrames={}, hits={})",
                                            util::ts_string(),
                                            util::hex_short(&app.key, 9),
                                            if app.ctr_half { "low7" } else { "full" },
                                            app.frames,
                                            app.hit_frames
                                        );
                                    }
                                }
                            }
                        }
                    }
                }
                pipe.cleanup();
                let period = if json_mode { 1 } else { 5 };
                if last_report.elapsed().as_secs() >= period {
                    last_report = Instant::now();
                    let _ = f_s2c.flush();
                    let _ = f_c2s.flush();
                    let _ = events.flush();
                    if json_mode {
                        let lines = agg.status(util::now_unix_ms() as u64);
                        for l in &lines {
                            let _ = writeln!(f_agg, "{}", l);
                            println!("{}", l);
                        }
                    } else {
                        stats.print_status(start.elapsed().as_secs());
                    }
                    stats.write_opstats(&out_dir);
                }
            }
            Ok(None) => {
                eprintln!("[listen] 캡처 스트림이 종료되었습니다 (dumpcap 종료?)");
                break;
            }
            Err(e) => {
                eprintln!("[listen] 캡처 읽기 오류: {}", e);
                break;
            }
        }
    }

    let _ = child.0.kill();
    let _ = f_s2c.flush();
    let _ = f_c2s.flush();
    let _ = events.flush();
    if json_mode {
        let lines = agg.status(util::now_unix_ms() as u64);
        for l in &lines {
            let _ = writeln!(f_agg, "{}", l);
            println!("{}", l);
        }
        println!("{{\"type\":\"end\"}}");
    }
    let _ = f_agg.flush();
    stats.write_opstats(&out_dir);
    let dur = start.elapsed().as_secs().max(1);
    if !json_mode {
        println!(
            "[listen] 종료: {}초 frames={} (s2c {} / c2s {}) gold={} exp={} fame={}",
            dur,
            stats.frames,
            stats.s2c_frames,
            stats.c2s_frames,
            stats.msgs.gold,
            stats.msgs.exp,
            stats.msgs.fame
        );
    }
    let summary = format!(
        "{{\n  \"started_ms\": {},\n  \"ended_ms\": {},\n  \"duration_sec\": {},\n  \"frames\": {},\n  \"frames_s2c\": {},\n  \"frames_c2s\": {},\n  \"gold\": {},\n  \"exp\": {},\n  \"fame\": {},\n  \"gold_msgs\": {},\n  \"exp_msgs\": {},\n  \"fame_msgs\": {},\n{}\n}}\n",
        start_ms,
        util::now_unix_ms(),
        dur,
        stats.frames,
        stats.s2c_frames,
        stats.c2s_frames,
        stats.msgs.gold,
        stats.msgs.exp,
        stats.msgs.fame,
        stats.msgs.gold_msgs,
        stats.msgs.exp_msgs,
        stats.msgs.fame_msgs,
        agg.summary_fields()
    );
    let _ = fs::write(format!("{}\\summary.json", out_dir), summary);
    if !json_mode {
        println!("[listen] 세션 저장: {}", out_dir);
    }
    0
}

/// `--port` 값들을 파싱한다. 단일 값("45017")과 범위("45000..45100") 모두 지원.
fn parse_ports(values: &[&str]) -> Vec<u16> {
    let mut ports = Vec::new();
    for s in values {
        if let Some((lo, hi)) = s.split_once("..") {
            if let (Ok(lo), Ok(hi)) = (lo.trim().parse::<u16>(), hi.trim().parse::<u16>()) {
                ports.extend(lo..=hi);
            }
        } else if let Ok(p) = s.parse::<u16>() {
            ports.push(p);
        }
    }
    ports.sort_unstable();
    ports.dedup();
    ports
}

#[cfg(test)]
mod tests {
    use super::parse_ports;

    #[test]
    fn port_ranges_expand_and_dedup() {
        let p = parse_ports(&["45000..45002", "45001", "5010"]);
        assert_eq!(p, vec![5010, 45000, 45001, 45002]);
    }

    #[test]
    fn invalid_ports_are_ignored() {
        assert!(parse_ports(&["abc", "70000", "45.."]).is_empty());
    }
}
