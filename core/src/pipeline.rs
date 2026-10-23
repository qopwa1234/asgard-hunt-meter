#![allow(dead_code)]

use std::collections::HashMap;

use crate::crypto::Codec;
use crate::frame::{Dir, Frame, FrameStream};
use crate::net;
use crate::reasm::Reasm;

pub type ConnId = ((u32, u16), (u32, u16)); // (lo endpoint, hi endpoint), ordered

pub struct Conn {
    pub lo: (u32, u16),
    pub hi: (u32, u16),
    pub codec: Codec,
    pub dirs: [FrameStream; 2], // 0 = lo->hi, 1 = hi->lo
    pub reasm: [Reasm; 2],
    pub dir_class: [Dir; 2],
    pub dead: bool,
    pub pkts: [u64; 2],
    pub raw_bytes: [u64; 2],
}

impl Conn {
    pub fn new(lo: (u32, u16), hi: (u32, u16), ports: &[u16], key: [u8; 9]) -> Conn {
        let mut d0 = Dir::Unknown;
        let mut d1 = Dir::Unknown;
        if !ports.is_empty() {
            let lo_srv = ports.contains(&lo.1);
            let hi_srv = ports.contains(&hi.1);
            if hi_srv && !lo_srv {
                d0 = Dir::C2S;
                d1 = Dir::S2C;
            } else if lo_srv && !hi_srv {
                d0 = Dir::S2C;
                d1 = Dir::C2S;
            }
        }
        Conn {
            lo,
            hi,
            codec: Codec::with_key(key),
            dirs: [FrameStream::new(d0), FrameStream::new(d1)],
            reasm: [Reasm::new(), Reasm::new()],
            dir_class: [d0, d1],
            dead: false,
            pkts: [0, 0],
            raw_bytes: [0, 0],
        }
    }

    pub fn set_dir(&mut self, idx: usize, d: Dir) {
        let other = match d {
            Dir::C2S => Dir::S2C,
            Dir::S2C => Dir::C2S,
            Dir::Unknown => Dir::Unknown,
        };
        self.dir_class[idx] = d;
        self.dirs[idx].dir = d;
        self.dir_class[1 - idx] = other;
        self.dirs[1 - idx].dir = other;
    }
}

pub struct Pipeline {
    pub conns: HashMap<ConnId, Conn>,
    pub server_ports: Vec<u16>,
    pub key0: [u8; 9],
    /// New connections start with this ctr mode (2026-10-19 서버: low7 기본).
    pub ctr_half0: bool,
    pub pkts: u64,
    pub segs: u64,
    pub skips: u64,
}

impl Pipeline {
    pub fn new(server_ports: Vec<u16>, key0: Option<[u8; 9]>) -> Self {
        Pipeline {
            conns: HashMap::new(),
            server_ports,
            key0: key0.unwrap_or(crate::crypto::KEY_DEFAULT),
            ctr_half0: false,
            pkts: 0,
            segs: 0,
            skips: 0,
        }
    }

    /// Feed one captured packet; decoded frames are reported via `emit`.
    pub fn feed<E: FnMut(Dir, u64, ConnId, &Frame)>(
        &mut self,
        ts_us: u64,
        linktype: u16,
        data: &[u8],
        emit: &mut E,
    ) {
        self.pkts += 1;
        let seg = match net::parse_tcp(linktype, data) {
            Some(s) => s,
            None => return,
        };
        if !self.server_ports.is_empty()
            && !self.server_ports.contains(&seg.src_port)
            && !self.server_ports.contains(&seg.dst_port)
        {
            self.skips += 1;
            return;
        }
        self.segs += 1;
        let a = (seg.src_ip, seg.src_port);
        let b = (seg.dst_ip, seg.dst_port);
        let (lo, hi, diridx) = if a <= b { (a, b, 0usize) } else { (b, a, 1usize) };
        let cid: ConnId = (lo, hi);
        let ports: &[u16] = &self.server_ports;
        let key0 = self.key0;
        let ctr_half0 = self.ctr_half0;
        let conn = self.conns.entry(cid).or_insert_with(|| {
            let mut c = Conn::new(lo, hi, ports, key0);
            if ctr_half0 {
                c.codec.set_ctr_half(true);
            }
            c
        });

        if seg.flags & 0x02 != 0 {
            if seg.flags & 0x10 != 0 {
                if conn.dir_class[diridx] == Dir::Unknown {
                    conn.set_dir(diridx, Dir::S2C);
                }
            } else if conn.dir_class[diridx] == Dir::Unknown {
                conn.set_dir(diridx, Dir::C2S);
            }
        }
        if seg.flags & 0x04 != 0 {
            conn.dead = true;
        }
        conn.pkts[diridx] += 1;
        conn.raw_bytes[diridx] += seg.payload.len() as u64;

        let Conn {
            codec,
            dirs,
            reasm,
            dir_class,
            ..
        } = conn;
        let dir_now = dir_class[diridx];
        let fs = &mut dirs[diridx];
        fs.dir = dir_now;
        let rs = &mut reasm[diridx];
        let mut sink = |chunk: &[u8]| {
            let mut inner = |f: Frame| {
                emit(dir_now, ts_us, cid, &f);
            };
            fs.push(codec, chunk, &mut inner);
        };
        rs.push(seg.seq, seg.flags, seg.payload, &mut sink);
    }

    pub fn cleanup(&mut self) {
        if self.conns.len() > 1024 {
            self.conns.retain(|_, c| !c.dead);
        }
    }
}
