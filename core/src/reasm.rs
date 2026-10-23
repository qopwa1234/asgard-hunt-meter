#![allow(dead_code)]

use std::collections::BTreeMap;

const OOO_MAX_BYTES: usize = 4 * 1024 * 1024;
/// Max hole size to zero-fill when a segment was never captured.
/// Larger holes are skipped (stream jumps forward).
const GAP_FILL_MAX: usize = 1 << 20;

/// Minimal single-direction TCP stream reassembler:
/// handles SYN start, retransmissions, overlaps and out-of-order segments.
/// Capture holes (lost packets) are zero-filled so the stream keeps flowing
/// (matches the reference tool's dense-stream semantics).
pub struct Reasm {
    started: bool,
    expect: u32,
    ooo: BTreeMap<u32, Vec<u8>>,
    ooo_bytes: usize,
    pub dups: u64,
    pub ooo_segs: u64,
    pub lost_bytes: u64,
}

impl Reasm {
    pub fn new() -> Self {
        Reasm {
            started: false,
            expect: 0,
            ooo: BTreeMap::new(),
            ooo_bytes: 0,
            dups: 0,
            ooo_segs: 0,
            lost_bytes: 0,
        }
    }

    pub fn push<F: FnMut(&[u8])>(&mut self, seq_in: u32, flags: u8, payload: &[u8], emit: &mut F) {
        let syn = flags & 0x02 != 0 && flags & 0x10 == 0;
        let mut seq = seq_in;
        if syn {
            self.started = true;
            self.expect = seq.wrapping_add(1);
            self.ooo.clear();
            self.ooo_bytes = 0;
            seq = self.expect;
        } else if !self.started {
            self.started = true;
            self.expect = seq;
        }
        if !payload.is_empty() {
            let diff = seq.wrapping_sub(self.expect) as i32;
            if diff == 0 {
                self.consume(payload, emit);
            } else if diff < 0 {
                let off = (-diff) as usize;
                if off < payload.len() {
                    self.consume(&payload[off..], emit);
                } else {
                    self.dups += 1;
                }
            } else {
                if self.ooo_bytes + payload.len() > OOO_MAX_BYTES {
                    self.ooo.clear();
                    self.ooo_bytes = 0;
                }
                self.ooo_segs += 1;
                self.ooo_bytes += payload.len();
                self.ooo.insert(seq, payload.to_vec());
            }
            loop {
                let ex = self.expect;
                match self.ooo.keys().next().copied() {
                    None => break,
                    Some(s) => {
                        let d = s.wrapping_sub(ex) as i32;
                        if d > 0 {
                            // hole in captured data: zero-fill (bounded) or jump
                            let gap = d as usize;
                            self.lost_bytes += gap as u64;
                            if gap <= GAP_FILL_MAX {
                                let zeros = vec![0u8; gap];
                                self.consume(&zeros, emit);
                            } else {
                                self.expect = s;
                            }
                        } else {
                            let buf = self.ooo.remove(&s).unwrap();
                            self.ooo_bytes = self.ooo_bytes.saturating_sub(buf.len());
                            let off = (-d) as usize;
                            if off < buf.len() {
                                self.consume(&buf[off..], emit);
                            } else {
                                self.dups += 1;
                            }
                        }
                    }
                }
            }
        }
    }

    fn consume<F: FnMut(&[u8])>(&mut self, bytes: &[u8], emit: &mut F) {
        emit(bytes);
        self.expect = self.expect.wrapping_add(bytes.len() as u32);
    }
}
