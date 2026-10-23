#![allow(dead_code)]

mod agg;
mod cp949;
mod cp949_table;
mod crypto;
mod frame;
mod keyrec;
mod live;
mod net;
mod opcode;
mod pcap;
mod pipeline;
mod reasm;
mod report;
mod stream;
mod util;

fn main() {
    util::init();
    let args: Vec<String> = std::env::args().collect();
    let code = match args.get(1).map(|s| s.as_str()) {
        Some("listen") => live::run(&args[2..]),
        Some("replay-bin") => stream::run_replay_bin(&args[2..]),
        Some("replay-pcap") => stream::run_replay_pcap(&args[2..]),
        Some("recover-key") => keyrec::cli_recover(&args[2..]),
        Some("agg-replay") => agg::cli_replay(&args[2..]),
        Some("version") | Some("--version") | Some("-V") => {
            println!("asgard-core {}", env!("CARGO_PKG_VERSION"));
            0
        }
        _ => {
            help();
            0
        }
    };
    std::process::exit(code);
}

fn help() {
    println!("asgard-core - Asgard Hunt Meter core (passive capture/decoder)");
    println!();
    println!("usage:");
    println!("  asgard-core replay-bin <stream.bin> --dir s2c|c2s [--label NAME] [--out DIR] [--key HEX9]");
    println!("  asgard-core replay-pcap <file.pcap|pcapng> [--port N ...] [--dump-dir DIR] [--label NAME]");
    println!("  asgard-core listen [--iface N|NAME] [--filter EXPR] [--host IP ...] [--port N ...]");
    println!("                     [--out DIR] [--seconds N] [--key HEX9] [--dumpcap PATH] [--trace] [--json]");
    println!("  asgard-core recover-key --in <frames.bin> [--key HEX9] [--out DIR]");
    println!();
    println!("  replay-bin  : decode a raw reassembled TCP stream (binary) -> stats/samples/strings/decoded.bin");
    println!("  replay-pcap : parse pcap/pcapng, reassemble TCP, decode game frames, per-connection stats");
    println!("  listen      : live capture via dumpcap (admin + Npcap), decode, print gold/exp/chat events");
    println!("                --trace: timestamped per-frame trace.log for opcode mapping");
    println!("                --json : emit JSON event/status lines on stdout (GUI mode)");
    println!("  recover-key : known-plaintext session-key recovery for a frames dump (mid-session attach)");
    println!("  agg-replay  : feed saved frames_*.bin through the aggregator (verification)");
}
