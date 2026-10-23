#![allow(dead_code)]

pub struct TcpSeg<'a> {
    pub src_ip: u32,
    pub dst_ip: u32,
    pub src_port: u16,
    pub dst_port: u16,
    pub seq: u32,
    pub flags: u8,
    pub payload: &'a [u8],
}

/// Parse Ethernet/SLL/NULL/raw-IPv4 link frames down to a TCP segment.
pub fn parse_tcp<'a>(linktype: u16, data: &'a [u8]) -> Option<TcpSeg<'a>> {
    let l3 = match linktype {
        1 => {
            if data.len() < 14 {
                return None;
            }
            let mut et = ((data[12] as u16) << 8) | data[13] as u16;
            let mut off = 14usize;
            while et == 0x8100 || et == 0x88a8 {
                if data.len() < off + 4 {
                    return None;
                }
                et = ((data[off + 2] as u16) << 8) | data[off + 3] as u16;
                off += 4;
            }
            if et != 0x0800 {
                return None;
            }
            off
        }
        113 => {
            if data.len() < 16 {
                return None;
            }
            let proto = ((data[14] as u16) << 8) | data[15] as u16;
            if proto != 0x0800 {
                return None;
            }
            16
        }
        0 | 108 => {
            if data.len() < 4 {
                return None;
            }
            let af = u32::from_le_bytes([data[0], data[1], data[2], data[3]]);
            let af_be = u32::from_be_bytes([data[0], data[1], data[2], data[3]]);
            if af != 2 && af_be != 2 {
                return None;
            }
            4
        }
        228 => 0,
        _ => return None,
    };

    if data.len() < l3 + 20 {
        return None;
    }
    let b = &data[l3..];
    if b[0] >> 4 != 4 {
        return None;
    }
    let ihl = ((b[0] & 0x0f) as usize) * 4;
    if ihl < 20 || b.len() < ihl {
        return None;
    }
    let total = ((b[2] as usize) << 8) | b[3] as usize;
    let frag = ((b[6] as u16) << 8) | b[7] as u16;
    if frag & 0x1fff != 0 {
        return None; // non-first fragment
    }
    if b[9] != 6 {
        return None; // not TCP
    }
    let src_ip = u32::from_be_bytes([b[12], b[13], b[14], b[15]]);
    let dst_ip = u32::from_be_bytes([b[16], b[17], b[18], b[19]]);
    let end = if total >= ihl && total <= b.len() {
        total
    } else {
        b.len()
    };
    if end < ihl + 20 {
        return None;
    }
    let t = &b[ihl..end];
    let src_port = ((t[0] as u16) << 8) | t[1] as u16;
    let dst_port = ((t[2] as u16) << 8) | t[3] as u16;
    let seq = u32::from_be_bytes([t[4], t[5], t[6], t[7]]);
    let doff = ((t[12] >> 4) as usize) * 4;
    if doff < 20 || t.len() < doff {
        return None;
    }
    let flags = t[13];
    Some(TcpSeg {
        src_ip,
        dst_ip,
        src_port,
        dst_port,
        seq,
        flags,
        payload: &t[doff..],
    })
}

pub fn ip_str(ip: u32) -> String {
    format!(
        "{}.{}.{}.{}",
        (ip >> 24) & 0xff,
        (ip >> 16) & 0xff,
        (ip >> 8) & 0xff,
        ip & 0xff
    )
}
