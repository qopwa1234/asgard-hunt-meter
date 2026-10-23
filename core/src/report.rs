#![allow(dead_code)]

use std::collections::HashMap;
use std::fs;
use std::path::Path;

use crate::cp949;
use crate::frame::Frame;

pub struct Sample {
    pub ctr: u8,
    pub clr: bool,
    pub len: usize,
    pub hex: Vec<u8>, // first <=160 bytes
}

pub struct OpStat {
    pub op: u8,
    pub n: u64,
    pub lens: HashMap<usize, u64>,
    pub samples: Vec<Sample>,
}

pub struct Stats {
    pub frames: u64,
    pub bytes: u64,
    pub order: Vec<u8>,
    pub map: HashMap<u8, OpStat>,
}

impl Stats {
    pub fn new() -> Self {
        Stats {
            frames: 0,
            bytes: 0,
            order: Vec::new(),
            map: HashMap::new(),
        }
    }

    pub fn add(&mut self, f: &Frame) {
        self.frames += 1;
        self.bytes += f.data.len() as u64;
        if !self.map.contains_key(&f.op) {
            self.order.push(f.op);
            self.map.insert(
                f.op,
                OpStat {
                    op: f.op,
                    n: 0,
                    lens: HashMap::new(),
                    samples: Vec::new(),
                },
            );
        }
        let e = self.map.get_mut(&f.op).unwrap();
        e.n += 1;
        *e.lens.entry(f.data.len()).or_insert(0) += 1;
        if e.samples.len() < 4 {
            let take = f.data.len().min(160);
            e.samples.push(Sample {
                ctr: f.ctr,
                clr: f.clr,
                len: f.data.len(),
                hex: f.data[..take].to_vec(),
            });
        }
    }

    pub fn sorted(&self) -> Vec<&OpStat> {
        let mut v: Vec<&OpStat> = self.order.iter().map(|o| self.map.get(o).unwrap()).collect();
        v.sort_by(|a, b| b.n.cmp(&a.n));
        v
    }

    pub fn top_lens(st: &OpStat) -> String {
        let mut lens: Vec<(usize, u64)> = st.lens.iter().map(|(k, v)| (*k, *v)).collect();
        lens.sort_by(|a, b| b.1.cmp(&a.1));
        lens.iter()
            .take(5)
            .map(|(l, c)| format!("{}x{}", l, c))
            .collect::<Vec<_>>()
            .join(" ")
    }

    pub fn write_opstats<P: AsRef<Path>>(&self, p: P) -> std::io::Result<()> {
        let mut lines = vec!["op n topLens".to_string()];
        for st in self.sorted() {
            lines.push(format!("0x{:02x} {} {}", st.op, st.n, Self::top_lens(st)));
        }
        fs::write(p, lines.join("\n"))
    }

    pub fn write_samples<P: AsRef<Path>>(&self, p: P) -> std::io::Result<()> {
        let mut lines: Vec<String> = Vec::new();
        for st in self.sorted().iter().take(40) {
            lines.push(format!(
                "== op 0x{:02x} n={} {}",
                st.op,
                st.n,
                Self::top_lens(st)
            ));
            for s in &st.samples {
                lines.push(format!(
                    "   c={}{} len={} hex={}",
                    s.ctr,
                    if s.clr { " CLR" } else { "" },
                    s.len,
                    crate::util::hex_short(&s.hex, 160)
                ));
                lines.push(format!("   asc={}", crate::util::printable_asc(&s.hex, 160)));
            }
        }
        fs::write(p, lines.join("\n"))
    }
}

/// ASCII string extraction (same rules as the reference decoder).
pub fn extract_strings(op: u8, idx: usize, data: &[u8], lines: &mut Vec<String>, cap: usize) {
    if lines.len() >= cap {
        return;
    }
    let mut i = 0usize;
    while i < data.len() && lines.len() < cap {
        if (0x20..0x7f).contains(&data[i]) {
            let mut j = i;
            while j < data.len() && (0x20..0x7f).contains(&data[j]) {
                j += 1;
            }
            if j - i >= 4 {
                let s: String = data[i..j].iter().map(|&b| b as char).collect();
                if !is_repeat(&s) {
                    lines.push(format!("0x{:02x} #{} @{} {}", op, idx, i, s));
                }
            }
            i = j;
        } else {
            i += 1;
        }
    }
}

fn is_repeat(s: &str) -> bool {
    let mut it = s.chars();
    match it.next() {
        None => true,
        Some(first) => it.all(|c| c == first),
    }
}

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum MsgKind {
    Gold,
    Exp,
    Fame,
    Item,
    Chat,
}

pub struct MsgStats {
    pub gold: u64,
    pub exp: u64,
    pub fame: u64,
    pub gold_msgs: u64,
    pub exp_msgs: u64,
    pub fame_msgs: u64,
}

impl MsgStats {
    pub fn new() -> Self {
        MsgStats {
            gold: 0,
            exp: 0,
            fame: 0,
            gold_msgs: 0,
            exp_msgs: 0,
            fame_msgs: 0,
        }
    }
}

/// Extract gold/exp/fame/item/chat events from message-like opcodes (0x0a/0x92/0x0d).
pub fn scan_msg(op: u8, data: &[u8], st: &mut MsgStats) -> Option<(MsgKind, u64, String)> {
    if !matches!(op, 0x0a | 0x92 | 0x0d) {
        return None;
    }
    let text = cp949::clean(&cp949::decode(data));
    if text.len() < 2 {
        return None;
    }
    let gold_tok = "글로드 획득했습니다";
    if text.contains(gold_tok) {
        if let Some(n) = num_before(&text, gold_tok) {
            st.gold += n;
            st.gold_msgs += 1;
            return Some((MsgKind::Gold, n, text));
        }
    }
    if let Some(n) = amount_after(&text, "xp포인트 획득") {
        st.exp += n;
        st.exp_msgs += 1;
        return Some((MsgKind::Exp, n, text));
    }
    if let Some(n) = amount_after(&text, "명성 경험치 획득") {
        st.fame += n;
        st.fame_msgs += 1;
        return Some((MsgKind::Fame, n, text));
    }
    if op == 0x0d {
        return chat_text(data).map(|t| (MsgKind::Chat, 0, t));
    }
    if text.contains("획득하") || text.contains("획득하였습니다") {
        return Some((MsgKind::Item, 0, text));
    }
    None
}

/// SSayPacket (0x0d) payload: [type:1] [ids/header] [cp949 text .. 00 00].
/// Skip the small binary header, then keep only plausible text.
fn chat_text(data: &[u8]) -> Option<String> {
    if data.is_empty() {
        return None;
    }
    let off = match data[0] {
        0x02 => 9,
        0x06 => 10,
        _ => 0,
    }
    .min(data.len());
    let raw = cp949::decode(&data[off..]);
    if !cp949::is_plausible_text(&raw) {
        return None; // 키 오동기화(모지바케) 프레임 방지
    }
    let text = cp949::clean(&raw);
    let hangul = text
        .chars()
        .filter(|c| ('\u{AC00}'..='\u{D7A3}').contains(c))
        .count();
    let alnum = text.chars().filter(|c| c.is_ascii_alphanumeric()).count();
    if hangul >= 2 || alnum >= 3 {
        Some(text)
    } else {
        None
    }
}

fn num_before(text: &str, token: &str) -> Option<u64> {
    let pos = text.find(token)?;
    let prefix = &text[..pos];
    let mut digits = String::new();
    for c in prefix.chars().rev() {
        if c == ' ' && digits.is_empty() {
            continue;
        }
        if c.is_ascii_digit() || c == ',' {
            digits.push(c);
        } else {
            break;
        }
    }
    let s: String = digits.chars().rev().filter(|c| *c != ',').collect();
    if s.is_empty() {
        None
    } else {
        s.parse().ok()
    }
}

fn amount_after(text: &str, token: &str) -> Option<u64> {
    let pos = text.find(token)?;
    let after = &text[pos + token.len()..];
    let mut digits = String::new();
    for c in after.chars() {
        if c.is_ascii_digit() || c == ',' {
            digits.push(c);
        } else if c == ':' || c == ' ' {
            if digits.is_empty() {
                continue;
            }
            break;
        } else if digits.is_empty() {
            break;
        } else {
            break;
        }
    }
    let s: String = digits.chars().filter(|c| *c != ',').collect();
    if s.is_empty() {
        None
    } else {
        s.parse().ok()
    }
}

pub fn write_replay_outputs(
    out_dir: &str,
    label: &str,
    stats: &Stats,
    decoded: &[u8],
    csv_lines: &[String],
    strings: &[String],
) -> std::io::Result<()> {
    fs::create_dir_all(out_dir)?;
    let p = |suffix: &str| format!("{}\\{}_{}", out_dir, label, suffix);
    fs::write(p("decoded.bin"), decoded)?;
    fs::write(p("frames.csv"), csv_lines.join("\n"))?;
    stats.write_opstats(p("opstats.txt"))?;
    stats.write_samples(p("samples.txt"))?;
    fs::write(p("strings.txt"), strings.join("\n"))?;
    Ok(())
}

pub fn json_event_line(ts: &str, kind: &str, amount: u64, text: &str) -> String {
    format!(
        "{{\"t\":\"{}\",\"kind\":\"{}\",\"amount\":{},\"text\":\"{}\"}}",
        ts,
        kind,
        amount,
        crate::util::json_escape(text)
    )
}
