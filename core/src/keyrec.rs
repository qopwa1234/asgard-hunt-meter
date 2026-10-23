#![allow(dead_code)]

//! Session-key auto-recovery for mid-session attach.
//!
//! The frame cipher uses the 9-byte session key only through `KEY[q % 9]`
//! (q = index inside the ciphertext part `payload[2..]`). When the codec runs
//! with key `K0` but the stream actually uses `K`, every decoded byte becomes
//! `d'[q] = plain[q] ^ X[q % 9]` for the constant correction `X = K0 ^ K`.
//! Recovering `X` from known plaintext (CP949 message anchors) therefore
//! recovers the session key: `K = K0 ^ X`.
//!
//! Method: vote all anchor substrings over all message frames, refine with
//! mismatch-tolerant near-match alignments, then validate exact long-anchor
//! hits. Only accept when validation passes (exact >=10-byte anchor matches
//! cannot be produced by chance, and a >=10-byte span covers all 9 residues,
//! so any wrong residue would break the match).

use crate::crypto::{Codec, KEY_DEFAULT};

/// Minimum number of 0x0a message frames before attempting recovery.
pub const MIN_VOTE_FRAMES: usize = 20;
const MAX_BUFFER_FRAMES: usize = 400;
const MAX_FRAME_LEN: usize = 4096;

/// CP949 byte sequences used as known plaintext.
/// (gold/exp/fame loot lines, item lines and common system-message suffixes)
pub const ANCHORS: [&[u8]; 19] = [
    b"\xb1\xdb\xb7\xce\xb5\xe5\x20\xc8\xb9\xb5\xe6\xc7\xdf\xbd\xc0\xb4\xcf\xb4\xd9", // "글로드 획득했습니다"
    b"\xc8\xb9\xb5\xe6\xc7\xdf\xbd\xc0\xb4\xcf\xb4\xd9", // "획득했습니다"
    b"\xb1\xdb\xb7\xce\xb5\xe5",                         // "글로드"
    b"xp\xc6\xf7\xc0\xce\xc6\xae\x20\xc8\xb9\xb5\xe6\x3a\x20", // "xp포인트 획득: "
    b"xp\xc6\xf7\xc0\xce\xc6\xae",                       // "xp포인트"
    b"\xb8\xed\xbc\xba\x20\xb0\xe6\xc7\xe8\xc4\xa1\x20\xc8\xb9\xb5\xe6\x3a\x20", // "명성 경험치 획득: "
    b"\xb8\xed\xbc\xba\x20\xb0\xe6\xc7\xe8\xc4\xa1",     // "명성 경험치"
    b"\xc8\xb9\xb5\xe6\xc7\xcf\xbf\xb4\xbd\xc0\xb4\xcf\xb4\xd9", // "획득하였습니다"
    b"\xb5\xc7\xbe\xfa\xbd\xc0\xb4\xcf\xb4\xd9",         // "되었습니다"
    b"\xc1\xf5\xb0\xa1\xb5\xc7\xbe\xfa\xbd\xc0\xb4\xcf\xb4\xd9", // "증가되었습니다"
    b"\xb0\xa8\xbc\xd2\xb5\xc7\xbe\xfa\xbd\xc0\xb4\xcf\xb4\xd9", // "감소되었습니다"
    b"\xc7\xd8\xc1\xa6\xb5\xcb\xb4\xcf\xb4\xd9",         // "해제됩니다"
    b"\xbb\xf3\xc5\xc2\xb0\xa1\x20\xb5\xc7\xbe\xfa\xbd\xc0\xb4\xcf\xb4\xd9", // "상태가 되었습니다"
    // Common town/buff stat lines. They arrive frequently even without loot,
    // so mid-session key recovery can start before the first gold/exp message.
    // Fixed buff-notice prefix: "!\0G{=c" / "!\0J{=c" (length 0x47/0x4a).
    b"\x21\x00\x47\x7b\x3d\x63\x66\x66\x66", // buff notice, short
    b"\x21\x00\x4a\x7b\x3d\x63\x66\x66\x66", // buff notice, long
    b"\xb9\xb0\xb8\xae\xb0\xf8\xb0\xdd\x20\xb8\xed\xc1\xdf\xb7\xfc", // physical hit
    b"\xb8\xb6\xb9\xfd\xb0\xf8\xb0\xdd\x20\xb8\xed\xc1\xdf\xb7\xfc", // magic hit
    b"\xb9\xb0\xb8\xae\x20\xc5\xa9\xb8\xae\xc6\xbc\xc4\xc3\x20\xc8\xae\xb7\xfc", // physical crit
    b"\xb8\xb6\xb9\xfd\x20\xc5\xa9\xb8\xae\xc6\xbc\xc4\xc3\x20\xc8\xae\xb7\xfc", // magic crit
];

pub struct RecOutcome {
    /// correction mask: plain[q] = decoded[q] ^ x[q % 9]
    pub x: [u8; 9],
    /// recovered session key = base ^ x
    pub key: [u8; 9],
    pub frames: usize,
    /// frames containing at least one validated (>=10 byte) anchor
    pub hits: u32,
    pub anchors_hit: u32,
    pub refined_changes: u32,
}

pub fn xor9(a: [u8; 9], b: [u8; 9]) -> [u8; 9] {
    let mut r = [0u8; 9];
    for i in 0..9 {
        r[i] = a[i] ^ b[i];
    }
    r
}

pub fn apply_x(data: &[u8], x: &[u8; 9]) -> Vec<u8> {
    data.iter()
        .enumerate()
        .map(|(q, &b)| b ^ x[q % 9])
        .collect()
}

fn contains(hay: &[u8], needle: &[u8]) -> bool {
    hay.len() >= needle.len() && hay.windows(needle.len()).any(|w| w == needle)
}

fn top3(v: &[u32; 256]) -> [(u8, u32); 3] {
    let mut t = [(0u8, 0u32); 3];
    for (i, &c) in v.iter().enumerate() {
        if c > t[0].1 {
            t[2] = t[1];
            t[1] = t[0];
            t[0] = (i as u8, c);
        } else if c > t[1].1 {
            t[2] = t[1];
            t[1] = (i as u8, c);
        } else if c > t[2].1 {
            t[2] = (i as u8, c);
        }
    }
    t
}

/// Known-plaintext vote: for every frame, anchor and offset, vote
/// `decoded[q] ^ anchor_byte` for residue `q % 9`.
pub fn vote(frames: &[&[u8]]) -> ([u8; 9], [[(u8, u32); 3]; 9]) {
    let mut votes = vec![[0u32; 256]; 9];
    for d in frames {
        for s in ANCHORS.iter() {
            let l = s.len();
            if d.len() < l {
                continue;
            }
            for o in 0..=(d.len() - l) {
                for i in 0..l {
                    let q = o + i;
                    votes[q % 9][(d[q] ^ s[i]) as usize] += 1;
                }
            }
        }
    }
    let mut x = [0u8; 9];
    let mut tops = [[(0u8, 0u32); 3]; 9];
    for r in 0..9 {
        tops[r] = top3(&votes[r]);
        x[r] = tops[r][0].0;
    }
    (x, tops)
}

/// Mismatch-tolerant refinement: collect corrections from near-match
/// alignments (allowing a few mismatches so alignments still work while some
/// residues of `x0` are wrong), then take the per-residue mode.
pub fn refine(frames: &[&[u8]], x0: [u8; 9]) -> ([u8; 9], u32) {
    let mut samp = vec![[0u32; 256]; 9];
    let mut samp_n = [0u32; 9];
    for d in frames {
        for s in ANCHORS.iter() {
            let l = s.len();
            if l < 8 || d.len() < l {
                continue;
            }
            let allow = if l >= 16 {
                3
            } else if l >= 10 {
                1
            } else {
                0
            };
            for o in 0..=(d.len() - l) {
                let mut mism = 0usize;
                for i in 0..l {
                    let q = o + i;
                    if d[q] ^ x0[q % 9] != s[i] {
                        mism += 1;
                        if mism > allow {
                            break;
                        }
                    }
                }
                if mism <= allow {
                    for i in 0..l {
                        let q = o + i;
                        samp[q % 9][(d[q] ^ s[i]) as usize] += 1;
                        samp_n[q % 9] += 1;
                    }
                }
            }
        }
    }
    let mut x = x0;
    let mut changes = 0u32;
    for r in 0..9 {
        if samp_n[r] >= 4 {
            let t = top3(&samp[r]);
            if t[0].0 != x[r] {
                changes += 1;
            }
            x[r] = t[0].0;
        }
    }
    (x, changes)
}

/// Validation: count frames that contain an exact >=10-byte anchor after
/// applying `x`. Returns (frames_with_any_hit, total_anchor_pairs).
pub fn validate(frames: &[&[u8]], x: &[u8; 9]) -> (u32, u32) {
    let mut hits = 0u32;
    let mut anch = 0u32;
    for d in frames {
        let corr = apply_x(d, x);
        let mut hit = false;
        for s in ANCHORS.iter().filter(|s| s.len() >= 10) {
            if contains(&corr, s) {
                anch += 1;
                hit = true;
            }
        }
        if hit {
            hits += 1;
        }
    }
    (hits, anch)
}

/// Full recovery: vote -> refine -> validate.
pub fn recover(base: [u8; 9], frames: &[(u8, Vec<u8>)]) -> Option<RecOutcome> {
    let msg: Vec<&[u8]> = frames
        .iter()
        .filter(|(op, d)| *op == 0x0a && d.len() >= 14)
        .map(|(_, d)| d.as_slice())
        .collect();
    if msg.len() < MIN_VOTE_FRAMES {
        return None;
    }
    let (x1, _tops) = vote(&msg);
    let (x2, changes) = refine(&msg, x1);
    let (hits, anchors_hit) = validate(&msg, &x2);
    if hits >= 2 {
        Some(RecOutcome {
            x: x2,
            key: xor9(base, x2),
            frames: msg.len(),
            hits,
            anchors_hit,
            refined_changes: changes,
        })
    } else {
        None
    }
}

/// Read a frame dump file ([op:1][len:u16 LE][data]) - same format the
/// listener writes to frames_s2c.bin / frames_c2s.bin.
pub fn read_frames_file(path: &str) -> std::io::Result<Vec<(u8, Vec<u8>)>> {
    let data = std::fs::read(path)?;
    let mut out = Vec::new();
    let mut i = 0usize;
    while i + 3 <= data.len() {
        let op = data[i];
        let len = u16::from_le_bytes([data[i + 1], data[i + 2]]) as usize;
        if i + 3 + len > data.len() {
            break;
        }
        out.push((op, data[i + 3..i + 3 + len].to_vec()));
        i += 3 + len;
    }
    Ok(out)
}

/// Per-connection frame buffer used by the live listener's adaptive decoder.
/// Stores message frames together with their raw ctr byte and the codec
/// configuration (key + ctr mode) that was used when they were decoded.
pub struct RecoverBuf {
    pub base_key: [u8; 9],
    pub base_half: bool,
    pub frames: Vec<(u8, u8, Vec<u8>)>,
    pub reported: bool,
    pub last_try_len: usize,
}

impl RecoverBuf {
    pub fn new(base_key: [u8; 9], base_half: bool) -> Self {
        RecoverBuf {
            base_key,
            base_half,
            frames: Vec::new(),
            reported: false,
            last_try_len: 0,
        }
    }

    pub fn reset(&mut self, base_key: [u8; 9], base_half: bool) {
        self.base_key = base_key;
        self.base_half = base_half;
        self.frames.clear();
        self.reported = false;
        self.last_try_len = 0;
    }

    pub fn push(&mut self, op: u8, ctr: u8, data: &[u8]) {
        if self.frames.len() >= MAX_BUFFER_FRAMES {
            let keep = MAX_BUFFER_FRAMES / 2;
            let drop = self.frames.len() - keep;
            self.frames.drain(..drop);
            self.last_try_len = self.frames.len().saturating_sub(12);
        }
        if op == 0x0a && data.len() >= 14 && data.len() <= MAX_FRAME_LEN {
            self.frames.push((op, ctr, data.to_vec()));
        }
    }

    pub fn ready(&self) -> bool {
        self.frames.len() >= MIN_VOTE_FRAMES && self.frames.len() >= self.last_try_len + 12
    }

    pub fn mark_tried(&mut self) {
        self.last_try_len = self.frames.len();
    }
}

/// Apply a recovered key to one connection's codec (helper for the listener).
pub fn apply_key(codec: &mut Codec, key: [u8; 9]) {
    codec.set_key(key);
}

pub const FF9: [u8; 9] = [0xff; 9];

/// K4 slot used for position `p` (the cipher's K4 holds key[j] at slot i*9+j).
fn k4_byte(key: &[u8; 9], p: usize) -> u8 {
    let n = ((p >> 2) % 9) * 4 + (p & 3);
    key[n % 9]
}

/// Re-interpret bytes decoded with (base_key, base_half) as if they had been
/// decoded with (key2, half2). Pure byte transform driven by the raw ctr.
fn transform(
    stored: &[u8],
    ctr: u8,
    base_key: [u8; 9],
    base_half: bool,
    key2: [u8; 9],
    half2: bool,
) -> Vec<u8> {
    use crate::crypto::M7;
    let c1 = if base_half { ctr & 0x7f } else { ctr };
    let c2 = if half2 { ctr & 0x7f } else { ctr };
    let m1 = M7[c1 as usize];
    let m2 = M7[c2 as usize];
    stored
        .iter()
        .enumerate()
        .map(|(p, &b)| {
            let j = ((p / 9) & 0xff) as u8;
            let t1 = if j != c1 { M7[j as usize] } else { 0 };
            let t2 = if j != c2 { M7[j as usize] } else { 0 };
            b ^ m1 ^ m2 ^ t1 ^ t2 ^ k4_byte(&base_key, p) ^ k4_byte(&key2, p)
        })
        .collect()
}

/// Score one decoding hypothesis over the stored frames:
/// (frames containing an exact >=10 byte anchor, total text score)
fn eval(
    frames: &[(u8, u8, Vec<u8>)],
    base_key: [u8; 9],
    base_half: bool,
    key2: [u8; 9],
    half2: bool,
) -> (u32, i64) {
    let mut hit_frames = 0u32;
    let mut score = 0i64;
    for (_, ctr, d) in frames {
        let out = transform(d, *ctr, base_key, base_half, key2, half2);
        let mut hit = false;
        for s in ANCHORS.iter().filter(|s| s.len() >= 10) {
            if contains(&out, s) {
                hit = true;
                break;
            }
        }
        if hit {
            hit_frames += 1;
            score += 40;
        }
        let t = crate::cp949::decode(&out);
        let hangul = t
            .chars()
            .filter(|c| ('\u{ac00}'..='\u{d7a3}').contains(c))
            .count() as i64;
        let repl = t.matches('\u{fffd}').count() as i64;
        score += 2 * hangul - 4 * repl;
    }
    (hit_frames, score)
}

/// Adaptive decision: given a window of message frames that were decoded with
/// (base_key, base_half), find the (key, ctr mode) pair that best explains the
/// stream. Handles both server behaviors (full byte ctr / low-7-bit ctr) and
/// the key-complement ambiguity.
pub fn decide(
    base_key: [u8; 9],
    base_half: bool,
    frames: &[(u8, u8, Vec<u8>)],
) -> Option<Applied> {
    if frames.len() < MIN_VOTE_FRAMES {
        return None;
    }
    // 기준 설정(현재 코덱)을 후보로 시작한다. ctr 모드가 틀리면 ctr>=0x80 절반만
    // 깨지므로 "hits >= 2" 조기 수용은 금지 — 항상 대안과 비교해서 고른다.
    let (hit0, score0) = eval(frames, base_key, base_half, base_key, base_half);
    let mut best = Some(Applied {
        key: base_key,
        ctr_half: base_half,
        frames: frames.len(),
        hit_frames: hit0,
        score: score0,
    });
    // vote for correction masks: whole window, then low-ctr / high-ctr subsets
    // (the two subsets can belong to different hypothesis families)
    let plain: Vec<&[u8]> = frames.iter().map(|(_, _, d)| d.as_slice()).collect();
    let mut lo: Vec<&[u8]> = Vec::new();
    let mut hi: Vec<&[u8]> = Vec::new();
    for (_, ctr, d) in frames {
        if *ctr < 0x80 {
            lo.push(d.as_slice());
        } else {
            hi.push(d.as_slice());
        }
    }
    let mut masks: Vec<[u8; 9]> = Vec::new();
    {
        let (x1, _) = vote(&plain);
        let (x2, _) = refine(&plain, x1);
        let (hx, _) = validate(&plain, &x2);
        if hx >= 2 {
            masks.push(x2);
        }
    }
    for sub in [&lo, &hi] {
        if sub.len() >= MIN_VOTE_FRAMES {
            let (x1, _) = vote(sub);
            let (x2, _) = refine(sub, x1);
            let (hx, _) = validate(sub, &x2);
            if hx >= 2 && !masks.contains(&x2) {
                masks.push(x2);
            }
        }
    }
    // candidate hypotheses: ctr-mode flip of the base key, then mask-derived
    // key = base ^ mask (and its complement), ctr full/low7
    let mut cands: Vec<([u8; 9], bool)> = vec![(base_key, !base_half)];
    for x in &masks {
        let kx = xor9(base_key, *x);
        let ky = xor9(kx, FF9);
        for k in [kx, ky] {
            for h in [true, false] {
                if !cands.contains(&(k, h)) {
                    cands.push((k, h));
                }
            }
        }
    }
    for (k, h) in cands {
        let (hf, sc) = eval(frames, base_key, base_half, k, h);
        let better = match &best {
            None => true,
            Some(b) => hf > b.hit_frames || (hf == b.hit_frames && sc > b.score),
        };
        if better {
            best = Some(Applied {
                key: k,
                ctr_half: h,
                frames: frames.len(),
                hit_frames: hf,
                score: sc,
            });
        }
    }
    best.filter(|b| b.hit_frames >= 2)
}

pub struct Applied {
    pub key: [u8; 9],
    pub ctr_half: bool,
    pub frames: usize,
    pub hit_frames: u32,
    pub score: i64,
}

/// `asgard-core recover-key --in <frames.bin> [--key HEX9] [--out DIR]`
pub fn cli_recover(args: &[String]) -> i32 {
    let a = crate::util::parse_args(args);
    let input = match a.get("in").map(|s| s.to_string()).or_else(|| a.pos.first().cloned()) {
        Some(p) => p,
        None => {
            eprintln!("usage: asgard-core recover-key --in <frames.bin> [--key HEX9] [--out DIR]");
            return 1;
        }
    };
    let base = a
        .get("key")
        .and_then(crate::util::parse_key)
        .unwrap_or(KEY_DEFAULT);
    let frames = match read_frames_file(&input) {
        Ok(f) => f,
        Err(e) => {
            eprintln!("[recover] 파일 읽기 실패 {}: {}", input, e);
            return 1;
        }
    };
    let msg: Vec<&[u8]> = frames
        .iter()
        .filter(|(op, d)| *op == 0x0a && d.len() >= 14)
        .map(|(_, d)| d.as_slice())
        .collect();
    println!(
        "[recover] file={} frames={} msgFrames={} base={}",
        input,
        frames.len(),
        msg.len(),
        crate::util::hex_short(&base, 9)
    );
    if msg.len() >= MIN_VOTE_FRAMES {
        let (x, tops) = vote(&msg);
        for (r, t) in tops.iter().enumerate() {
            println!(
                "[recover] vote[{}] {:02x}({}) {:02x}({}) {:02x}({})",
                r, t[0].0, t[0].1, t[1].0, t[1].1, t[2].0, t[2].1
            );
        }
        println!("[recover] vote X = {}", crate::util::hex_short(&x, 9));
    } else {
        println!(
            "[recover] 메시지 프레임 부족 ({} < {})",
            msg.len(),
            MIN_VOTE_FRAMES
        );
    }

    match recover(base, &frames) {
        Some(o) => {
            println!(
                "[recover] OK  x={}  key={}  frames={} hits={} anchorsHit={} refinedChanges={}",
                crate::util::hex_short(&o.x, 9),
                crate::util::hex_short(&o.key, 9),
                o.frames,
                o.hits,
                o.anchors_hit,
                o.refined_changes
            );
            if o.x == [0u8; 9] {
                println!("[recover] => 현재 키가 정확합니다 (보정 불필요)");
            } else {
                println!("[recover] => 키 자동 복원 완료");
            }

            let mut lines: Vec<String> = Vec::new();
            for (op, d) in &frames {
                if !matches!(*op, 0x0a | 0x0d) || d.len() < 8 {
                    continue;
                }
                let c = apply_x(d, &o.x);
                let t = crate::cp949::clean(&crate::cp949::decode(&c));
                if t.chars().count() >= 4 {
                    lines.push(format!("op0x{:02x} | {}", op, t));
                }
            }
            let mut shown = 0;
            for l in &lines {
                println!("  {}", l);
                shown += 1;
                if shown >= 15 {
                    break;
                }
            }

            if let Some(out) = a.get("out") {
                if let Err(e) = std::fs::create_dir_all(out) {
                    eprintln!("[recover] 출력 폴더 생성 실패: {}", e);
                    return 1;
                }
                let mut body = String::new();
                body.push_str(&format!(
                    "x={}\nkey={}\nbase={}\nframes={}\nhits={}\nanchorsHit={}\nrefinedChanges={}\n",
                    crate::util::hex_short(&o.x, 9),
                    crate::util::hex_short(&o.key, 9),
                    crate::util::hex_short(&base, 9),
                    o.frames,
                    o.hits,
                    o.anchors_hit,
                    o.refined_changes
                ));
                body.push_str("\n== corrected messages ==\n");
                body.push_str(&lines.join("\n"));
                body.push('\n');
                let _ = std::fs::write(format!("{}\\recovered.txt", out), body);
                println!("[recover] 저장: {}\\recovered.txt", out);
            }
            0
        }
        None => {
            println!("[recover] 복원 실패 (앵커 부족 또는 검증 실패)");
            2
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn gold_line(n: u64) -> Vec<u8> {
        let digs = n.to_string();
        let mut text = digs.into_bytes();
        text.push(0x20);
        text.extend_from_slice(
            b"\xb1\xdb\xb7\xce\xb5\xe5\x20\xc8\xb9\xb5\xe6\xc7\xdf\xbd\xc0\xb4\xcf\xb4\xd9",
        );
        let mut v = vec![0x19, 0x00, text.len() as u8];
        v.extend_from_slice(&text);
        v
    }

    fn exp_line(n: u64) -> Vec<u8> {
        let digs = n.to_string();
        let mut text = b"xp\xc6\xf7\xc0\xce\xc6\xae\x20\xc8\xb9\xb5\xe6\x3a\x20".to_vec();
        text.extend_from_slice(digs.as_bytes());
        let mut v = vec![0x1a, 0x00, text.len() as u8];
        v.extend_from_slice(&text);
        v
    }

    fn fame_line(n: u64) -> Vec<u8> {
        let digs = n.to_string();
        let mut text =
            b"\xb8\xed\xbc\xba\x20\xb0\xe6\xc7\xe8\xc4\xa1\x20\xc8\xb9\xb5\xe6\x3a\x20".to_vec();
        text.extend_from_slice(digs.as_bytes());
        let mut v = vec![0x1a, 0x00, text.len() as u8];
        v.extend_from_slice(&text);
        v
    }

    fn sample_set() -> Vec<Vec<u8>> {
        let mut v = Vec::new();
        for n in [5u64, 42, 133, 6975, 18443, 999_999] {
            v.push(gold_line(n));
        }
        for n in [87168u64, 1024, 999_999_999] {
            v.push(exp_line(n));
        }
        for n in [278661u64, 1_537_301] {
            v.push(fame_line(n));
        }
        v
    }

    fn corrupt(plain: &[u8], x: &[u8; 9]) -> Vec<u8> {
        plain
            .iter()
            .enumerate()
            .map(|(q, &b)| b ^ x[q % 9])
            .collect()
    }

    #[test]
    fn recovers_synthetic_key() {
        let xr: [u8; 9] = [0x13, 0x57, 0x9b, 0xdf, 0x24, 0x68, 0xac, 0xf1, 0x35];
        let mut frames: Vec<(u8, Vec<u8>)> = Vec::new();
        for _ in 0..4 {
            for p in sample_set() {
                frames.push((0x0a, corrupt(&p, &xr)));
            }
        }
        let out = recover(KEY_DEFAULT, &frames).expect("recover failed");
        assert_eq!(out.x, xr, "correction mismatch");
        assert_eq!(out.key, xor9(KEY_DEFAULT, xr));
        assert!(out.hits >= 2, "validation hits too low: {}", out.hits);
    }

    #[test]
    fn no_correction_when_key_correct() {
        let mut frames: Vec<(u8, Vec<u8>)> = Vec::new();
        for _ in 0..4 {
            for p in sample_set() {
                frames.push((0x0a, p));
            }
        }
        let out = recover(KEY_DEFAULT, &frames).expect("recover failed");
        assert_eq!(out.x, [0u8; 9], "expected zero correction");
    }

    #[test]
    fn refuses_without_enough_frames() {
        let mut frames: Vec<(u8, Vec<u8>)> = Vec::new();
        for p in sample_set() {
            frames.push((0x0a, p));
        }
        assert!(recover(KEY_DEFAULT, &frames).is_none());
    }

    /// Encrypt/decrypt one payload with the full cipher model (XOR is symmetric).
    fn enc_frame(plain: &[u8], key: [u8; 9], half: bool, ctr: u8) -> Vec<u8> {
        use crate::crypto::M7;
        let c = if half { ctr & 0x7f } else { ctr };
        let mc = M7[c as usize];
        plain
            .iter()
            .enumerate()
            .map(|(p, &b)| {
                let j = ((p / 9) & 0xff) as u8;
                let mj = if j != c { M7[j as usize] } else { 0 };
                b ^ mc ^ mj ^ k4_byte(&key, p)
            })
            .collect()
    }

    const CTRS: [u8; 10] = [0x12, 0x95, 0x33, 0xc7, 0x44, 0xa1, 0x08, 0xe3, 0x2b, 0xd0];

    fn window_of(mut fill: impl FnMut(u8, &[u8]) -> Vec<u8>) -> Vec<(u8, u8, Vec<u8>)> {
        let mut frames = Vec::new();
        let mut i = 0usize;
        for _ in 0..4 {
            for p in sample_set() {
                let ctr = CTRS[i % CTRS.len()];
                frames.push((0x0a, ctr, fill(ctr, &p)));
                i += 1;
            }
        }
        frames
    }

    #[test]
    fn decide_keeps_correct_key() {
        let frames = window_of(|_ctr, p| p.to_vec());
        let app = decide(KEY_DEFAULT, false, &frames).expect("decide failed");
        assert_eq!(app.key, KEY_DEFAULT);
        assert!(!app.ctr_half);
        assert!(app.hit_frames >= 2);
    }

    /// New server behavior: ctr index is `ctr & 0x7F`; the listener ran with the
    /// default key and full ctr, so both halves of the stream are garbled (the
    /// low-ctr half looks like one key, the high-ctr half like its complement).
    #[test]
    fn decide_detects_half_ctr_and_key() {
        let x_lo: [u8; 9] = [0x51, 0x13, 0x42, 0x28, 0x34, 0x06, 0x68, 0x0a, 0x0c];
        let k_s = xor9(KEY_DEFAULT, x_lo);
        let frames = window_of(|ctr, p| {
            let wire = enc_frame(p, k_s, true, ctr);
            enc_frame(&wire, KEY_DEFAULT, false, ctr)
        });
        let app = decide(KEY_DEFAULT, false, &frames).expect("decide failed");
        assert_eq!(app.key, k_s, "key mismatch");
        assert!(app.ctr_half, "should detect low7 ctr mode");
        assert!(app.hit_frames >= 2);
    }

    /// Reference-capture behavior: full-byte ctr with a single unknown key.
    #[test]
    fn decide_detects_full_ctr_key() {
        let xr: [u8; 9] = [0x13, 0x57, 0x9b, 0xdf, 0x24, 0x68, 0xac, 0xf1, 0x35];
        let k_s = xor9(KEY_DEFAULT, xr);
        let frames = window_of(|ctr, p| {
            let wire = enc_frame(p, k_s, false, ctr);
            enc_frame(&wire, KEY_DEFAULT, false, ctr)
        });
        let app = decide(KEY_DEFAULT, false, &frames).expect("decide failed");
        assert_eq!(app.key, k_s, "key mismatch");
        assert!(!app.ctr_half, "should keep full ctr mode");
        assert!(app.hit_frames >= 2);
    }

    /// 2026-10-19 서버 low7 동작 + 키는 이미 정확하지만 모드가 full 인 경우:
    /// ctr>=0x80 절반이 비트반전으로 저장되어 있어도 decide() 는 키를 바꾸지
    /// 않고 모드만 low7 로 전환해야 한다 (조기 수용 금지 회귀 테스트).
    #[test]
    fn decide_fixes_ctr_mode_with_correct_key() {
        let frames = window_of(|ctr, p| {
            let wire = enc_frame(p, KEY_DEFAULT, true, ctr);
            enc_frame(&wire, KEY_DEFAULT, false, ctr)
        });
        let app = decide(KEY_DEFAULT, false, &frames).expect("decide failed");
        assert_eq!(app.key, KEY_DEFAULT, "key must not change");
        assert!(app.ctr_half, "should switch to low7 ctr mode");
        assert!(app.hit_frames >= 2);
    }
}
