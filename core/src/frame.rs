#![allow(dead_code)]

use crate::crypto::Codec;

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum Dir {
    C2S,
    S2C,
    Unknown,
}

impl Dir {
    #[inline]
    pub fn is_clr(self, op: u8) -> bool {
        match self {
            Dir::C2S => op & 0xef == 0x00,
            Dir::S2C => op == 0x00 || op == 0x03,
            Dir::Unknown => matches!(op, 0x00 | 0x03 | 0x10),
        }
    }

    pub fn label(self) -> &'static str {
        match self {
            Dir::C2S => "c2s",
            Dir::S2C => "s2c",
            Dir::Unknown => "?",
        }
    }
}

pub struct Frame {
    pub op: u8,
    pub ctr: u8,
    pub clr: bool,
    pub data: Vec<u8>,
}

/// Streaming frame scanner/decoder for one direction of a TCP stream.
/// Frame layout: AA | len:u16 BE | payload[len]; payload[0]=opcode(plain),
/// payload[1]=counter (cipher only), ciphertext at payload[2..].
pub struct FrameStream {
    pub dir: Dir,
    pub buf: Vec<u8>,
    pub resyncs: u64,
    pub frames: u64,
    pub key_updates: u32,
}

impl FrameStream {
    pub fn new(dir: Dir) -> Self {
        FrameStream {
            dir,
            buf: Vec::new(),
            resyncs: 0,
            frames: 0,
            key_updates: 0,
        }
    }

    pub fn push<F: FnMut(Frame)>(&mut self, codec: &mut Codec, bytes: &[u8], emit: &mut F) {
        self.buf.extend_from_slice(bytes);
        let mut pos = 0usize;
        loop {
            let rem = &self.buf[pos..];
            if rem.len() < 3 {
                break;
            }
            if rem[0] != 0xAA {
                match rem[1..].iter().position(|&b| b == 0xAA) {
                    Some(k) => {
                        pos += 1 + k;
                        self.resyncs += 1;
                    }
                    None => {
                        pos = self.buf.len();
                        self.resyncs += 1;
                        break;
                    }
                }
                continue;
            }
            let len = ((rem[1] as usize) << 8) | (rem[2] as usize);
            if len == 0 {
                pos += 1;
                self.resyncs += 1;
                continue;
            }
            if rem.len() < 3 + len {
                break;
            }
            let payload = &rem[3..3 + len];
            pos += 3 + len;
            let op = payload[0];
            let clr = self.dir.is_clr(op);
            if !clr && payload.len() < 2 {
                self.resyncs += 1;
                continue;
            }
            let ctr;
            let mut data;
            if clr {
                ctr = 0u8;
                data = Vec::with_capacity(payload.len().saturating_sub(1));
                data.extend_from_slice(&payload[1..]);
                // op 0x10 key exchange (c2s clear): 02 07 09 <key:9> <unknown:9>
                if op == 0x10
                    && data.len() >= 12
                    && data[0] == 0x02
                    && data[1] == 0x07
                    && data[2] >= 9
                {
                    let mut k = [0u8; 9];
                    k.copy_from_slice(&data[3..12]);
                    if k != codec.key {
                        codec.set_key(k);
                        self.key_updates += 1;
                    }
                }
            } else {
                ctr = payload[1];
                data = Vec::with_capacity(payload.len() - 2);
                codec.decrypt_into(ctr, &payload[2..], &mut data);
            }
            self.frames += 1;
            emit(Frame { op, ctr, clr, data });
        }
        if pos > 0 {
            self.buf.drain(..pos);
        }
    }
}
