#![allow(dead_code)]

use std::io::{self, ErrorKind, Read};

pub struct Pkt {
    pub ts_us: u64,
    pub linktype: u16,
    pub data: Vec<u8>,
}

impl Default for Pkt {
    fn default() -> Self {
        Pkt {
            ts_us: 0,
            linktype: 1,
            data: Vec::new(),
        }
    }
}

enum Kind {
    Classic,
    Ng,
}

struct NgIface {
    linktype: u16,
    ps_per_unit: u64,
}

const MAX_CAPLEN: usize = 16 * 1024 * 1024;

fn read_fully<R: Read>(r: &mut R, buf: &mut [u8]) -> io::Result<bool> {
    let mut off = 0;
    while off < buf.len() {
        match r.read(&mut buf[off..]) {
            Ok(0) => {
                if off == 0 {
                    return Ok(false);
                }
                return Err(io::Error::new(ErrorKind::UnexpectedEof, "eof mid-record"));
            }
            Ok(n) => off += n,
            Err(e) if e.kind() == ErrorKind::Interrupted => continue,
            Err(e) => return Err(e),
        }
    }
    Ok(true)
}

fn rd_u16(b: &[u8], le: bool) -> u16 {
    let a = [b[0], b[1]];
    if le {
        u16::from_le_bytes(a)
    } else {
        u16::from_be_bytes(a)
    }
}

fn rd_u32(b: &[u8], le: bool) -> u32 {
    let a = [b[0], b[1], b[2], b[3]];
    if le {
        u32::from_le_bytes(a)
    } else {
        u32::from_be_bytes(a)
    }
}

/// Streaming capture reader for classic pcap and pcapng (file or pipe).
pub struct CapReader<R: Read> {
    r: R,
    kind: Kind,
    le: bool,
    ts_ns: bool,
    linktype: u16,
    ifaces: Vec<NgIface>,
    eof: bool,
}

impl<R: Read> CapReader<R> {
    pub fn new(mut r: R) -> io::Result<Self> {
        let mut magic = [0u8; 4];
        if !read_fully(&mut r, &mut magic)? {
            return Err(io::Error::new(ErrorKind::UnexpectedEof, "empty capture"));
        }
        if magic == [0x0a, 0x0d, 0x0d, 0x0a] {
            let mut h = [0u8; 8];
            if !read_fully(&mut r, &mut h)? {
                return Err(io::Error::new(ErrorKind::UnexpectedEof, "truncated pcapng"));
            }
            let le = h[4..8] == [0x4d, 0x3c, 0x2b, 0x1a];
            let blen = rd_u32(&h[0..4], le) as usize;
            if !(12..=MAX_CAPLEN).contains(&blen) {
                return Err(io::Error::new(ErrorKind::InvalidData, "bad pcapng SHB length"));
            }
            let mut rest = vec![0u8; blen - 12];
            if !read_fully(&mut r, &mut rest)? {
                return Err(io::Error::new(ErrorKind::UnexpectedEof, "truncated pcapng SHB"));
            }
            Ok(CapReader {
                r,
                kind: Kind::Ng,
                le,
                ts_ns: false,
                linktype: 1,
                ifaces: Vec::new(),
                eof: false,
            })
        } else {
            let (le, ns) = match magic {
                [0xd4, 0xc3, 0xb2, 0xa1] => (true, false),
                [0xa1, 0xb2, 0xc3, 0xd4] => (false, false),
                [0x4d, 0x3c, 0xb2, 0xa1] => (true, true),
                [0xa1, 0xb2, 0x3c, 0x4d] => (false, true),
                _ => {
                    return Err(io::Error::new(
                        ErrorKind::InvalidData,
                        "unknown capture magic (not pcap/pcapng)",
                    ))
                }
            };
            let mut rest = [0u8; 20];
            if !read_fully(&mut r, &mut rest)? {
                return Err(io::Error::new(ErrorKind::UnexpectedEof, "truncated pcap header"));
            }
            let linktype = rd_u16(&rest[16..18], le);
            Ok(CapReader {
                r,
                kind: Kind::Classic,
                le,
                ts_ns: ns,
                linktype,
                ifaces: Vec::new(),
                eof: false,
            })
        }
    }

    pub fn next(&mut self, out: &mut Pkt) -> io::Result<Option<()>> {
        if self.eof {
            return Ok(None);
        }
        match self.kind {
            Kind::Classic => {
                let mut h = [0u8; 16];
                if !read_fully(&mut self.r, &mut h)? {
                    self.eof = true;
                    return Ok(None);
                }
                let ts_sec = rd_u32(&h[0..4], self.le) as u64;
                let ts_frac = rd_u32(&h[4..8], self.le) as u64;
                let incl = rd_u32(&h[8..12], self.le) as usize;
                if incl > MAX_CAPLEN {
                    return Err(io::Error::new(ErrorKind::InvalidData, "caplen too large"));
                }
                let ts_us = if self.ts_ns {
                    ts_sec * 1_000_000 + ts_frac / 1000
                } else {
                    ts_sec * 1_000_000 + ts_frac
                };
                out.data.resize(incl, 0);
                if !read_fully(&mut self.r, &mut out.data)? {
                    self.eof = true;
                    return Ok(None);
                }
                out.ts_us = ts_us;
                out.linktype = self.linktype;
                Ok(Some(()))
            }
            Kind::Ng => loop {
                let mut h = [0u8; 8];
                if !read_fully(&mut self.r, &mut h)? {
                    self.eof = true;
                    return Ok(None);
                }
                let mut btype = rd_u32(&h[0..4], self.le);
                let mut blen = rd_u32(&h[4..8], self.le) as usize;
                if !(12..=MAX_CAPLEN).contains(&blen) {
                    let blen2 = rd_u32(&h[4..8], !self.le) as usize;
                    let t2 = rd_u32(&h[0..4], !self.le);
                    if (12..=MAX_CAPLEN).contains(&blen2) && t2 == 0x0a0d0d0a {
                        self.le = !self.le;
                        btype = 0x0a0d0d0a;
                        blen = blen2;
                        let _ = btype;
                    } else {
                        return Err(io::Error::new(
                            ErrorKind::InvalidData,
                            "bad pcapng block length",
                        ));
                    }
                }
                let mut body = vec![0u8; blen - 12];
                if !read_fully(&mut self.r, &mut body)? {
                    self.eof = true;
                    return Ok(None);
                }
                let mut tl = [0u8; 4];
                if !read_fully(&mut self.r, &mut tl)? {
                    self.eof = true;
                    return Ok(None);
                }
                match btype {
                    0x0a0d0d0a => {
                        if body.len() >= 4 {
                            if body[0..4] == [0x4d, 0x3c, 0x2b, 0x1a] {
                                self.le = true;
                            } else if body[0..4] == [0x1a, 0x2b, 0x3c, 0x4d] {
                                self.le = false;
                            }
                        }
                    }
                    0x00000001 => {
                        if body.len() >= 8 {
                            let linktype = rd_u16(&body[0..2], self.le);
                            let mut ps: u64 = 1_000_000;
                            let mut o = 8usize;
                            while o + 4 <= body.len() {
                                let code = rd_u16(&body[o..o + 2], self.le);
                                let olen = rd_u16(&body[o + 2..o + 4], self.le) as usize;
                                if code == 0 {
                                    break;
                                }
                                let dstart = o + 4;
                                if dstart + olen > body.len() {
                                    break;
                                }
                                if code == 9 && olen >= 1 {
                                    let v = body[dstart];
                                    if v & 0x80 == 0 {
                                        let exp = (v as u32).min(12);
                                        ps = 10u64.pow(12 - exp);
                                    } else {
                                        let exp = ((v & 0x7f) as u32).min(40);
                                        ps = 1_000_000_000_000u64 / (1u64 << exp);
                                    }
                                }
                                o = (dstart + olen + 3) & !3usize;
                            }
                            self.ifaces.push(NgIface {
                                linktype,
                                ps_per_unit: ps,
                            });
                        }
                    }
                    0x00000006 => {
                        if body.len() >= 20 {
                            let iface = rd_u32(&body[0..4], self.le) as usize;
                            let tsh = rd_u32(&body[4..8], self.le) as u64;
                            let tsl = rd_u32(&body[8..12], self.le) as u64;
                            let caplen = rd_u32(&body[12..16], self.le) as usize;
                            let raw_ts = (tsh << 32) | tsl;
                            let (lt, ps) = self.iface_info(iface);
                            let clamped = caplen.min(body.len() - 20).min(MAX_CAPLEN);
                            out.data.clear();
                            out.data.extend_from_slice(&body[20..20 + clamped]);
                            out.ts_us = ((raw_ts as u128 * ps as u128) / 1_000_000u128) as u64;
                            out.linktype = lt;
                            return Ok(Some(()));
                        }
                    }
                    0x00000003 => {
                        if body.len() >= 4 {
                            let origlen = rd_u32(&body[0..4], self.le) as usize;
                            let area = body.len() - 4;
                            let caplen = origlen.min(area).min(MAX_CAPLEN);
                            out.data.clear();
                            out.data.extend_from_slice(&body[4..4 + caplen]);
                            out.ts_us = 0;
                            out.linktype = 1;
                            return Ok(Some(()));
                        }
                    }
                    0x00000002 => {
                        if body.len() >= 20 {
                            let iface = rd_u16(&body[0..2], self.le) as usize;
                            let tsh = rd_u32(&body[4..8], self.le) as u64;
                            let tsl = rd_u32(&body[8..12], self.le) as u64;
                            let caplen = rd_u32(&body[12..16], self.le) as usize;
                            let raw_ts = (tsh << 32) | tsl;
                            let (lt, ps) = self.iface_info(iface);
                            let clamped = caplen.min(body.len() - 20).min(MAX_CAPLEN);
                            out.data.clear();
                            out.data.extend_from_slice(&body[20..20 + clamped]);
                            out.ts_us = ((raw_ts as u128 * ps as u128) / 1_000_000u128) as u64;
                            out.linktype = lt;
                            return Ok(Some(()));
                        }
                    }
                    _ => {}
                }
            },
        }
    }

    fn iface_info(&self, idx: usize) -> (u16, u64) {
        self.ifaces
            .get(idx)
            .map(|i| (i.linktype, i.ps_per_unit))
            .unwrap_or((1, 1_000_000))
    }
}
