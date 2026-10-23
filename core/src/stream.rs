#![allow(dead_code)]

use std::collections::HashMap;
use std::fs;
use std::io::BufReader;

use crate::crypto::Codec;
use crate::frame::{Dir, Frame, FrameStream};
use crate::opcode;
use crate::pcap::{CapReader, Pkt};
use crate::pipeline::{ConnId, Pipeline};
use crate::report::{self, MsgStats, Stats};
use crate::util::{default_stem, parse_args, parse_key};

pub fn run_replay_bin(args: &[String]) -> i32 {
    let a = parse_args(args);
    let file = match a.pos.first() {
        Some(f) => f.clone(),
        None => {
            eprintln!("usage: asgard-core replay-bin <stream.bin> --dir s2c|c2s [--label NAME] [--out DIR] [--key HEX9]");
            return 2;
        }
    };
    let dir = match a.get("dir").unwrap_or("s2c") {
        "c2s" => Dir::C2S,
        "s2c" => Dir::S2C,
        o => {
            eprintln!("bad --dir {} (expected c2s|s2c)", o);
            return 2;
        }
    };
    let label = a
        .get("label")
        .map(|s| s.to_string())
        .unwrap_or_else(|| default_stem(&file));
    let out = a.get("out").unwrap_or(".").to_string();
    let key = a.get("key").and_then(|s| parse_key(s));
    let data = match fs::read(&file) {
        Ok(d) => d,
        Err(e) => {
            eprintln!("read error: {}: {}", file, e);
            return 1;
        }
    };
    let mut codec = match key {
        Some(k) => Codec::with_key(k),
        None => Codec::new(),
    };
    let mut fsx = FrameStream::new(dir);
    let mut stats = Stats::new();
    let mut decoded: Vec<u8> = Vec::with_capacity(data.len());
    let mut csv: Vec<String> = Vec::new();
    let mut strings: Vec<String> = Vec::new();
    let mut msgs = MsgStats::new();
    let mut idx = 0usize;
    fsx.push(&mut codec, &data, &mut |f: Frame| {
        stats.add(&f);
        decoded.push(f.op);
        decoded.extend_from_slice(&(f.data.len() as u16).to_le_bytes());
        decoded.extend_from_slice(&f.data);
        csv.push(format!(
            "{},0x{:02x},{},{},{}",
            idx,
            f.op,
            f.data.len(),
            f.ctr,
            if f.clr { 1 } else { 0 }
        ));
        report::extract_strings(f.op, idx, &f.data, &mut strings, 200_000);
        let _ = report::scan_msg(f.op, &f.data, &mut msgs);
        idx += 1;
    });
    if let Err(e) = report::write_replay_outputs(&out, &label, &stats, &decoded, &csv, &strings) {
        eprintln!("write error: {}", e);
        return 1;
    }
    println!(
        "### {} dir={} frames={} decodedBytes={}",
        label,
        dir.label(),
        stats.frames,
        stats.bytes
    );
    let top: Vec<String> = stats
        .sorted()
        .iter()
        .take(15)
        .map(|s| format!("0x{:x}:{}", s.op, s.n))
        .collect();
    println!("top ops: {}", top.join(" "));
    println!(
        "resyncs={} key_updates={} msgs(gold={} exp={} fame={})",
        fsx.resyncs, fsx.key_updates, msgs.gold_msgs, msgs.exp_msgs, msgs.fame_msgs
    );
    0
}

struct ConnOut {
    frames: [u64; 2],
    bytes: [u64; 2],
    by: [HashMap<u8, u64>; 2],
    decoded: [Vec<u8>; 2],
    msgs: MsgStats,
}

impl ConnOut {
    fn new() -> Self {
        ConnOut {
            frames: [0, 0],
            bytes: [0, 0],
            by: [HashMap::new(), HashMap::new()],
            decoded: [Vec::new(), Vec::new()],
            msgs: MsgStats::new(),
        }
    }
}

fn endpoints(cid: &ConnId, ports: &[u16]) -> ((u32, u16), (u32, u16)) {
    let (lo, hi) = *cid;
    if !ports.is_empty() {
        if ports.contains(&hi.1) {
            return (lo, hi);
        }
        if ports.contains(&lo.1) {
            return (hi, lo);
        }
    }
    if hi.1 < lo.1 {
        (lo, hi)
    } else {
        (hi, lo)
    }
}

pub fn run_replay_pcap(args: &[String]) -> i32 {
    let a = parse_args(args);
    let file = match a.pos.first() {
        Some(f) => f.clone(),
        None => {
            eprintln!("usage: asgard-core replay-pcap <file.pcap|pcapng> [--port N ...] [--dump-dir DIR] [--key HEX9]");
            return 2;
        }
    };
    let ports: Vec<u16> = a.all("port").iter().filter_map(|s| s.parse().ok()).collect();
    let key = a.get("key").and_then(|s| parse_key(s));
    let dump_dir = a.get("dump-dir").map(|s| s.to_string());
    println!(
        "replay-pcap file={} ports={:?} dump-dir={:?}",
        file,
        ports,
        dump_dir
    );

    let f = match fs::File::open(&file) {
        Ok(f) => f,
        Err(e) => {
            eprintln!("open error: {}: {}", file, e);
            return 1;
        }
    };
    let mut reader = match CapReader::new(BufReader::with_capacity(1 << 20, f)) {
        Ok(r) => r,
        Err(e) => {
            eprintln!("capture open error: {}", e);
            return 1;
        }
    };
    let mut pipe = Pipeline::new(ports.clone(), key);
    let mut pkt = Pkt::default();
    let mut outs: HashMap<ConnId, ConnOut> = HashMap::new();
    let mut packets: u64 = 0;
    loop {
        match reader.next(&mut pkt) {
            Ok(Some(())) => {
                packets += 1;
                let ts = pkt.ts_us;
                let lt = pkt.linktype;
                let mut emit = |dir: Dir, ts_us: u64, cid: ConnId, f: &Frame| {
                    let o = outs.entry(cid).or_insert_with(ConnOut::new);
                    let di = match dir {
                        Dir::C2S => 0usize,
                        Dir::S2C => 1usize,
                        Dir::Unknown => 0usize,
                    };
                    o.frames[di] += 1;
                    o.bytes[di] += f.data.len() as u64;
                    *o.by[di].entry(f.op).or_insert(0) += 1;
                    o.decoded[di].push(f.op);
                    o.decoded[di].extend_from_slice(&(f.data.len() as u16).to_le_bytes());
                    o.decoded[di].extend_from_slice(&f.data);
                    let _ = report::scan_msg(f.op, &f.data, &mut o.msgs);
                    let _ = ts_us;
                };
                pipe.feed(ts, lt, &pkt.data, &mut emit);
                if packets % 500_000 == 0 {
                    pipe.cleanup();
                    eprintln!("... {} packets, {} conns", packets, pipe.conns.len());
                }
            }
            Ok(None) => break,
            Err(e) => {
                eprintln!("capture read error at packet {}: {}", packets, e);
                break;
            }
        }
    }
    eprintln!(
        "# packets={} tcp_segments={} skipped_non_matching={}",
        packets, pipe.segs, pipe.skips
    );

    let mut list: Vec<(ConnId, &ConnOut)> = outs.iter().map(|(k, v)| (*k, v)).collect();
    list.sort_by(|a, b| {
        (b.1.frames[0] + b.1.frames[1]).cmp(&(a.1.frames[0] + a.1.frames[1]))
    });
    for (cid, o) in list.iter().take(30) {
        if o.frames[0] + o.frames[1] == 0 {
            continue;
        }
        let (client, server) = endpoints(cid, &ports);
        println!(
            "[conn] {}:{} -> {}:{}  s2c frames={} bytes={} | c2s frames={} bytes={}",
            crate::net::ip_str(client.0),
            client.1,
            crate::net::ip_str(server.0),
            server.1,
            o.frames[1],
            o.bytes[1],
            o.frames[0],
            o.bytes[0]
        );
        if let Some(c) = pipe.conns.get(cid) {
            let mut c2s_idx = 0usize;
            let mut s2c_idx = 1usize;
            for i in 0..2 {
                match c.dir_class[i] {
                    Dir::C2S => c2s_idx = i,
                    Dir::S2C => s2c_idx = i,
                    Dir::Unknown => {}
                }
            }
            println!(
                "       reasm: c2s lost={} dups={} | s2c lost={} dups={}",
                c.reasm[c2s_idx].lost_bytes,
                c.reasm[c2s_idx].dups,
                c.reasm[s2c_idx].lost_bytes,
                c.reasm[s2c_idx].dups
            );
        }
        let mut top: Vec<(u8, &'static str, u64, bool)> = Vec::new();
        for (op, n) in o.by[1].iter() {
            top.push((*op, opcode::s2c_name(*op), *n, true));
        }
        for (op, n) in o.by[0].iter() {
            top.push((*op, opcode::c2s_name(*op), *n, false));
        }
        top.sort_by(|a, b| b.2.cmp(&a.2));
        let s: Vec<String> = top
            .iter()
            .take(8)
            .map(|(op, nm, n, s2c)| format!("{}0x{:02x} {}:{}", if *s2c { "s" } else { "c" }, op, nm, n))
            .collect();
        println!("       top: {}", s.join("  "));
        if o.msgs.gold_msgs > 0 || o.msgs.exp_msgs > 0 || o.msgs.fame_msgs > 0 {
            println!(
                "       msgs: gold={} ({}) exp={} ({}) fame={} ({})",
                o.msgs.gold,
                o.msgs.gold_msgs,
                o.msgs.exp,
                o.msgs.exp_msgs,
                o.msgs.fame,
                o.msgs.fame_msgs
            );
        }
    }

    if let Some(dd) = &dump_dir {
        let _ = fs::create_dir_all(dd);
        for (cid, o) in &list {
            if o.frames[0] + o.frames[1] == 0 {
                continue;
            }
            let (client, server) = endpoints(cid, &ports);
            for (di, tag) in [(0usize, "c2s"), (1usize, "s2c")] {
                if o.frames[di] == 0 {
                    continue;
                }
                let name = format!(
                    "{}\\clientport-{}__serverport-{}__{}_decoded.bin",
                    dd, client.1, server.1, tag
                );
                let _ = fs::write(name, &o.decoded[di]);
            }
        }
        println!("dumped decoded streams to {}", dd);
    }
    0
}
