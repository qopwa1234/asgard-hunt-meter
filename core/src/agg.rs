#![allow(dead_code)]

//! P2 aggregation engine: converts decoded frames into structured profit events
//! (gold/exp/fame/kills/items/chat/consumable use/buffs/market) and periodic stats.
//! Emits JSON lines (one object per line) for the GUI. No external crates.

use std::collections::HashMap;

use crate::cp949;
use crate::frame::{Dir, Frame};
use crate::util::json_escape;

/// Max distinct mob names tracked for kill statistics.
const MOB_CAP: usize = 512;
/// Max distinct items tracked.
const ITEM_CAP: usize = 4096;
/// Max mob objId -> name entries (spawn cache).
const SPAWN_CAP: usize = 4096;

pub struct ItemAgg {
    pub name: String,
    pub iid: String,
    pub count: u64,
    pub first_ms: u64,
    pub last_ms: u64,
}

/// Live inventory item state, keyed by item serial (0x0f refresh frames).
/// `count` = stack quantity (u16 BE in the tail `.. 01 30 [hi] [lo] 01 00 00`),
/// `cur`/`max` = POINT charge string (e.g. R포션 505945/1000000).
pub struct InvState {
    pub name: String,
    pub iid: String,
    pub slot: u8,
    pub count: Option<u64>,
    pub cur: Option<u64>,
    pub max: Option<u64>,
    pub last_ms: u64,
    pub updates: u64,
}

pub struct Agg {
    pub start_ms: u64,
    pub last_ms: u64,
    pub gold: u64,
    pub exp: u64,
    pub fame: u64,
    pub kills: u64,
    pub items_total: u64,
    pub gold_msgs: u64,
    pub exp_msgs: u64,
    pub fame_msgs: u64,
    pub item_msgs: u64,
    pub chats: u64,
    pub uses: u64,
    pub buffs: u64,
    pub item_order: Vec<String>,
    pub items: HashMap<String, ItemAgg>,
    pub item_msg_order: Vec<String>,
    pub item_msgs_by_player: HashMap<String, u64>,
    /// mob objId -> (name, last_seen_ms)
    spawn_names: HashMap<u32, (String, u64)>,
    pub mob_kills: HashMap<String, u64>,
    pub mob_order: Vec<String>,
    /// last N (ts_ms, gold_total) samples for pace calculation
    pace: Vec<(u64, u64)>,
    cur_min: u64,
    cur_g: u64,
    cur_e: u64,
    cur_k: u64,
    pub minutes: Vec<(u64, u64, u64, u64)>,
    /// serial -> live inventory state (updated by 0x0f refresh frames)
    pub inv: HashMap<u32, InvState>,
    pub inv_upds: u64,
    pub frames_s2c: u64,
    pub frames_c2s: u64,
    /// repair(반전 프레임 복구)로 정정된 프레임 수
    pub repaired: u64,
    /// 내 캐릭터 이름 — 일반채팅/귓말 에코 매칭으로 학습
    pub my_name: Option<String>,
    /// C2S 0x0e 일반채팅 전송 (ts, text) — S2C 에코와 매칭해 my_name 학습
    pending_sent: Vec<(u64, String)>,
    /// C2S 0x19 귓말 전송 (ts, 대상, text) — S2C 에코와 매칭
    pending_whisper: Vec<(u64, String, String)>,
    /// C2S 0x19 그룹채팅 전송 (ts, text) — 채널 마커 0x2121. S2C 그룹 에코와 매칭
    pending_group: Vec<(u64, String)>,
    /// C2S 0x19 길드채팅 전송 (ts, text) — 채널 마커 0x2134. S2C 통합(1c) 에코와 매칭
    pending_guild: Vec<(u64, String)>,
    /// 그룹 채팅에서 관측된 그룹원 이름 (GUI 그룹원 득템 트래킹용)
    pub group_players: Vec<String>,
    /// iid -> (name, last_val, last_ms, rate(값/초))
    buff_state: HashMap<String, (String, u16, u64, f64)>,
    /// 0x44에서 감지한 미션/이벤트 문구 (중복 방지)
    mission_seen: std::collections::HashSet<String>,
}

impl Agg {
    pub fn new() -> Self {
        Agg {
            start_ms: 0,
            last_ms: 0,
            gold: 0,
            exp: 0,
            fame: 0,
            kills: 0,
            items_total: 0,
            gold_msgs: 0,
            exp_msgs: 0,
            fame_msgs: 0,
            item_msgs: 0,
            chats: 0,
            uses: 0,
            buffs: 0,
            item_order: Vec::new(),
            items: HashMap::new(),
            item_msg_order: Vec::new(),
            item_msgs_by_player: HashMap::new(),
            spawn_names: HashMap::new(),
            mob_kills: HashMap::new(),
            mob_order: Vec::new(),
            pace: Vec::new(),
            cur_min: 0,
            cur_g: 0,
            cur_e: 0,
            cur_k: 0,
            minutes: Vec::new(),
            inv: HashMap::new(),
            inv_upds: 0,
            frames_s2c: 0,
            frames_c2s: 0,
            repaired: 0,
            my_name: None,
            pending_sent: Vec::new(),
            pending_whisper: Vec::new(),
            pending_group: Vec::new(),
            pending_guild: Vec::new(),
            group_players: Vec::new(),
            buff_state: HashMap::new(),
            mission_seen: std::collections::HashSet::new(),
        }
    }

    fn ensure_start(&mut self, ts_ms: u64) {
        if self.start_ms == 0 {
            self.start_ms = ts_ms;
        }
        self.last_ms = ts_ms;
        let m = ts_ms / 60_000;
        if self.cur_min == 0 {
            self.cur_min = m;
        }
    }

    /// Called for every decoded frame. Appends JSON lines to `out`.
    pub fn on_frame(&mut self, dir: Dir, ts_ms: u64, f: &Frame, out: &mut Vec<String>) {
        self.ensure_start(ts_ms);
        match dir {
            Dir::S2C => self.frames_s2c += 1,
            Dir::C2S => self.frames_c2s += 1,
            Dir::Unknown => {}
        }
        self.close_minutes(ts_ms, out);
        // 오디코드(서버 카운터 경계)로 앞부분이 반전된 프레임 복구.
        // 구조 검증에 실패하고, 첫 9바이트 반전본이 검증을 통과할 때만 교체한다.
        let fixed = if dir == Dir::S2C && !frame_ok(f.op, &f.data) {
            repair_frame(f)
        } else {
            None
        };
        if fixed.is_some() {
            self.repaired += 1;
        }
        let f = fixed.as_ref().unwrap_or(f);
        match (dir, f.op) {
            (Dir::S2C, 0x0a) => self.on_message(ts_ms, f, out),
            (Dir::S2C, 0x0f) => self.on_item_add(ts_ms, f, out),
            (Dir::S2C, 0x5f) => self.on_death(ts_ms, f, out),
            (Dir::S2C, 0x07) => self.on_draw_objects(ts_ms, f),
            (Dir::S2C, 0x0d) => self.on_say(ts_ms, f, out),
            (Dir::S2C, 0x3a) => self.on_party(ts_ms, f, out),
            (Dir::S2C, 0x2c) => self.on_buff(ts_ms, f, true, out),
            (Dir::S2C, 0x2d) => self.on_buff(ts_ms, f, false, out),
            (Dir::C2S, 0x1c) => self.on_use(ts_ms, f, out),
            (Dir::C2S, 0x0e) => self.on_chat_send(ts_ms, f),
            (Dir::C2S, 0x19) => self.on_whisper_send(ts_ms, f),
            (Dir::C2S, 0x87) => self.on_market_req(ts_ms, f, out),
            (Dir::S2C, 0x91) => self.on_market_res(ts_ms, f, out),
            (Dir::S2C, 0x44) => self.on_mission(ts_ms, f, out),
            _ => {}
        }
    }

    fn close_minutes(&mut self, ts_ms: u64, out: &mut Vec<String>) {
        let m = ts_ms / 60_000;
        if self.cur_min != 0 && m != self.cur_min {
            out.push(format!(
                "{{\"type\":\"minute\",\"mi\":{},\"gold\":{},\"exp\":{},\"kills\":{}}}",
                self.cur_min, self.cur_g, self.cur_e, self.cur_k
            ));
            self.minutes.push((self.cur_min, self.cur_g, self.cur_e, self.cur_k));
            if self.minutes.len() > 24 * 60 {
                self.minutes.remove(0);
            }
            self.cur_min = m;
            self.cur_g = 0;
            self.cur_e = 0;
            self.cur_k = 0;
        }
    }

    // ---- 0x0a SMessagePacket -------------------------------------------------
    //
    // 레이아웃: [subtype u16 LE][len u8][text CP949]  (단 03 00 은 4바이트 헤더)
    // 서브타입(실측): 00 00=귓말, 0b 00=그룹 채팅, 1c 00=통합 채팅, 1e 00=연합,
    //   19 00=골드, 1a 00=EXP, 18 00=아이템 획득 알림, 21 00=스탯 변동,
    //   03 00=공지/방송, 05 00=안내 팁, 08 00=점수/보상, 22 00=즉시시전,
    //   23 00=도움요청/알림
    fn on_message(&mut self, ts_ms: u64, f: &Frame, out: &mut Vec<String>) {
        let d = &f.data;
        if d.len() >= 4 {
            let sub = u16::from_le_bytes([d[0], d[1]]);
            match sub {
                0x0000 => {
                    self.on_whisper_msg(ts_ms, d, out);
                    return;
                }
                0x000b => {
                    self.on_group_msg(ts_ms, d, out);
                    return;
                }
                0x001c => {
                    self.on_global_msg(ts_ms, d, out);
                    return;
                }
                0x001e => {
                    self.on_union_msg(ts_ms, d, out);
                    return;
                }
                _ => {}
            }
        }
        let text = cp949::clean(&cp949::decode(&f.data));
        if text.len() < 2 {
            return;
        }
        if text.contains("글로드 획득했습니다") {
            if let Some(n) = num_before(&text, "글로드 획득했습니다") {
                self.gold += n;
                self.gold_msgs += 1;
                self.cur_g += n;
                self.pace.push((ts_ms, self.gold));
                if self.pace.len() > 512 {
                    self.pace.remove(0);
                }
                out.push(format!(
                    "{{\"type\":\"ev\",\"t\":{},\"kind\":\"gold\",\"amount\":{},\"text\":\"{}\"}}",
                    ts_ms,
                    n,
                    json_escape(&text)
                ));
                return;
            }
        }
        if let Some(n) = amount_after(&text, "xp포인트 획득") {
            self.exp += n;
            self.exp_msgs += 1;
            self.cur_e += n;
            out.push(format!(
                "{{\"type\":\"ev\",\"t\":{},\"kind\":\"exp\",\"amount\":{},\"text\":\"{}\"}}",
                ts_ms,
                n,
                json_escape(&text)
            ));
            return;
        }
        if let Some(n) = amount_after(&text, "명성 경험치 획득") {
            self.fame += n;
            self.fame_msgs += 1;
            out.push(format!(
                "{{\"type\":\"ev\",\"t\":{},\"kind\":\"fame\",\"amount\":{},\"text\":\"{}\"}}",
                ts_ms,
                n,
                json_escape(&text)
            ));
            return;
        }
        if text.contains("획득하였습니다") || text.contains("획득했습니다") {
            if let Some((color, player, item, count)) = parse_item_msg(&text) {
                self.item_msgs += 1;
                *self.item_msgs_by_player.entry(player.clone()).or_insert(0) += 1;
                if !self.item_msg_order.contains(&item) {
                    self.item_msg_order.push(item.clone());
                }
                out.push(format!(
                    "{{\"type\":\"ev\",\"t\":{},\"kind\":\"itemmsg\",\"player\":\"{}\",\"name\":\"{}\",\"count\":{},\"color\":\"{}\",\"text\":\"{}\"}}",
                    ts_ms,
                    json_escape(&player),
                    json_escape(&item),
                    count,
                    json_escape(&color),
                    json_escape(&text)
                ));
                return;
            }
        }
        // 버프 만료: "<이름> 효과가 사라졌습니다" → buff_end 이벤트 (학습/알림용)
        if let Some(pos) = text.find("효과가 사라졌습니다") {
            let name = text[..pos].trim().to_string();
            if !name.is_empty() {
                out.push(format!(
                    "{{\"type\":\"ev\",\"t\":{},\"kind\":\"buff_end\",\"name\":\"{}\",\"text\":\"{}\"}}",
                    ts_ms,
                    json_escape(&name),
                    json_escape(&text)
                ));
                return;
            }
        }
        // generic system text worth showing (buffs gone, level-ups, notices)
        let hangul = text
            .chars()
            .filter(|c| ('\u{AC00}'..='\u{D7A3}').contains(c))
            .count();
        if hangul >= 4 && cp949::is_plausible_text(&text) {
            out.push(format!(
                "{{\"type\":\"ev\",\"t\":{},\"kind\":\"sys\",\"text\":\"{}\"}}",
                ts_ms,
                json_escape(&text)
            ));
        }
    }

    // ---- 0x0a[00 00] 귓말 ----------------------------------------------------
    //
    // 본문: "이름> 내용". 내가 보낸 귓말은 서버가 내 이름으로 에코한다.
    // C2S 0x19 전송 기록과 매칭해 방향(in/out)을 판정한다.
    fn on_whisper_msg(&mut self, ts_ms: u64, d: &[u8], out: &mut Vec<String>) {
        let Some(t) = sub_text(d) else { return };
        let t = strip_markup(&t);
        let Some(p) = t.find("> ") else {
            // "여프로님은 게임에 없습니다" 등 실패 안내
            let hangul = t
                .chars()
                .filter(|c| ('\u{AC00}'..='\u{D7A3}').contains(c))
                .count();
            if hangul >= 2 {
                out.push(format!(
                    "{{\"type\":\"ev\",\"t\":{},\"kind\":\"sys\",\"text\":\"{}\"}}",
                    ts_ms,
                    json_escape(&t)
                ));
            }
            return;
        };
        let from = t[..p].trim().to_string();
        let msg = t[p + 2..].trim_start().to_string();
        if from.is_empty() || msg.is_empty() {
            return;
        }
        let mut dir_out = false;
        let mut peer = String::new();
        if let Some(idx) = self.pending_whisper.iter().position(|(ts, to, tx)| {
            ts_ms.saturating_sub(*ts) <= 5_000 && *tx == msg && *to != from
        }) {
            let (_, to, _) = self.pending_whisper.remove(idx);
            dir_out = true;
            peer = to;
            if self.my_name.is_none() {
                self.my_name = Some(from.clone());
            }
        } else if let Some(mn) = &self.my_name {
            if *mn == from {
                dir_out = true;
                if let Some((_, to, _)) = self.pending_whisper.last() {
                    peer = to.clone();
                }
            }
        }
        self.chats += 1;
        if dir_out {
            out.push(format!(
                "{{\"type\":\"ev\",\"t\":{},\"kind\":\"whisper\",\"dir\":\"out\",\"from\":\"{}\",\"peer\":\"{}\",\"text\":\"{}\"}}",
                ts_ms,
                json_escape(&from),
                json_escape(&peer),
                json_escape(&msg)
            ));
        } else {
            out.push(format!(
                "{{\"type\":\"ev\",\"t\":{},\"kind\":\"whisper\",\"dir\":\"in\",\"from\":\"{}\",\"text\":\"{}\"}}",
                ts_ms,
                json_escape(&from),
                json_escape(&msg)
            ));
        }
    }

    /// 0x0a[0b 00] 그룹 채팅 — "이름> 내용"
    fn on_group_msg(&mut self, ts_ms: u64, d: &[u8], out: &mut Vec<String>) {
        let Some(t) = sub_text(d) else { return };
        let t = strip_markup(&t);
        let Some(p) = t.find("> ") else { return };
        let from = t[..p].trim().to_string();
        let msg = t[p + 2..].trim_start().to_string();
        if from.is_empty() || msg.is_empty() {
            return;
        }
        // 내가 보낸 그룹채팅(C2S 0x19 + 채널 마커 0x2121)의 에코면 → 내 이름 학습 + out 판정
        let mut is_out = self.my_name.as_deref() == Some(from.as_str());
        if !is_out {
            if let Some(idx) = self.pending_group.iter().position(|(ts, tx)| {
                ts_ms.saturating_sub(*ts) <= 3_000 && *tx == msg
            }) {
                self.pending_group.remove(idx);
                is_out = true;
                if self.my_name.is_none() {
                    self.my_name = Some(from.clone());
                }
            }
        }
        if is_out {
            // 과거에 그룹원으로 잘못 들어간 내 이름 제거
            self.group_players.retain(|n| n != &from);
        } else if !self.group_players.iter().any(|n| n == &from) && self.group_players.len() < 64 {
            self.group_players.push(from.clone());
            out.push(format!(
                "{{\"type\":\"ev\",\"t\":{},\"kind\":\"group_member\",\"name\":\"{}\"}}",
                ts_ms,
                json_escape(&from)
            ));
        }
        self.chats += 1;
        out.push(format!(
            "{{\"type\":\"ev\",\"t\":{},\"kind\":\"gchat\",\"out\":{},\"player\":\"{}\",\"text\":\"{}\"}}",
            ts_ms,
            is_out,
            json_escape(&from),
            json_escape(&msg)
        ));
    }

    /// 0x0a[1c 00] 통합 채팅 — "[통합] 이름 : 내용"
    fn on_global_msg(&mut self, ts_ms: u64, d: &[u8], out: &mut Vec<String>) {
        self.on_channel_text(ts_ms, d, "[통합]", 100, out);
    }

    /// 0x0a[1e 00] 연합 채팅 — "[연합] 이름 : 내용"
    fn on_union_msg(&mut self, ts_ms: u64, d: &[u8], out: &mut Vec<String>) {
        self.on_channel_text(ts_ms, d, "[연합]", 102, out);
    }

    /// 0x0a[21 00] 길드 채팅 — "[길드] 이름 : 내용" (색상 마크업 포함)
    fn on_guild_msg(&mut self, ts_ms: u64, d: &[u8], out: &mut Vec<String>) {
        self.on_channel_text(ts_ms, d, "[길드]", 101, out);
    }

    fn on_channel_text(
        &mut self,
        ts_ms: u64,
        d: &[u8],
        tag: &str,
        ch: u32,
        out: &mut Vec<String>,
    ) {
        let Some(t) = sub_text(d) else { return };
        let t = strip_markup(&t);
        let t = t.strip_prefix(tag).unwrap_or(&t).trim_start();
        let (player, msg) = match t.find(" : ") {
            Some(p) => (
                t[..p].trim().to_string(),
                t[p + 3..].trim_start().to_string(),
            ),
            None => (String::new(), t.to_string()),
        };
        if msg.is_empty() {
            return;
        }
        let mut ch = ch;
        let mut is_out = !player.is_empty() && self.my_name.as_deref() == Some(player.as_str());
        // 길드 채팅(C2S 0x19 마커 21 34)의 서버 에코는 통합(1c)으로 오지만, 길드로 재분류한다.
        if ch == 100 {
            if let Some(idx) = self.pending_guild.iter().position(|(ts, tx)| {
                ts_ms.saturating_sub(*ts) <= 3_000 && *tx == msg
            }) {
                self.pending_guild.remove(idx);
                ch = 101;
                is_out = true;
                if self.my_name.is_none() && !player.is_empty() {
                    self.my_name = Some(player.clone());
                }
            }
        }
        self.chats += 1;
        out.push(format!(
            "{{\"type\":\"ev\",\"t\":{},\"kind\":\"chat\",\"ch\":{},\"out\":{},\"player\":\"{}\",\"text\":\"{}\"}}",
            ts_ms,
            ch,
            is_out,
            json_escape(&player),
            json_escape(&msg)
        ));
    }

    // ---- 0x0f SAddInventoryPacket (item add / stack & charge update) --------
    //
    // 레이아웃: [u8 A][u8 slot][u32 serial][u32 serial2][u16 x]
    //           [u8 nameLen][name CP949][u8 idLen][iid ASCII][...]
    // 값 필드 (best effort):
    //   - POINT:  "POINT" + [u8 len] + "현재/최대" (R포션 등 충전식)
    //   - 스택 수량: 패턴 `05 00 00 00 01 30 00` 뒤 u32 LE (구운빵 등)
    // 같은 serial 재수신은 값이 바뀔 때만 item_upd 로 방출한다.
    fn on_item_add(&mut self, ts_ms: u64, f: &Frame, out: &mut Vec<String>) {
        let d = &f.data;
        if d.len() < 20 {
            return;
        }
        let slot = d[1];
        let serial = u32::from_be_bytes([d[2], d[3], d[4], d[5]]);
        if serial == 0 {
            return;
        }
        let name_len = d[12] as usize;
        if name_len < 1 || name_len > 64 || 13 + name_len + 2 > d.len() {
            return;
        }
        let mut name = String::new();
        let t = cp949::decode(&d[13..13 + name_len]);
        if !t.contains('\u{FFFD}') {
            name = cp949::clean(&t);
        }
        let p = 13 + name_len;
        let mut iid = String::new();
        let idlen = d[p] as usize;
        if idlen >= 2 && idlen <= 48 && p + 1 + idlen <= d.len() {
            let raw = &d[p + 1..p + 1 + idlen];
            if raw.iter().all(|&c| (0x20..0x7f).contains(&c)) {
                iid = raw.iter().map(|&c| c as char).collect();
            }
        }
        if name.is_empty() {
            if let Some(s) = find_best_string(&d[2..], 2, 60) {
                name = s;
            }
        }
        if name.is_empty() && iid.is_empty() {
            return;
        }
        let (cur, max) = find_point_value(d);
        let count = find_stack_count(d);

        let kind;
        let is_new;
        match self.inv.get_mut(&serial) {
            Some(st) => {
                let changed = st.slot != slot
                    || st.count != count
                    || st.cur != cur
                    || st.max != max;
                st.last_ms = ts_ms;
                if !changed {
                    return;
                }
                if !name.is_empty() && name != st.name {
                    st.name = name.clone();
                }
                if !iid.is_empty() && iid != st.iid {
                    st.iid = iid.clone();
                }
                st.slot = slot;
                st.count = count;
                st.cur = cur;
                st.max = max;
                st.updates += 1;
                kind = "item_upd";
                is_new = false;
            }
            None => {
                if self.inv.len() < ITEM_CAP {
                    self.inv.insert(
                        serial,
                        InvState {
                            name: name.clone(),
                            iid: iid.clone(),
                            slot,
                            count,
                            cur,
                            max,
                            last_ms: ts_ms,
                            updates: 1,
                        },
                    );
                }
                kind = "item";
                is_new = true;
            }
        }

        if is_new {
            self.items_total += 1;
            let key = if !iid.is_empty() {
                iid.clone()
            } else {
                name.clone()
            };
            match self.items.get_mut(&key) {
                Some(e) => {
                    e.count += 1;
                    e.last_ms = ts_ms;
                    if e.name.is_empty() {
                        e.name = name.clone();
                    }
                }
                None => {
                    if self.items.len() < ITEM_CAP {
                        self.item_order.push(key.clone());
                        self.items.insert(
                            key.clone(),
                            ItemAgg {
                                name: name.clone(),
                                iid: iid.clone(),
                                count: 1,
                                first_ms: ts_ms,
                                last_ms: ts_ms,
                            },
                        );
                    }
                }
            }
        } else {
            self.inv_upds += 1;
        }

        let mut extra = String::new();
        if let Some(c) = count {
            extra.push_str(&format!(",\"count\":{}", c));
        }
        if let Some(c) = cur {
            extra.push_str(&format!(",\"cur\":{}", c));
        }
        if let Some(m) = max {
            extra.push_str(&format!(",\"max\":{}", m));
        }
        out.push(format!(
            "{{\"type\":\"ev\",\"t\":{},\"kind\":\"{}\",\"name\":\"{}\",\"iid\":\"{}\",\"slot\":{},\"ser\":{}{}}}",
            ts_ms,
            kind,
            json_escape(&name),
            json_escape(&iid),
            slot,
            serial,
            extra
        ));
    }

    // ---- 0x5f SDieObjectsPacket (kill) --------------------------------------
    fn on_death(&mut self, ts_ms: u64, f: &Frame, out: &mut Vec<String>) {
        if f.data.len() < 4 {
            return;
        }
        let obj = u32::from_be_bytes([f.data[0], f.data[1], f.data[2], f.data[3]]);
        self.kills += 1;
        self.cur_k += 1;
        let name = self
            .spawn_names
            .get(&obj)
            .map(|(n, _)| n.clone())
            .unwrap_or_default();
        if !name.is_empty() {
            *self.mob_kills.entry(name.clone()).or_insert(0) += 1;
            if !self.mob_order.contains(&name) {
                if self.mob_order.len() >= MOB_CAP {
                    self.mob_order.remove(0);
                }
                self.mob_order.push(name.clone());
            }
        }
        out.push(format!(
            "{{\"type\":\"ev\",\"t\":{},\"kind\":\"kill\",\"obj\":\"{:08x}\",\"mob\":\"{}\",\"kills\":{}}}",
            ts_ms,
            obj,
            json_escape(&name),
            self.kills
        ));
    }

    // ---- 0x07 SDrawObjectsPacket (spawn: learn objId -> name) ---------------
    fn on_draw_objects(&mut self, ts_ms: u64, f: &Frame) {
        let d = &f.data;
        if d.len() < 12 {
            return;
        }
        let obj = u32::from_be_bytes([d[7], d[8], d[9], d[10]]);
        if obj == 0 {
            return;
        }
        if let Some(name) = find_best_string(&d[11..], 2, 40) {
            if self.spawn_names.len() >= SPAWN_CAP {
                // evict oldest
                if let Some(k) = self
                    .spawn_names
                    .iter()
                    .min_by_key(|(_, (_, t))| *t)
                    .map(|(k, _)| *k)
                {
                    self.spawn_names.remove(&k);
                }
            }
            self.spawn_names.insert(obj, (name, ts_ms));
        }
    }

    // ---- 0x0d SSayPacket (chat / combat log) --------------------------------
    //
    // 레이아웃: [ch u8][id u32 BE][unk u24][len u8][text CP949][00 00]
    //   ch=0  일반 채팅 ("닉네임: 내용")
    //   ch=2  전투 스킬 팝업 — 채팅 집계 제외
    //   ch=6  확성기 ("[닉네임]: 내용")
    //   ch=14 기타 채널 ("닉네임: 내용")
    fn on_say(&mut self, ts_ms: u64, f: &Frame, out: &mut Vec<String>) {
        let d = &f.data;
        if d.len() < 10 {
            return;
        }
        let ch = d[0];
        if ch > 0x20 {
            return; // 채팅 타입 바이트가 비정상(오디코드 의심)
        }
        if ch == 2 {
            return; // 전투 로그
        }
        let id = u32::from_be_bytes([d[1], d[2], d[3], d[4]]);
        let tlen = d[8] as usize;
        if tlen == 0 || 9 + tlen > d.len() {
            return;
        }
        let raw = cp949::decode(&d[9..9 + tlen]);
        if !cp949::is_plausible_text(&raw) {
            return; // 키 오동기화(모지바케) 프레임 방지
        }
        let text = strip_markup(&cp949::clean(&raw));
        if text.is_empty() {
            return;
        }
        // 발신자 추출: ch=6 "[닉]: 내용", 그 외 "닉: 내용"
        let (mut player, mut msg) = (String::new(), text.clone());
        if ch == 6 && text.starts_with('[') {
            if let Some(e) = text.find("]: ") {
                player = text[1..e].trim().to_string();
                msg = text[e + 3..].trim_start().to_string();
            }
        } else if let Some(p) = text.find(": ") {
            player = text[..p].trim().to_string();
            msg = text[p + 2..].trim_start().to_string();
        }
        if msg.is_empty() || !cp949::is_plausible_text(&msg) {
            return;
        }
        let mut is_out = !player.is_empty() && self.my_name.as_deref() == Some(player.as_str());
        // 내가 보낸 일반채팅(C2S 0x0e)의 에코면 → 내 이름 학습 + out 판정
        if ch == 0 && !is_out {
            if let Some(idx) = self.pending_sent.iter().position(|(ts, s)| {
                ts_ms.saturating_sub(*ts) <= 3_000 && (msg == *s || text.ends_with(s.as_str()))
            }) {
                self.pending_sent.remove(idx);
                is_out = true;
                if self.my_name.is_none() && !player.is_empty() {
                    self.my_name = Some(player.clone());
                }
            }
        }
        self.chats += 1;
        out.push(format!(
            "{{\"type\":\"ev\",\"t\":{},\"kind\":\"chat\",\"ch\":{},\"id\":{},\"out\":{},\"player\":\"{}\",\"text\":\"{}\"}}",
            ts_ms,
            ch,
            id,
            is_out,
            json_escape(&player),
            json_escape(&msg)
        ));
    }

    // ---- 0x2c/0x2d lasting spells (buffs) -----------------------------------
    //
    // 0x2c: [00 xx][u24][u16][u16][u8 nameLen][name CP949][u8 idLen][iid ASCII]...
    //       끝 12바이트 트레일러 = [u16][u16 BE 남은시간][u16][u16 BE][u32]
    //       남은시간 단위는 버프별로 다름(초/0.5초 등) → Δ값/Δt(rate)로 잔여 추정.
    // 0x2d: 5바이트 동기화 잡음이 대부분 — 이름이 파싱될 때만 만료 처리.
    fn on_buff(&mut self, ts_ms: u64, f: &Frame, on: bool, out: &mut Vec<String>) {
        let Some((name, iid, val)) = parse_buff_frame(&f.data) else {
            return;
        };
        if name.is_empty() {
            return;
        }
        let key = if iid.is_empty() { name.clone() } else { iid.clone() };
        let mut rate = 0.0f64;
        let mut remain: Option<f64> = None;
        if on {
            rate = match self.buff_state.get(&key).cloned() {
                Some((_, pv, pts, prate)) => {
                    let dt = (ts_ms.saturating_sub(pts)) as f64 / 1000.0;
                    if dt >= 0.5 && val > 0 && pv > val {
                        let r = (pv - val) as f64 / dt;
                        if prate > 0.0 {
                            prate * 0.5 + r * 0.5
                        } else {
                            r
                        }
                    } else {
                        prate // 동일값(갱신) 또는 증가(리셋) — 기존 rate 유지
                    }
                }
                None => 0.0,
            };
            if rate > 0.0 && val > 0 {
                remain = Some(val as f64 / rate);
            } else if val >= 120 {
                // 단일 프레임 + 초 단위 값으로 보이는 경우(헌신 스크롤 등) — 폴백
                remain = Some(val as f64);
            }
            self.buff_state
                .insert(key.clone(), (name.clone(), val, ts_ms, rate));
        } else {
            self.buff_state.remove(&key);
        }
        self.buffs += 1;
        let mut extra = String::new();
        if val > 0 {
            extra.push_str(&format!(",\"val\":{}", val));
        }
        if rate > 0.0 {
            extra.push_str(&format!(",\"rate\":{:.3}", rate));
        }
        if let Some(r) = remain {
            extra.push_str(&format!(",\"remain\":{:.0}", r));
        }
        out.push(format!(
            "{{\"type\":\"ev\",\"t\":{},\"kind\":\"buff\",\"on\":{},\"name\":\"{}\",\"iid\":\"{}\"{}}}",
            ts_ms,
            on,
            json_escape(&name),
            json_escape(&iid),
            extra
        ));
    }

    // ---- 0x3a 파티원 상태 ----------------------------------------------------
    //
    // [7바이트 헤더: 05 08 ...] + 반복 { [u8 nameLen][name CP949][8바이트] }.
    // 8바이트 트레일러 실측: 00 00 00 HP% 00 00 00 MP%  (예: 00 00 00 3e 00 00 00 43)
    // 파티 인원수에 따라 전체 길이가 달라진다(3명=56B, 4명=65B). 끝까지 정확히 맞을 때만 채택.
    fn on_party(&mut self, ts_ms: u64, f: &Frame, out: &mut Vec<String>) {
        let d = &f.data;
        if d.len() < 20 || d[0] != 0x05 {
            return;
        }
        // 헤더: [05][가변 1B][u32][u8 인원수] — 두 번째 바이트는 파티/지역에 따라 08/0a 등으로 변한다.
        let count = d[6] as usize;
        if !(1..=15).contains(&count) {
            return;
        }
        let mut p = 7usize;
        let mut entries: Vec<(String, u8, u8)> = Vec::new();
        while p < d.len() {
            let nl = d[p] as usize;
            if nl == 0 || nl > 24 || p + 1 + nl + 8 > d.len() {
                return; // 구조 검증 실패 → 부분 방출 금지
            }
            let name = cp949::clean(&cp949::decode(&d[p + 1..p + 1 + nl]));
            let hp = d[p + 1 + nl + 3];
            let mp = d[p + 1 + nl + 7];
            p += 1 + nl + 8;
            // 0x3a 는 다른 하위 형식과 섞여 들어오는 경우가 있어, 한글 2~8자만 파티원으로 인정한다.
            // (한 글자라도 비한글이면 프레임 전체를 폐기 → 오탐 명단 방지)
            let chars: Vec<char> = name.chars().collect();
            if chars.len() < 2
                || chars.len() > 8
                || !chars.iter().all(|c| ('\u{AC00}'..='\u{D7A3}').contains(c))
            {
                return;
            }
            entries.push((name, hp, mp));
        }
        if p != d.len() || entries.len() != count {
            return;
        }
        let mut current: Vec<String> = Vec::new();
        for (name, hp, mp) in entries {
            if Some(name.clone()) == self.my_name {
                continue;
            }
            if current.iter().any(|n| n == &name) {
                continue;
            }
            current.push(name.clone());
            out.push(format!(
                "{{\"type\":\"ev\",\"t\":{},\"kind\":\"party\",\"player\":\"{}\",\"hp\":{},\"mp\":{}}}",
                ts_ms,
                json_escape(&name),
                hp,
                mp
            ));
        }
        // 0x3a 프레임은 "나를 제외한 현재 파티원 전원"을 담으므로, 최신 프레임으로 명단을 교체한다.
        // (탈퇴/교체된 멤버가 목록에 남지 않도록)
        for name in &current {
            if !self.group_players.iter().any(|n| n == name) {
                out.push(format!(
                    "{{\"type\":\"ev\",\"t\":{},\"kind\":\"group_member\",\"name\":\"{}\"}}",
                    ts_ms,
                    json_escape(name)
                ));
            }
        }
        self.group_players = current;
    }

    // ---- C2S 0x0e (일반 채팅 전송) — 에코 매칭용 기록 -----------------------
    fn on_chat_send(&mut self, ts_ms: u64, f: &Frame) {
        let d = &f.data;
        if d.len() < 3 || d[0] != 0x00 {
            return;
        }
        let l = d[1] as usize;
        if l == 0 || 2 + l > d.len() {
            return;
        }
        let t = cp949::clean(&cp949::decode(&d[2..2 + l]));
        if t.is_empty() {
            return;
        }
        self.pending_sent.push((ts_ms, t));
        if self.pending_sent.len() > 8 {
            self.pending_sent.remove(0);
        }
    }

    // ---- C2S 0x19 (귓말 전송) — 에코 방향 판정용 기록 -----------------------
    //
    // 레이아웃: [u16 BE nameLen][name CP949][u8 textLen][text CP949]
    fn on_whisper_send(&mut self, ts_ms: u64, f: &Frame) {
        let d = &f.data;
        if d.len() < 4 {
            return;
        }
        let nl = ((d[0] as usize) << 8) | d[1] as usize;
        if nl == 0 || nl > 64 || 2 + nl + 1 > d.len() {
            return;
        }
        // 대상 이름 자리에 21 21(채널 마커)이 오면 귓말이 아니라 그룹 채팅 전송이다.
        // 0x19 는 두 용도로 공유된다: [00 02][21 21][len][text]
        if nl == 2 && d[2] == 0x21 && (d[3] == 0x21 || d[3] == 0x34) {
            let guild = d[3] == 0x34;
            let p = 4;
            if p + 1 <= d.len() {
                let tl = d[p] as usize;
                if tl > 0 && p + 1 + tl <= d.len() {
                    let text = cp949::clean(&cp949::decode(&d[p + 1..p + 1 + tl]));
                    if !text.is_empty() {
                        if guild {
                            self.pending_guild.push((ts_ms, text));
                            if self.pending_guild.len() > 8 {
                                self.pending_guild.remove(0);
                            }
                        } else {
                            self.pending_group.push((ts_ms, text));
                            if self.pending_group.len() > 8 {
                                self.pending_group.remove(0);
                            }
                        }
                    }
                }
            }
            return;
        }
        let to = cp949::clean(&cp949::decode(&d[2..2 + nl]));
        let p = 2 + nl;
        let tl = d[p] as usize;
        if tl == 0 || p + 1 + tl > d.len() {
            return;
        }
        let text = cp949::clean(&cp949::decode(&d[p + 1..p + 1 + tl]));
        if to.is_empty() || text.is_empty() {
            return;
        }
        self.pending_whisper.push((ts_ms, to, text));
        if self.pending_whisper.len() > 8 {
            self.pending_whisper.remove(0);
        }
    }

    // ---- C2S 0x1c CUsePacket (item use) -------------------------------------
    fn on_use(&mut self, ts_ms: u64, f: &Frame, out: &mut Vec<String>) {
        let d = &f.data;
        if d.len() < 2 || d[0] != 0x01 {
            return;
        }
        let slot = d[1];
        self.uses += 1;
        out.push(format!(
            "{{\"type\":\"ev\",\"t\":{},\"kind\":\"use\",\"slot\":{}}}",
            ts_ms, slot
        ));
    }

    // ---- C2S 0x87 CMarketPacket ---------------------------------------------
    fn on_market_req(&mut self, ts_ms: u64, f: &Frame, out: &mut Vec<String>) {
        let d = &f.data;
        if d.is_empty() {
            return;
        }
        let act = d[0];
        let mut q = String::new();
        if act == 3 && d.len() > 6 {
            let t = longest_text(&d[6..], 64);
            q = t;
        }
        let extra = if !q.is_empty() {
            format!(",\"q\":\"{}\"", json_escape(&q))
        } else {
            String::new()
        };
        out.push(format!(
            "{{\"type\":\"ev\",\"t\":{},\"kind\":\"market_req\",\"act\":{}{}}}",
            ts_ms,
            act,
            extra
        ));
    }

    fn on_market_res(&mut self, ts_ms: u64, f: &Frame, out: &mut Vec<String>) {
        let d = &f.data;
        if d.is_empty() {
            return;
        }
        let act = d[0];
        out.push(format!(
            "{{\"type\":\"ev\",\"t\":{},\"kind\":\"market_res\",\"act\":{},\"len\":{}}}",
            ts_ms,
            act,
            d.len()
        ));
        if act == 3 && d.len() > 60 {
            self.parse_market_search(ts_ms, d, out);
        }
    }

    /// 0x91 act=3(검색 응답) — 매물 목록에서 아이템명/iid/가격(최저)을 추출.
    /// 실측 구조: 매물마다 [u16 nameLen][name CP949][u8 iidLen][iid ASCII] ... [u64 BE price][u32 count].
    /// 가격 필드는 앞 3바이트가 0이고 뒤에 count(>=1)가 오는 패턴으로 찾는다.
    fn parse_market_search(&self, ts_ms: u64, d: &[u8], out: &mut Vec<String>) {
        let mut entries: Vec<(String, String, usize)> = Vec::new();
        let mut p = 0usize;
        while p + 2 < d.len() {
            let nl = d[p] as usize;
            if (2..=40).contains(&nl) && p + 1 + nl < d.len() {
                let name = cp949::clean(&cp949::decode(&d[p + 1..p + 1 + nl]));
                let hangul = name
                    .chars()
                    .filter(|c| ('\u{AC00}'..='\u{D7A3}').contains(c))
                    .count();
                if hangul >= 2 && cp949::is_plausible_text(&name) {
                    let q = p + 1 + nl;
                    let il = d[q] as usize;
                    if (2..=40).contains(&il) && q + 1 + il <= d.len() {
                        let iidb = &d[q + 1..q + 1 + il];
                        if iidb.iter().all(|&c| (0x20..0x7f).contains(&c))
                            && iidb.first() == Some(&b'i')
                        {
                            let iid = iidb.iter().map(|&c| c as char).collect::<String>();
                            entries.push((name, iid, q + 1 + il));
                            p = q + 1 + il;
                            continue;
                        }
                    }
                }
            }
            p += 1;
        }
        let mut best: Option<(String, String, u64)> = None;
        for (idx, (name, iid, start)) in entries.iter().enumerate() {
            let end = entries.get(idx + 1).map(|e| e.2).unwrap_or(d.len());
            let mut j = *start;
            while j + 7 <= end {
                if d[j] != 0 {
                    j += 1;
                    continue;
                }
                let mut k = j;
                while k < end && d[k] == 0 {
                    k += 1;
                }
                // 가격 필드: 4바이트 이상 연속 0 뒤의 u32(가격) + 3바이트 수량(1~100).
                // (0 바이트를 5개→4개로 잘못 세면 256으로 나뉜 값이 잡힌다 — 실측 5억2천 → 200만 버그)
                if k - j >= 4 && k + 7 <= end {
                    let price = u32::from_be_bytes(d[k..k + 4].try_into().unwrap()) as u64;
                    let cnt = ((d[k + 4] as u32) << 16) | ((d[k + 5] as u32) << 8) | d[k + 6] as u32;
                    let ascii_all = d[k..k + 4].iter().all(|&c| (0x20..0x7f).contains(&c));
                    if (1_000..=2_000_000_000).contains(&price)
                        && (1..=100).contains(&cnt)
                        && !ascii_all
                        && best.as_ref().map_or(true, |b| price < b.2)
                    {
                        best = Some((name.clone(), iid.clone(), price));
                    }
                }
                j = k;
            }
        }
        if let Some((name, iid, price)) = best {
            out.push(format!(
                "{{\"type\":\"ev\",\"t\":{},\"kind\":\"market\",\"item\":\"{}\",\"iid\":\"{}\",\"price\":{},\"listings\":{}}}",
                ts_ms,
                json_escape(&name),
                json_escape(&iid),
                price,
                entries.len()
            ));
        }
    }

    /// 0x44 (NDW 계열) — 길이 프리픽스 문자열에서 한글 미션/이벤트 문구를 추출.
    /// 구조가 UI 정의 데이터와 섞여 있어 "감지된 문구" 수준으로만 제공한다.
    fn on_mission(&mut self, ts_ms: u64, f: &Frame, out: &mut Vec<String>) {
        let d = &f.data;
        if d.len() < 8 {
            return;
        }
        let mut p = 0usize;
        while p + 1 < d.len() {
            let nl = d[p] as usize;
            if nl >= 2 && nl <= 80 && p + 1 + nl <= d.len() {
                let raw = cp949::decode(&d[p + 1..p + 1 + nl]);
                let t = strip_markup(&cp949::clean(&raw));
                let hangul = t
                    .chars()
                    .filter(|c| ('\u{AC00}'..='\u{D7A3}').contains(c))
                    .count();
                if hangul >= 2 && t.chars().count() <= 120 && cp949::is_plausible_text(&t) {
                    let text = if t.chars().count() > 80 {
                        t.chars().take(80).collect::<String>()
                    } else {
                        t
                    };
                    if self.mission_seen.insert(text.clone()) {
                        out.push(format!(
                            "{{\"type\":\"ev\",\"t\":{},\"kind\":\"mission\",\"text\":\"{}\"}}",
                            ts_ms,
                            json_escape(&text)
                        ));
                    }
                }
                p += 1 + nl;
            } else {
                p += 1;
            }
        }
    }

    // ---- periodic status -----------------------------------------------------
    pub fn status(&mut self, ts_ms: u64) -> Vec<String> {
        let mut out = Vec::new();
        self.ensure_start(ts_ms);
        self.close_minutes(ts_ms, &mut out);
        let sec = (ts_ms.saturating_sub(self.start_ms)) / 1000;
        let sec_f = if sec == 0 { 1.0 } else { sec as f64 };
        let gph = (self.gold as f64) * 3600.0 / sec_f;
        let eph = (self.exp as f64) * 3600.0 / sec_f;
        // 5-minute pace (gold earned in the last 300s window)
        let mut pace_gold = 0u64;
        if let Some(&(last_ts, last_total)) = self.pace.last() {
            for &(ts, total) in self.pace.iter().rev() {
                if last_ts.saturating_sub(ts) > 300_000 {
                    break;
                }
                pace_gold = last_total - total;
            }
        }
        let pace_ph = if sec >= 60 { (pace_gold as f64) * 3600.0 / 300.0 } else { 0.0 };
        let mut top_items: Vec<(String, u64, String)> = self
            .items
            .values()
            .map(|e| (e.name.clone(), e.count, e.iid.clone()))
            .collect();
        top_items.sort_by(|a, b| b.1.cmp(&a.1));
        top_items.truncate(8);
        let items_json = top_items
            .iter()
            .map(|(n, c, i)| format!("[\"{}\",{},\"{}\"]", json_escape(n), c, json_escape(i)))
            .collect::<Vec<_>>()
            .join(",");
        let mut top_mobs: Vec<(String, u64)> = self
            .mob_kills
            .iter()
            .map(|(k, v)| (k.clone(), *v))
            .collect();
        top_mobs.sort_by(|a, b| b.1.cmp(&a.1));
        top_mobs.truncate(8);
        let mobs_json = top_mobs
            .iter()
            .map(|(n, c)| format!("[\"{}\",{}]", json_escape(n), c))
            .collect::<Vec<_>>()
            .join(",");
        // live inventory state: most recently updated items (for consumable monitoring)
        let mut inv_list: Vec<&InvState> = self.inv.values().collect();
        inv_list.sort_by(|a, b| b.last_ms.cmp(&a.last_ms));
        inv_list.truncate(20);
        let inv_json = inv_list
            .iter()
            .map(|st| {
                format!(
                    "[\"{}\",\"{}\",{},{},{},{}]",
                    json_escape(&st.name),
                    json_escape(&st.iid),
                    st.slot,
                    opt_json(st.count),
                    opt_json(st.cur),
                    opt_json(st.max)
                )
            })
            .collect::<Vec<_>>()
            .join(",");
        out.push(format!(
            "{{\"type\":\"status\",\"t\":{},\"sec\":{},\"gold\":{},\"exp\":{},\"fame\":{},\"kills\":{},\"items\":{},\"goldMsgs\":{},\"expMsgs\":{},\"fameMsgs\":{},\"itemMsgs\":{},\"chats\":{},\"uses\":{},\"buffMsgs\":{},\"repaired\":{},\"gph\":{:.0},\"eph\":{:.0},\"paceGold\":{},\"paceGph\":{:.0},\"framesS2c\":{},\"framesC2s\":{},\"invCount\":{},\"inv\":[{}],\"topItems\":[{}],\"topMobs\":[{}],\"groupPlayers\":[{}]}}",
            ts_ms,
            sec,
            self.gold,
            self.exp,
            self.fame,
            self.kills,
            self.items_total,
            self.gold_msgs,
            self.exp_msgs,
            self.fame_msgs,
            self.item_msgs,
            self.chats,
            self.uses,
            self.buffs,
            self.repaired,
            gph,
            eph,
            pace_gold,
            pace_ph,
            self.frames_s2c,
            self.frames_c2s,
            self.inv.len(),
            inv_json,
            items_json,
            mobs_json,
            self.group_players
                .iter()
                .map(|n| format!("\"{}\"", json_escape(n)))
                .collect::<Vec<_>>()
                .join(",")
        ));
        out
    }

    pub fn summary_fields(&self) -> String {
        format!(
            "  \"agg_gold\": {},\n  \"agg_exp\": {},\n  \"agg_fame\": {},\n  \"agg_kills\": {},\n  \"agg_items\": {},\n  \"agg_item_upds\": {},\n  \"agg_inv\": {},\n  \"agg_item_msgs\": {},\n  \"agg_chats\": {},\n  \"agg_uses\": {},\n  \"agg_buffs\": {},\n  \"agg_repaired\": {}",
            self.gold, self.exp, self.fame, self.kills, self.items_total,
            self.inv_upds, self.inv.len(), self.item_msgs, self.chats, self.uses, self.buffs,
            self.repaired
        )
    }
}

// ---- parsing helpers --------------------------------------------------------

/// "12,345 글로드 획득했습니다" -> 12345
fn num_before(text: &str, token: &str) -> Option<u64> {
    let pos = text.find(token)?;
    let prefix = &text[..pos];
    let mut digits = String::new();
    for c in prefix.chars().rev() {
        if c == ' ' && digits.is_empty() {
            continue;
        }
        if c.is_ascii_digit() || c == ',' {
            digits.push(c);
        } else {
            break;
        }
    }
    let s: String = digits.chars().rev().filter(|c| *c != ',').collect();
    if s.is_empty() {
        None
    } else {
        s.parse().ok()
    }
}

/// "xp포인트 획득: 12345" -> 12345
fn amount_after(text: &str, token: &str) -> Option<u64> {
    let pos = text.find(token)?;
    let after = &text[pos + token.len()..];
    let mut digits = String::new();
    for c in after.chars() {
        if c.is_ascii_digit() || c == ',' {
            digits.push(c);
        } else if c == ':' || c == ' ' {
            if digits.is_empty() {
                continue;
            }
            break;
        } else {
            break;
        }
    }
    let s: String = digits.chars().filter(|c| *c != ',').collect();
    if s.is_empty() {
        None
    } else {
        s.parse().ok()
    }
}

/// "{=cFF8626일진기사님이 빛바랜 패밀리어의 영혼을 1개를 획득하였습니다.{=d"
/// -> (color, player, item, count)
fn parse_item_msg(text: &str) -> Option<(String, String, String, u64)> {
    let mut color = String::new();
    let mut s = text;
    if let Some(p) = s.find("{=c") {
        if s.len() >= p + 9 {
            color = s[p + 3..p + 9].to_string();
        }
        s = &s[p + 9..];
    }
    if let Some(p) = s.find("{=d") {
        s = &s[..p];
    }
    let d_pos = s.find("님이")?;
    let player = s[..d_pos].trim().to_string();
    if player.is_empty() || player.chars().count() > 16 {
        return None;
    }
    let rest = s[d_pos + "님이".len()..].trim_start();
    let g_idx = if let Some(i) = rest.find("개를 획득") {
        i
    } else if let Some(i) = rest.find("개 획득") {
        i
    } else {
        return None;
    };
    // count digits right before "개"
    let bytes = rest.as_bytes();
    let mut j = g_idx;
    let mut digits = String::new();
    while j > 0 && (bytes[j - 1].is_ascii_digit() || bytes[j - 1] == b',') {
        j -= 1;
    }
    digits.push_str(&rest[j..g_idx]);
    let count: u64 = digits
        .chars()
        .filter(|c| *c != ',')
        .collect::<String>()
        .parse()
        .unwrap_or(1);
    let mut item = rest[..j].trim_end().to_string();
    for suf in ["을", "를", "이", "가"] {
        if item.ends_with(suf) {
            item.truncate(item.len() - suf.len());
            break;
        }
    }
    let item = item.trim().to_string();
    if item.is_empty() || item.chars().count() > 40 {
        return None;
    }
    Some((color, player, item, count))
}

/// Find the best length-prefixed CP949 string in `data` (>=2 Hangul, no invalid bytes).
/// Used for mob names (0x07) and buff names (0x2c/0x2d).
/// 0x0a 서브타입(실측된 것) — 복구 검증용
fn known_msg_subtype(sub: u16) -> bool {
    matches!(
        sub,
        0x0000
            | 0x0003
            | 0x0005
            | 0x0008
            | 0x000b
            | 0x0018
            | 0x0019
            | 0x001a
            | 0x001c
            | 0x001e
            | 0x0021
            | 0x0022
            | 0x0023
    )
}

/// 0x0a 채팅 계열 본문: [sub u16][len u8][text] → text
fn sub_text(d: &[u8]) -> Option<String> {
    if d.len() < 4 {
        return None;
    }
    let l = d[2] as usize;
    if l == 0 || 3 + l > d.len() {
        return None;
    }
    let t = cp949::decode(&d[3..3 + l]);
    if !cp949::is_plausible_text(&t) {
        return None;
    }
    Some(cp949::clean(&t))
}

/// "{=cXXXXXX", "{=d" 등 색상 마크업 제거
fn strip_markup(s: &str) -> String {
    let mut out = String::with_capacity(s.len());
    let mut it = s.chars().peekable();
    while let Some(c) = it.next() {
        if c == '{' && it.peek() == Some(&'=') {
            let mut n = 0;
            while let Some(c2) = it.next() {
                n += 1;
                if c2 == '}' || n > 12 {
                    break;
                }
            }
        } else {
            out.push(c);
        }
    }
    out
}

/// CP949 텍스트 품질 검증: 한글이 섞여 있고, 오염(한자/미할당) 비율이 낮은지.
/// 모지바케(오디코드) 프레임 판별에 사용.
fn text_ok(bytes: &[u8]) -> bool {
    if bytes.len() < 4 {
        return false;
    }
    let s = cp949::decode(bytes);
    let mut good = 0usize;
    let mut bad = 0usize;
    let mut hangul = 0usize;
    for c in s.chars() {
        let u = c as u32;
        if ('\u{AC00}'..='\u{D7A3}').contains(&c) {
            good += 1;
            hangul += 1;
        } else if u < 0x80
            || (0x3131..=0x318F).contains(&u)
            || (0xA0..=0xFF).contains(&u)
            || (0x2010..=0x27BF).contains(&u)
            || (0x3000..=0x30FF).contains(&u)
            || (0xFF00..=0xFFEF).contains(&u)
        {
            good += 1;
        } else {
            bad += 1; // 한자/미할당 — 오염 후보
        }
    }
    good >= 4 && bad * 4 <= good + bad && hangul >= 1
}

/// 프레임이 복구 없이 그대로 쓸 수 있는지(구조 검증).
/// false 이고 복구 가능한 op 면 repair_frame 시도.
/// 값(골드/수량) 정확성이 중요한 프레임은 복구 대상에서 제외한다.
fn frame_ok(op: u8, d: &[u8]) -> bool {
    match op {
        0x0a => {
            if d.len() < 4 {
                return false;
            }
            let sub = u16::from_le_bytes([d[0], d[1]]);
            if matches!(sub, 0x0000 | 0x000b | 0x001c | 0x001e) {
                let l = d[2] as usize;
                if l == 0 || 3 + l > d.len() {
                    return false;
                }
                text_ok(&d[3..3 + l])
            } else {
                // 알려진 비채팅 서브타입 = 그대로 사용(오복구 방지), 미지 = 복구 시도
                known_msg_subtype(sub)
            }
        }
        0x0d => {
            if d.len() < 10 {
                return false;
            }
            let ch = d[0];
            if ch == 2 {
                return true;
            }
            if ch > 0x20 {
                return false;
            }
            let l = d[8] as usize;
            if l == 0 || 9 + l > d.len() {
                return false;
            }
            text_ok(&d[9..9 + l])
        }
        0x44 => d.windows(3).any(|w| w == b"NDW") || text_ok(d),
        _ => true,
    }
}

/// 서버 카운터 경계에서 앞 9바이트가 0xFF 반전된 프레임 복구.
/// 채팅(0x0a 채팅 서브타입, 0x0d, 0x44)만 대상 — 값 프레임은 오복구 방지 위해 제외.
fn repair_frame(f: &Frame) -> Option<Frame> {
    let d = &f.data;
    if d.len() < 9 {
        return None;
    }
    let mut v = d.clone();
    for b in v.iter_mut().take(9) {
        *b ^= 0xff;
    }
    let ok = match f.op {
        0x0a => {
            if v.len() < 4 {
                false
            } else {
                let sub = u16::from_le_bytes([v[0], v[1]]);
                let l = if matches!(sub, 0x0000 | 0x000b | 0x001c | 0x001e) {
                    v[2] as usize
                } else {
                    0
                };
                l > 0 && 3 + l <= v.len() && text_ok(&v[3..3 + l])
            }
        }
        0x0d => {
            if v.len() < 10 {
                false
            } else {
                let ch = v[0];
                let l = v[8] as usize;
                ch <= 0x20 && ch != 2 && l > 0 && 9 + l <= v.len() && text_ok(&v[9..9 + l])
            }
        }
        0x44 => v.windows(3).any(|w| w == b"NDW"),
        _ => false,
    };
    if ok {
        Some(Frame {
            op: f.op,
            ctr: f.ctr,
            clr: f.clr,
            data: v,
        })
    } else {
        None
    }
}

/// 0x2c 버프 프레임 → (name, iid, val).
/// val = 끝에서 (len-10..len-8) u16 BE (만료 카운트다운; 단위는 버프별 상이).
fn parse_buff_frame(d: &[u8]) -> Option<(String, String, u16)> {
    let n = d.len();
    if n < 16 {
        return None;
    }
    // 이름 = [u8 nameLen][name CP949]; 정규 위치는 9 ([00 xx][u24][u16][u16]).
    // 8부터 스캔하되, 정규 위치이거나 뒤에 ASCII iid 가 검증될 때만 채택한다.
    let mut name = String::new();
    let mut iid = String::new();
    let mut i = 8usize;
    while i < (n - 1).min(44) {
        let l = d[i] as usize;
        if (3..=48).contains(&l) && i + 1 + l <= n {
            let t = cp949::decode(&d[i + 1..i + 1 + l]);
            let hangul = t
                .chars()
                .filter(|c| ('\u{AC00}'..='\u{D7A3}').contains(c))
                .count();
            if !t.contains('\u{FFFD}') && cp949::is_plausible_text(&t) && hangul >= 2 {
                let p = i + 1 + l;
                let mut cand_iid = String::new();
                let mut id_ok = false;
                if p < n {
                    let il = d[p] as usize;
                    if (1..=48).contains(&il) && p + 1 + il <= n {
                        let raw = &d[p + 1..p + 1 + il];
                        if raw.iter().all(|c| (0x20..0x7f).contains(c)) {
                            cand_iid = String::from_utf8_lossy(raw).to_string();
                            id_ok = !cand_iid.is_empty();
                        }
                    }
                }
                if id_ok || i == 9 {
                    name = cp949::clean(&t);
                    iid = cand_iid;
                    break;
                }
            }
        }
        i += 1;
    }
    if name.is_empty() {
        return None;
    }
    let mut val = u16::from_be_bytes([d[n - 10], d[n - 9]]);
    let v2 = u16::from_be_bytes([d[n - 6], d[n - 5]]);
    if val == 0 && v2 > 0 {
        val = v2;
    }
    Some((name, iid, val))
}

fn find_best_string(data: &[u8], min_len: usize, max_len: usize) -> Option<String> {
    let mut best: Option<(usize, String)> = None;
    let n = data.len();
    let mut i = 0;
    while i < n {
        let l = data[i] as usize;
        if l >= min_len && l <= max_len && i + 1 + l <= n {
            let t = cp949::decode(&data[i + 1..i + 1 + l]);
            let hangul = t
                .chars()
                .filter(|c| ('\u{AC00}'..='\u{D7A3}').contains(c))
                .count();
            let bad = t.chars().filter(|c| *c == '\u{FFFD}').count();
            if bad == 0 && hangul >= 2 {
                let has_space = t.contains(' ') as usize;
                let score = hangul * 2 + t.chars().count() + has_space * 4;
                if best.is_none() || score > best.as_ref().unwrap().0 {
                    best = Some((score, cp949::clean(&t)));
                }
            }
        }
        i += 1;
    }
    best.map(|(_, s)| s)
}

/// Longest valid CP949 run starting at `data[0]` (up to `max` bytes).
fn longest_text(data: &[u8], max: usize) -> String {
    let end = max.min(data.len());
    for k in (2..=end).rev() {
        let t = cp949::decode(&data[..k]);
        let bad = t.chars().filter(|c| *c == '\u{FFFD}').count();
        let ctrl = t.chars().filter(|c| (*c as u32) < 0x20 && *c != ' ').count();
        if bad == 0 && ctrl == 0 {
            let clean = cp949::clean(&t);
            if clean.chars().count() >= 2 {
                return clean;
            }
        }
    }
    String::new()
}

/// "POINT" + [u8 len] + "현재/최대" (ASCII digits) → (cur, max).
/// 예: R포션 `POINT\x0e505945/1000000` → (505945, 1000000)
fn find_point_value(data: &[u8]) -> (Option<u64>, Option<u64>) {
    let n = data.len();
    if n < 8 {
        return (None, None);
    }
    let mut i = 0;
    while i + 6 <= n {
        if &data[i..i + 5] == b"POINT" {
            let lp = i + 5;
            let l = data[lp] as usize;
            if (3..=24).contains(&l) && lp + 1 + l <= n {
                let raw = &data[lp + 1..lp + 1 + l];
                if raw.iter().all(|&c| c.is_ascii_digit() || c == b'/') {
                    if let Some(sl) = raw.iter().position(|&c| c == b'/') {
                        let cur = parse_digits_u64(&raw[..sl]);
                        let max = parse_digits_u64(&raw[sl + 1..]);
                        if cur.is_some() && max.is_some() {
                            return (cur, max);
                        }
                    }
                }
            }
        }
        i += 1;
    }
    (None, None)
}

/// 스택 수량: 프레임 꼬리의 `00 00 00 01 30 [hi] [lo] 01 00 00` 패턴에서
/// 수량 = u16 **BE**(hi, lo) — 구운빵 262(0x0106), 현신 스크롤 1(0x0001), 호크마 300(0x012C).
/// (구버전은 뒤의 `01` 고정 바이트를 LE 상위바이트로 읽어 255 이하 수량을 +256 오표시했다.)
/// 뒤에서부터 마지막으로 성립하는 패턴을 채택 — R포션 꼬리처럼 접미가 다른 레이아웃은 None.
fn find_stack_count(data: &[u8]) -> Option<u64> {
    const PAT: [u8; 5] = [0x00, 0x00, 0x00, 0x01, 0x30];
    let n = data.len();
    if n < 11 {
        return None;
    }
    let mut found: Option<u64> = None;
    for i in 0..=n - 8 {
        if n >= i + 10 && data[i..i + 5] == PAT && data[i + 7] == 0x01 && data[i + 8] == 0x00
            && data[i + 9] == 0x00
        {
            let v = ((data[i + 5] as u64) << 8) | data[i + 6] as u64;
            if v > 0 {
                found = Some(v);
            }
        }
    }
    found
}

fn parse_digits_u64(b: &[u8]) -> Option<u64> {
    if b.is_empty() || b.len() > 15 {
        return None;
    }
    let mut v: u64 = 0;
    for &c in b {
        if !c.is_ascii_digit() {
            return None;
        }
        v = v * 10 + (c - b'0') as u64;
    }
    Some(v)
}

fn opt_json(v: Option<u64>) -> String {
    match v {
        Some(x) => x.to_string(),
        None => "null".to_string(),
    }
}

// ---- offline verification: feed saved frame dumps through the aggregator ----

/// `asgard-core agg-replay <session_dir>` — frames_s2c.bin/frames_c2s.bin 를
/// 그대로 집계기에 흘려 결과 카운터를 출력한다 (수집 세션 검증용).
pub fn cli_replay(args: &[String]) -> i32 {
    let a = crate::util::parse_args(args);
    let dir = match a.pos.first() {
        Some(d) => d.clone(),
        None => {
            eprintln!("usage: asgard-core agg-replay <session_dir>");
            return 1;
        }
    };
    let mut agg = Agg::new();
    let mut out: Vec<String> = Vec::new();
    let mut ts_us: u64 = 1_700_000_000_000_000;
    let mut n_s2c = 0u64;
    let mut n_c2s = 0u64;
    for (file, dirn) in [("frames_s2c.bin", Dir::S2C), ("frames_c2s.bin", Dir::C2S)] {
        let path = format!("{}\\{}", dir, file);
        let data = match std::fs::read(&path) {
            Ok(d) => d,
            Err(e) => {
                eprintln!("read {}: {}", path, e);
                return 1;
            }
        };
        let mut i = 0usize;
        while i + 3 <= data.len() {
            let op = data[i];
            let len = u16::from_le_bytes([data[i + 1], data[i + 2]]) as usize;
            if i + 3 + len > data.len() {
                break;
            }
            let f = Frame {
                op,
                ctr: 0,
                clr: false,
                data: data[i + 3..i + 3 + len].to_vec(),
            };
            ts_us += 5_000; // 5ms/프레임 (카운터 검증용 근사 타임라인)
            agg.on_frame(dirn, ts_us / 1000, &f, &mut out);
            if dirn == Dir::S2C {
                n_s2c += 1;
            } else {
                n_c2s += 1;
            }
            i += 3 + len;
        }
    }
    let lines = agg.status(ts_us / 1000);
    println!("frames: s2c={} c2s={} events={}", n_s2c, n_c2s, out.len());
    println!("{}", agg.summary_fields().replace('\n', " "));
    for l in &lines {
        if l.contains("\"type\":\"status\"") {
            println!("{}", l);
        }
    }
    let mut hist: HashMap<String, u64> = HashMap::new();
    for l in &out {
        if let Some(k) = kind_of(l) {
            *hist.entry(k).or_insert(0) += 1;
        }
    }
    let mut hv: Vec<(String, u64)> = hist.into_iter().collect();
    hv.sort_by(|a, b| b.1.cmp(&a.1));
    for (k, n) in hv {
        println!("ev {:<12} {}", k, n);
    }
    0
}

fn kind_of(line: &str) -> Option<String> {
    let p = line.find("\"kind\":\"")? + 8;
    let e = line[p..].find('"')? + p;
    Some(line[p..e].to_string())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn item_msg_parse() {
        let t = "G{=cFF8626일진기사님이 빛바랜 패밀리어의 영혼을 1개를 획득하였습니다.{=d";
        let (c, p, i, n) = parse_item_msg(t).unwrap();
        assert_eq!(c, "FF8626");
        assert_eq!(p, "일진기사");
        assert_eq!(i, "빛바랜 패밀리어의 영혼");
        assert_eq!(n, 1);
    }

    #[test]
    fn item_msg_parse2() {
        let t = "{=cda70d6시도니아님이 호크마 스톤을 1개 획득하였습니다.{=d";
        let (c, p, i, n) = parse_item_msg(t).unwrap();
        assert_eq!(c, "da70d6");
        assert_eq!(p, "시도니아");
        assert_eq!(i, "호크마 스톤");
        assert_eq!(n, 1);
    }

    #[test]
    fn num_helpers() {
        assert_eq!(num_before("14173 글로드 획득했습니다", "글로드 획득했습니다"), Some(14173));
        assert_eq!(amount_after("xp포인트 획득: 123067", "xp포인트 획득"), Some(123067));
        assert_eq!(num_before("1,234,567 글로드 획득했습니다", "글로드 획득했습니다"), Some(1234567));
    }

    #[test]
    fn plausible_text_filter() {
        // 정상 한국어/기호
        assert!(cp949::is_plausible_text("구운빵 3개 획득! ★ [C등급]"));
        assert!(cp949::is_plausible_text("다크_엔젤: 안녕하세요~ ㅋㅋ"));
        // 모지바케(키 오동기화) — 아랍/라틴확장/U+FFFD 포함
        assert!(!cp949::is_plausible_text("?? ǹ?W6q??"));
        assert!(!cp949::is_plausible_text("SY <9M \\ ٦ _Q>GaXd"));
        assert!(!cp949::is_plausible_text("ab\u{FFFD}cd"));
    }

    // 실제 watch3 캡처에서 추출한 프레임 (iFood06 구운빵, iCashUse176_14 R포션)
    const BREAD_HEX: &str = concat!(
        "010875ade72a824dffffff0006b1b8bfeebba70769466f6f6430360100000000",
        "00000000000000000000000000fffffefffffffeffff00000000000000000000",
        "0000000000000000000000000000000000000000000000000000000000000000",
        "0000000000000000000000000000000000000000000000000000000000000000",
        "00000000000000000005000000013000d3010000",
    );
    const POTION_HEX: &str = concat!(
        "012175ade6fa8716ffffff000f5b48502f313030b8b85d52c6f7bcc70e694361",
        "73685573653137365f313401009ba1581c000000000000000000000000000001",
        "0000000100000000000000000000000000000000000000000000000000000000",
        "0000000000000000000000000000000000000000000000000000000000000000",
        "0000000000000000000000000000000000000000000000000005000000013000",
        "01010105504f494e540e3530353934352f3130303030303000",
    );

    fn hexv(s: &str) -> Vec<u8> {
        let clean: String = s.chars().filter(|c| c.is_ascii_hexdigit()).collect();
        (0..clean.len() / 2)
            .map(|i| u8::from_str_radix(&clean[i * 2..i * 2 + 2], 16).unwrap())
            .collect()
    }

    #[test]
    fn inventory_bread_and_potion() {
        let bread = hexv(BREAD_HEX);
        assert_eq!(bread.len(), 148);
        let mut agg = Agg::new();
        let mut out = Vec::new();
        let f = Frame {
            op: 0x0f,
            ctr: 0,
            clr: false,
            data: bread.clone(),
        };
        agg.on_frame(Dir::S2C, 1000, &f, &mut out);
        assert_eq!(out.len(), 1, "first frame should emit item");
        assert!(out[0].contains("\"kind\":\"item\""));
        assert!(out[0].contains("\"name\":\"구운빵\""));
        assert!(out[0].contains("\"iid\":\"iFood06\""));
        assert!(out[0].contains("\"count\":211"));
        // 동일 값 재수신 → 무시
        agg.on_frame(Dir::S2C, 2000, &f, &mut out);
        assert_eq!(out.len(), 1);
        // 수량 감소 → item_upd
        let mut bread2 = bread.clone();
        bread2[144] = 0xd2; // 211 -> 210
        let f2 = Frame {
            op: 0x0f,
            ctr: 0,
            clr: false,
            data: bread2,
        };
        agg.on_frame(Dir::S2C, 3000, &f2, &mut out);
        assert_eq!(out.len(), 2);
        assert!(out[1].contains("\"kind\":\"item_upd\""));
        assert!(out[1].contains("\"count\":210"));
        // status 에 inv 반영
        let lines = agg.status(4000);
        let st = lines
            .iter()
            .find(|l| l.contains("\"type\":\"status\""))
            .unwrap();
        assert!(st.contains("\"inv\":[[\"구운빵\",\"iFood06\",8,210,null,null]]"));

        // R포션: POINT 충전값 파싱 (505945/1000000)
        let potion = hexv(POTION_HEX);
        assert_eq!(potion.len(), 185);
        let fp = Frame {
            op: 0x0f,
            ctr: 0,
            clr: false,
            data: potion,
        };
        let mut out2 = Vec::new();
        agg.on_frame(Dir::S2C, 5000, &fp, &mut out2);
        assert_eq!(out2.len(), 1);
        assert!(out2[0].contains("\"kind\":\"item\""));
        assert!(out2[0].contains("[HP/100만]R포션"));
        assert!(out2[0].contains("\"iid\":\"iCashUse176_14\""));
        assert!(out2[0].contains("\"cur\":505945"));
        assert!(out2[0].contains("\"max\":1000000"));
        assert!(!out2[0].contains("\"count\":"));
    }

    #[test]
    fn stack_count_be_model() {
        // hunt3/hunt1 실캡처 꼬리 12바이트 — 수량 = u16 BE
        let cases: &[(&str, u64)] = &[
            ("000500000001300106010000", 262), // hunt3 구운빵 (이미지 262 일치)
            ("000500000001300001010000", 1),   // 현신 유지 스크롤
            ("000200000001300003010000", 3),   // [펫]테이밍아이템
            ("00050000000130012c010000", 300), // 호크마 스톤
            ("000500000001300080010000", 128), // hunt1 구운빵
        ];
        for (tail, want) in cases {
            assert_eq!(find_stack_count(&hexv(tail)), Some(*want), "tail {}", tail);
        }
        // 접미가 `01 00 00`이 아닌 레이아웃(R포션 POINT 꼬리)은 수량 없음
        assert_eq!(find_stack_count(&hexv("0500000001300001010105504f494e54")), None);
    }

    // ---- hunt3/hunt1 실캡처 프레임 (채팅/버프/복구) -------------------------
    const WHISPER_HEX: &str = "000013b9d9b5e5c0b1c7cf3e20c5d7bdbac6aeb1d331";
    const WHISPER_REPLY_HEX: &str = "000011bfa9c7c1b7ce3e20c5d7bdbac6aeb1d331";
    const GROUP_HEX: &str = "0b0026bfacc1d6b3aac7d8bedfc1f63e20b4dcb0d4b4c220bfd62039b7ce20b2f6b4c2b0c7b0a1bfa9";
    const GLOBAL_HEX: &str = "1c003c5bc5ebc7d55d20bcd6c0ccbedf203a20bec6bdbab0a1b8a3b5e5b0a120c0dab2d920b1f4bafdb0c5b8aeb4c2b5a520c0ccc0afb0a120b9bbb1eebfe4";
    const CHAT0_HEX: &str = "0044fe566000000014b9d9b5e5c0b1c7cf3a20c5d7bdbac6aec0cfb9dd0000";
    const SEND0E_HEX: &str = "000ac5d7bdbac6aec0cfb9dd";
    const SEND19_HEX: &str = "0006bfa9c7c1b7ce09c5d7bdbac6aeb1d331";
    const TALK6_HEX: &str = "06450f03af8cf3ff2b5bbbe7b6f3bfe45d3a203132bcbf377e38c0db20c7d8b9e62fbec7b8f920c0a7c1eeb8b520bbf0b4cfb4d90000";
    const KEEP_HEX: &str = "0000fa4ec303000c000dc7f6bdc520c0afc1f6203130250a614b6565705472616e7300000000000000000000000e1000000e1000000000";

    fn frame_of(op: u8, hex: &str) -> Frame {
        Frame {
            op,
            ctr: 0,
            clr: false,
            data: hexv(hex),
        }
    }

    #[test]
    fn whisper_direction_and_learning() {
        let mut agg = Agg::new();
        let mut out = Vec::new();
        // 내가 여프로에게 귓말 전송 (C2S)
        let send = frame_of(0x19, SEND19_HEX);
        agg.on_frame(Dir::C2S, 1000, &send, &mut out);
        assert!(out.is_empty());
        // 서버 에코: "바드윤하> 테스트귓1" → out + 내 이름 학습
        let echo = frame_of(0x0a, WHISPER_HEX);
        agg.on_frame(Dir::S2C, 1100, &echo, &mut out);
        assert_eq!(out.len(), 1);
        assert!(out[0].contains("\"kind\":\"whisper\""));
        assert!(out[0].contains("\"dir\":\"out\""));
        assert!(out[0].contains("\"peer\":\"여프로\""));
        assert_eq!(agg.my_name.as_deref(), Some("바드윤하"));
        // 상대 답장: "여프로> 테스트귓1" → in
        let reply = frame_of(0x0a, WHISPER_REPLY_HEX);
        agg.on_frame(Dir::S2C, 14000, &reply, &mut out);
        assert_eq!(out.len(), 2);
        assert!(out[1].contains("\"dir\":\"in\""));
        assert!(out[1].contains("\"from\":\"여프로\""));
        assert!(out[1].contains("테스트귓1"));
        assert_eq!(agg.chats, 2);
    }

    #[test]
    fn group_chat_roster() {
        let mut agg = Agg::new();
        let mut out = Vec::new();
        let f = frame_of(0x0a, GROUP_HEX);
        agg.on_frame(Dir::S2C, 1000, &f, &mut out);
        assert_eq!(out.len(), 2, "group_member + gchat");
        assert!(out[0].contains("\"kind\":\"group_member\""));
        assert!(out[0].contains("연주나해야지"));
        assert!(out[1].contains("\"kind\":\"gchat\""));
        assert!(out[1].contains("\"player\":\"연주나해야지\""));
        assert!(out[1].contains("단게는"));
        assert!(agg.group_players.contains(&"연주나해야지".to_string()));
    }

    #[test]
    fn group_chat_send_sets_direction() {
        let mut agg = Agg::new();
        let mut out = Vec::new();
        // C2S 0x19 그룹채팅: [00 02][21 21][len][text] — 대상 자리에 채널 마커
        let send = frame_of(0x19, "000221210abec8b3e7c7cfbcbcbfa9");
        agg.on_frame(Dir::C2S, 1000, &send, &mut out);
        assert!(out.is_empty());
        // 서버 에코(0x0a 0b): "바드윤하> 안녕하세?" — 내 메시지로 판정 + 이름 학습
        let echo = frame_of(0x0a, "0b0014b9d9b5e5c0b1c7cf3e20bec8b3e7c7cfbcbcbfa9");
        agg.on_frame(Dir::S2C, 1500, &echo, &mut out);
        assert_eq!(out.len(), 1, "내 그룹채팅에는 group_member 이벤트가 없어야 함");
        assert!(out[0].contains("\"kind\":\"gchat\""));
        assert!(out[0].contains("\"out\":true"));
        assert_eq!(agg.my_name.as_deref(), Some("바드윤하"));
        assert!(agg.group_players.is_empty());
    }

    #[test]
    fn party_status_names_and_hp() {
        let mut agg = Agg::new();
        let mut out = Vec::new();
        let f = frame_of(
            0x3a,
            "05085ab009000308bfa4b5e5b8aec4a10000003e0000004306b1b3b6f5b2db0000003d0000004308b9cdbec6b9cdbdba0000003700000043",
        );
        agg.on_frame(Dir::S2C, 1000, &f, &mut out);
        assert_eq!(out.len(), 6, "group_member 3 + party 3");
        assert!(out.iter().any(|l| l.contains("\"kind\":\"party\"")
            && l.contains("\"player\":\"엘드리치\"")
            && l.contains("\"hp\":62")
            && l.contains("\"mp\":67")));
        assert_eq!(agg.group_players.len(), 3);
    }

    #[test]
    fn party_status_variable_length() {
        let mut agg = Agg::new();
        let mut out = Vec::new();
        // 파티 4명(나 제외) = 69바이트 — 길이 고정 가정 없이 끝까지 파싱해야 한다.
        let f = frame_of(
            0x3a,
            "05085ab009000408bfa4b5e5b8aec4a1000000420000004306b1b3b6f5b2db000000380000004208b9cdbec6b9cdbdba000000370000004306c5d7bdbac6ae0000002a0000002f",
        );
        agg.on_frame(Dir::S2C, 1000, &f, &mut out);
        assert_eq!(out.len(), 8);
        assert_eq!(agg.group_players.len(), 4);
        assert!(out.iter().any(|l| l.contains("\"kind\":\"party\"")
            && l.contains("\"player\":\"테스트\"")
            && l.contains("\"hp\":42")
            && l.contains("\"mp\":47")));
    }

    #[test]
    fn mission_text_parse_dedup() {
        let mut agg = Agg::new();
        let mut out = Vec::new();
        let f = frame_of(
            0x44,
            "0200034e4457085ab0090003044e616d650009bfc3c0cebff820c6d1045469746c65000e3136363539203136373131393335034a6f620009bfc3c0cebff820c6d1",
        );
        agg.on_frame(Dir::S2C, 1000, &f, &mut out);
        assert!(out
            .iter()
            .any(|l| l.contains("\"kind\":\"mission\"") && l.contains("올인원 팩")));
        let before = out.len();
        agg.on_frame(Dir::S2C, 2000, &f, &mut out);
        assert_eq!(out.len(), before, "동일 문구는 중복 방출하지 않는다");
    }

    #[test]
    fn market_search_price_parse() {
        let mut agg = Agg::new();
        let mut out = Vec::new();
        let f = frame_of(
            0x91,
            "0300000000006ad62eef0000000300000000fbe4c723fbfbfbfbe5fe920000000001010000000088db53a8ee00145bb3b25db3fab7e6c0c7c7d8b9e6b5c8b7cebaea10694443325f535f526f62654d30315f4c0100000000000000000000000000000000000000010000000106323236375e31ffffffffffffffff06054a5b000008db000000020000000000000000010053a8eeffffffffffffffff0000000000000000000000000000000000000000000000000000000000000000000000000000000000000105010000064e6f4e616d650002000000000000006401000100000000001f3da00000000147d3570000000001010000000088db53a8ee00145bb3b25db3fab7e6c0c7c7d8b9e6b5c8b7cebaea1069444332",
        );
        agg.on_frame(Dir::S2C, 1000, &f, &mut out);
        assert!(out.iter().any(|l| l.contains("\"kind\":\"market\"")
            && l.contains("뇌룡의")
            && l.contains("\"price\":524132352")));
    }

    #[test]
    fn party_status_alt_header() {
        let mut agg = Agg::new();
        let mut out = Vec::new();
        // 8인 파티 실측 헤더: 05 0a ... (두 번째 바이트가 08이 아님) + 인원수 03
        let f = frame_of(
            0x3a,
            "050a9d8bcf000304c6e4b8a3000000350000002c06b1b3b6f5b2db000000380000002a08bfa4b5e5b8aec4a10000003c0000002f",
        );
        agg.on_frame(Dir::S2C, 1000, &f, &mut out);
        assert_eq!(out.len(), 6);
        assert!(out.iter().any(|l| l.contains("\"player\":\"페르\"")
            && l.contains("\"hp\":53")
            && l.contains("\"mp\":44")));
        assert_eq!(agg.group_players.len(), 3);
    }

    #[test]
    fn global_chat_parse() {
        let mut agg = Agg::new();
        let mut out = Vec::new();
        let f = frame_of(0x0a, GLOBAL_HEX);
        agg.on_frame(Dir::S2C, 1000, &f, &mut out);
        assert_eq!(out.len(), 1);
        assert!(out[0].contains("\"kind\":\"chat\""));
        assert!(out[0].contains("\"ch\":100"));
        assert!(out[0].contains("\"player\":\"솔이야\""));
        assert!(out[0].contains("아스가르드가"));
        assert!(!out[0].contains("[통합]"));
    }

    #[test]
    fn general_chat_echo_and_my_name() {
        let mut agg = Agg::new();
        let mut out = Vec::new();
        let send = frame_of(0x0e, SEND0E_HEX);
        agg.on_frame(Dir::C2S, 1000, &send, &mut out);
        let echo = frame_of(0x0d, CHAT0_HEX);
        agg.on_frame(Dir::S2C, 1100, &echo, &mut out);
        assert_eq!(out.len(), 1);
        assert!(out[0].contains("\"kind\":\"chat\""));
        assert!(out[0].contains("\"ch\":0"));
        assert!(out[0].contains("\"out\":true"));
        assert!(out[0].contains("\"player\":\"바드윤하\""));
        assert!(out[0].contains("\"text\":\"테스트일반\""));
        assert_eq!(agg.my_name.as_deref(), Some("바드윤하"));
    }

    #[test]
    fn broadcast_ch6_parse() {
        let mut agg = Agg::new();
        let mut out = Vec::new();
        let f = frame_of(0x0d, TALK6_HEX);
        agg.on_frame(Dir::S2C, 1000, &f, &mut out);
        assert_eq!(out.len(), 1);
        assert!(out[0].contains("\"ch\":6"));
        assert!(out[0].contains("\"player\":\"사라요\""));
        assert!(out[0].contains("해방/악몽 위즈링"));
        assert!(!out[0].contains("[사라요]"));
    }

    #[test]
    fn buff_keep_trans_parse() {
        let mut agg = Agg::new();
        let mut out = Vec::new();
        let f = frame_of(0x2c, KEEP_HEX);
        agg.on_frame(Dir::S2C, 1000, &f, &mut out);
        assert_eq!(out.len(), 1);
        assert!(out[0].contains("\"kind\":\"buff\""));
        assert!(out[0].contains("\"name\":\"현신 유지 10%\""));
        assert!(out[0].contains("\"iid\":\"aKeepTrans\""));
        assert!(out[0].contains("\"val\":3600"));
        assert!(out[0].contains("\"remain\":3600"));
        assert_eq!(agg.buffs, 1);
    }

    #[test]
    fn buff_rate_estimate() {
        let mut agg = Agg::new();
        let mut out = Vec::new();
        let f1 = frame_of(0x2c, KEEP_HEX);
        agg.on_frame(Dir::S2C, 1000, &f1, &mut out);
        // 5초 뒤 값 3600 → 3584 (rate ≈ 3.2/s)
        let mut d2 = hexv(KEEP_HEX);
        d2[45] = 0x0e;
        d2[46] = 0x00;
        let f2 = Frame {
            op: 0x2c,
            ctr: 0,
            clr: false,
            data: d2,
        };
        agg.on_frame(Dir::S2C, 6001, &f2, &mut out);
        assert_eq!(out.len(), 2);
        assert!(out[1].contains("\"val\":3584"));
        assert!(out[1].contains("\"rate\":"));
        assert!(out[1].contains("\"remain\":"));
    }

    #[test]
    fn repair_inverted_chat() {
        // hunt3 실사례: ctr=0 경계에서 첫 9바이트가 0xFF 반전된 통합 채팅 프레임
        let g = hexv(GLOBAL_HEX);
        let mut corr = g.clone();
        for b in corr.iter_mut().take(9) {
            *b ^= 0xff;
        }
        let mut agg = Agg::new();
        let mut out = Vec::new();
        let f = frame_of(0x0a, &corr.iter().map(|b| format!("{:02x}", b)).collect::<String>());
        agg.on_frame(Dir::S2C, 1000, &f, &mut out);
        assert_eq!(agg.repaired, 1);
        let ev = out
            .iter()
            .find(|l| l.contains("\"kind\":\"chat\""))
            .expect("복구된 채팅 이벤트");
        assert!(ev.contains("솔이야"));
        assert!(ev.contains("\"ch\":100"));
    }
}
