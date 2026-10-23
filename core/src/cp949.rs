#![allow(dead_code)]

use crate::cp949_table::CP949_TABLE;

/// Decode CP949/UHC bytes to String (invalid bytes -> U+FFFD).
pub fn decode(data: &[u8]) -> String {
    let mut out = String::with_capacity(data.len());
    let mut i = 0usize;
    while i < data.len() {
        let b = data[i];
        if b < 0x80 {
            out.push(b as char);
            i += 1;
            continue;
        }
        if i + 1 < data.len() {
            let code = ((b as u16) << 8) | data[i + 1] as u16;
            if let Ok(idx) = CP949_TABLE.binary_search_by_key(&code, |e| e.0) {
                if let Some(c) = char::from_u32(CP949_TABLE[idx].1) {
                    out.push(c);
                    i += 2;
                    continue;
                }
            }
        }
        out.push('\u{FFFD}');
        i += 1;
    }
    out
}

pub fn has_hangul(s: &str) -> bool {
    s.chars().any(|c| ('\u{AC00}'..='\u{D7A3}').contains(&c))
}

/// CP949 디코딩 결과가 실제 한국어/ASCII 텍스트로 보이는지 판정한다.
/// 키 오동기화(모지바케) 문자열은 U+FFFD 나 CP949 가 만들 수 없는 문자
/// (아랍/키릴/라틴 확장 등)를 포함하므로 걸러낸다.
pub fn is_plausible_text(s: &str) -> bool {
    let mut n = 0usize;
    for c in s.chars() {
        n += 1;
        let u = c as u32;
        let ok = u < 0x80                       // ASCII
            || (0xA0..=0xFF).contains(&u)       // Latin-1 기호 (°, ± 등)
            || (0x2010..=0x27BF).contains(&u)   // 구두점/기호/딩벳 (…, ★, ♥, ▶)
            || (0x3000..=0x30FF).contains(&u)   // CJK 기호/가나
            || (0x3131..=0xD7A3).contains(&u)   // 한글 자모/음절
            || (0x4E00..=0x9FFF).contains(&u)   // 한자
            || (0xF900..=0xFAFF).contains(&u)   // 호환 한자
            || (0xFF00..=0xFFEF).contains(&u);  // 전각 (U+FFFD 는 여기 없음)
        if !ok {
            return false;
        }
    }
    n > 0
}

/// Collapse control chars into single spaces and drop replacement chars.
pub fn clean(s: &str) -> String {
    let mut out = String::with_capacity(s.len());
    let mut last_ws = false;
    for c in s.chars() {
        if (c as u32) < 0x20 || c == '\u{7f}' {
            if !last_ws {
                out.push(' ');
                last_ws = true;
            }
            continue;
        }
        if c == '\u{FFFD}' {
            continue;
        }
        out.push(c);
        last_ws = c == ' ';
    }
    out.trim().to_string()
}
