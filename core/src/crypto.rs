#![allow(dead_code)]

//! Asgard stream cipher (mode 7), reconstructed 2026-10-16.
//!
//! Frame: AA | len:u16 BE | payload[len]
//!   payload[0] = opcode (plaintext), payload[1] = counter (cipher frames only)
//!   ciphertext starts at payload[2].
//!
//! plain[p] = wire[p] ^ M7(ctr) ^ (j != ctr ? M7(j) : 0) ^ K4[((p>>2)%9)*4 + (p&3)]
//!   j = floor(p/9) & 0xFF,  K4 = key repeated x4 (36 bytes)
//!
//! Session key is delivered in the clear by op 0x10 on new connections:
//!   payload = 02 07 09 <key 9 bytes> <unknown 9 bytes>

pub const KEY_DEFAULT: [u8; 9] = [0x21, 0x23, 0x35, 0x63, 0x63, 0x52, 0x5e, 0x51, 0x44];
pub const W: usize = 9;

const fn build_m7() -> [u8; 256] {
    let mut t = [0u8; 256];
    let mut i = 0usize;
    while i < 256 {
        t[i] = if i <= 0x7f {
            (0xff - 2 * i as u16) as u8
        } else {
            (2 * i as u16) as u8
        };
        i += 1;
    }
    t
}

pub static M7: [u8; 256] = build_m7();

#[derive(Clone, Copy)]
pub struct Codec {
    pub key: [u8; 9],
    /// true = use `ctr & 0x7F` as M7 index (high bit is a flag, 2026-10-19 server
    /// behavior); false = full byte (reference captures up to 2026-10-18).
    pub ctr_half: bool,
    k4: [u8; 36],
}

impl Codec {
    pub fn new() -> Self {
        Self::with_key(KEY_DEFAULT)
    }

    pub fn with_key(key: [u8; 9]) -> Self {
        let mut c = Codec {
            key,
            ctr_half: false,
            k4: [0u8; 36],
        };
        c.rebuild();
        c
    }

    fn rebuild(&mut self) {
        let mut i = 0usize;
        while i < 4 {
            let mut j = 0usize;
            while j < W {
                self.k4[i * W + j] = self.key[j];
                j += 1;
            }
            i += 1;
        }
    }

    pub fn set_key(&mut self, key: [u8; 9]) {
        if key != self.key {
            self.key = key;
            self.rebuild();
        }
    }

    pub fn set_ctr_half(&mut self, half: bool) {
        self.ctr_half = half;
    }

    pub fn decrypt_into(&self, ctr: u8, wire: &[u8], out: &mut Vec<u8>) {
        let c = if self.ctr_half { ctr & 0x7f } else { ctr };
        let mc = M7[c as usize];
        out.reserve(wire.len());
        for (p, &w) in wire.iter().enumerate() {
            let j = ((p / W) & 0xff) as u8;
            let k = self.k4[((p >> 2) % W) * 4 + (p & 3)];
            let mj = if j != c { M7[j as usize] } else { 0 };
            out.push(w ^ mc ^ mj ^ k);
        }
    }
}
