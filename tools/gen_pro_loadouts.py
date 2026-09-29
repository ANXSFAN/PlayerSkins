# -*- coding: utf-8 -*-
"""
生成 data/pro_loadouts.json：全量职业选手库存预设。

数据源：
  - csskins.wiki/loadout/player      选手列表 + 每人枪皮/刀/手套（服务端渲染 HTML）
  - ByMykel/CSGO-API skins.json      皮肤名 -> paint_index（含多普勒相位）、武器 defindex
  - data/stickers_db.json            自动匹配选手大赛金色签名贴纸（×4）
  - tools/pro_overrides.json         人工覆盖：中文别名/称号/相位/贴纸/挂件（挂件没有公开数据源）

用法（联网，首次约 1-2 分钟，页面缓存在 tools/cache/ 下，重跑不再下载）：
    python tools/gen_pro_loadouts.py            # 增量：用缓存
    python tools/gen_pro_loadouts.py --refresh  # 强制重新下载全部页面
"""
import html
import json
import re
import sys
import time
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
CACHE = Path(__file__).resolve().parent / "cache"
OUT = ROOT / "data" / "pro_loadouts.json"
OVERRIDES_PATH = Path(__file__).resolve().parent / "pro_overrides.json"

BASE = "https://csskins.wiki"
UA = {"User-Agent": "Mozilla/5.0 (PlayerSkins pro_loadouts generator)"}

DEFAULT_WEAR = 0.0001
DEFAULT_DOPPLER_PHASE = "Phase 2"  # 页面不写相位时的默认；个别选手在 overrides 里指定

# ByMykel 数据里手套/刀一般自带 weapon_id；这里是保底表
FALLBACK_DEF = {
    "★ Bayonet": 500, "★ Classic Knife": 503, "★ Flip Knife": 505, "★ Gut Knife": 506,
    "★ Karambit": 507, "★ M9 Bayonet": 508, "★ Huntsman Knife": 509, "★ Falchion Knife": 512,
    "★ Bowie Knife": 514, "★ Butterfly Knife": 515, "★ Shadow Daggers": 516, "★ Paracord Knife": 517,
    "★ Survival Knife": 518, "★ Ursus Knife": 519, "★ Navaja Knife": 520, "★ Nomad Knife": 521,
    "★ Stiletto Knife": 522, "★ Talon Knife": 523, "★ Skeleton Knife": 525, "★ Kukri Knife": 526,
    "★ Bloodhound Gloves": 5027, "★ Sport Gloves": 5030, "★ Driver Gloves": 5031,
    "★ Hand Wraps": 5032, "★ Moto Gloves": 5033, "★ Specialist Gloves": 5034,
    "★ Hydra Gloves": 5035, "★ Broken Fang Gloves": 4725,
}


def fetch(url: str, cache_file: Path, refresh: bool) -> str:
    if cache_file.exists() and not refresh:
        return cache_file.read_text(encoding="utf-8")
    last_ex: Exception | None = None
    for attempt in range(3):
        try:
            req = urllib.request.Request(url, headers=UA)
            text = urllib.request.urlopen(req, timeout=30).read().decode("utf-8")
            cache_file.parent.mkdir(parents=True, exist_ok=True)
            cache_file.write_text(text, encoding="utf-8")
            time.sleep(0.15)  # 别打太快
            return text
        except Exception as ex:
            last_ex = ex
            time.sleep(1.5 * (attempt + 1))
    raise last_ex  # type: ignore[misc]


def load_skins_index(refresh: bool):
    """(武器名小写, 皮肤名小写) -> [(paint_index, phase), ...]；武器名小写 -> defindex"""
    raw = fetch(
        "https://raw.githubusercontent.com/ByMykel/CSGO-API/main/public/api/en/skins.json",
        CACHE / "skins_full.json", refresh)
    skins = json.loads(raw)
    paint_map: dict[tuple[str, str], list[tuple[int, str | None]]] = {}
    def_map: dict[str, int] = {k.lower(): v for k, v in FALLBACK_DEF.items()}
    for e in skins:
        name = e.get("name") or ""
        if " | " not in name or e.get("paint_index") in (None, ""):
            continue
        weapon, skin = (s.strip() for s in name.split(" | ", 1))
        paint_map.setdefault((weapon.lower(), skin.lower()), []).append(
            (int(e["paint_index"]), e.get("phase")))
        wid = (e.get("weapon") or {}).get("weapon_id")
        if wid is not None:
            def_map.setdefault(weapon.lower(), int(wid))
    return paint_map, def_map


def pick_paint(entries: list[tuple[int, str | None]], want_phase: str | None) -> int:
    if len(entries) == 1:
        return entries[0][0]
    phase = want_phase or DEFAULT_DOPPLER_PHASE
    for p, ph in entries:
        if (ph or "").lower() == phase.lower():
            return p
    return entries[0][0]


def load_stickers():
    db = json.loads((ROOT / "data" / "stickers_db.json").read_text(encoding="utf-8-sig"))
    return db


def pick_autograph(stickers, display_name: str) -> int | None:
    """选手大赛签名：金色冠军 > 金色 > 普通签名，都取 id 最大（最新）的一张。"""
    n = display_name.lower()
    champ_gold, gold, plain = [], [], []
    for e in stickers:
        en = (e.get("en") or "")
        low = en.lower()
        if not (low.startswith(n + " (") or low.startswith(n + " |")):
            continue
        if "(gold, champion)" in low:
            champ_gold.append(e["i"])
        elif "(gold" in low:
            gold.append(e["i"])
        elif low.startswith(n + " |"):
            plain.append(e["i"])
    for bucket in (champ_gold, gold, plain):
        if bucket:
            return max(bucket)
    return None


def parse_player_page(html_text: str, weapon_regex: re.Pattern):
    """返回 (display_name, team, [(武器名, 皮肤名) ...])"""
    text = html.unescape(html_text)
    # 面包屑 JSON-LD：position 3 = 队伍，position 4 = 选手正式写法
    team = disp = None
    m = re.search(r'"position":3,"name":"([^"]+)"', text)
    if m and m.group(1) != "Teams":
        team = m.group(1)
    m = re.search(r'"position":4,"name":"([^"]+)"', text)
    if m:
        disp = m.group(1)
    pairs = set()
    for m in weapon_regex.finditer(text):
        weapon, skin = m.group(1).strip(), m.group(2).strip()
        low = skin.lower()
        if "loadout" in low or "pro player" in low or "skins," in low:
            continue
        # 个别页面把武器名重复进皮肤名（如 "Desert Eagle | Desert Eagle Jormungandr"）
        if low.startswith(weapon.lower() + " "):
            skin = skin[len(weapon) + 1:]
        pairs.add((weapon, skin))
    return disp, team, sorted(pairs)


def main():
    refresh = "--refresh" in sys.argv
    overrides = json.loads(OVERRIDES_PATH.read_text(encoding="utf-8")) if OVERRIDES_PATH.exists() else {}
    ov_players = overrides.get("players", {})
    priority = overrides.get("priority", [])

    paint_map, def_map = load_skins_index(refresh)
    stickers = load_stickers()

    # 武器名用 ByMykel 的完整名字列表精确匹配（长名优先，避免把 "Desert Eagle" 截成 "Eagle"）
    weapon_names = sorted({w for w, _ in paint_map}, key=len, reverse=True)
    weapon_regex = re.compile(
        r"(" + "|".join(re.escape(w) for w in weapon_names) + r") \| ([A-Za-z0-9 .'&!?%()-]+)",
        re.IGNORECASE)

    index_html = fetch(f"{BASE}/loadout/player", CACHE / "index.html", refresh)
    slugs = sorted(set(re.findall(r"/loadout/player/([a-z0-9_-]+)", index_html)))
    print(f"共 {len(slugs)} 名选手")

    result, misses = [], []
    for slug in slugs:
        try:
            page = fetch(f"{BASE}/loadout/player/{slug}", CACHE / "players" / f"{slug}.html", refresh)
        except Exception as ex:
            misses.append(f"{slug}: 下载失败 {ex}")
            continue
        disp, team, pairs = parse_player_page(page, weapon_regex)
        disp = disp or slug
        ov = ov_players.get(slug, {})
        phase = ov.get("knife_phase")
        charms = {str(k): v for k, v in (ov.get("charms") or {}).items()}

        cfg: dict = {"Guns": {}}
        for weapon, skin in pairs:
            wl = weapon.lower()
            entries = paint_map.get((wl, skin.lower()))
            if not entries:
                misses.append(f"{slug}: 皮肤没匹配上 -> {weapon} | {skin}")
                continue
            defindex = def_map.get(wl)
            if defindex is None:
                misses.append(f"{slug}: 不认识的武器 -> {weapon}")
                continue
            is_knife = weapon.startswith("★") and "gloves" not in wl and "wraps" not in wl
            is_glove = weapon.startswith("★") and not is_knife
            paint = pick_paint(entries, phase if is_knife else None)
            if is_knife:
                cfg["KnifeDef"] = defindex
                cfg["Knife"] = {"Paint": paint, "Seed": ov.get("knife_seed", 0), "Wear": DEFAULT_WEAR}
            elif is_glove:
                cfg["GloveDef"] = defindex
                cfg["Gloves"] = {"Paint": paint, "Seed": 0, "Wear": DEFAULT_WEAR}
            else:
                lo = {"Paint": paint, "Seed": 0, "Wear": DEFAULT_WEAR}
                cfg["Guns"][str(defindex)] = lo

        if not cfg["Guns"] and "Knife" not in cfg:
            misses.append(f"{slug}: 页面上没解析出任何皮肤，跳过")
            continue

        # 人工补充的武器（如只用 M4A4 的选手补一把 M4A1-S）；放在贴纸之前，让它们也拿到签名
        for d, spec in (ov.get("extra_guns") or {}).items():
            if d not in cfg["Guns"]:
                cfg["Guns"][d] = {"Paint": spec["Paint"], "Seed": spec.get("Seed", 0),
                                  "Wear": spec.get("Wear", DEFAULT_WEAR)}

        sticker_id = ov.get("sticker", pick_autograph(stickers, disp))
        if sticker_id:
            for lo in cfg["Guns"].values():
                lo["Stickers"] = {str(i): {"Id": sticker_id} for i in range(4)}
        for d, cid in charms.items():
            if d in cfg["Guns"]:
                cfg["Guns"][d]["Charm"] = {"Id": cid}

        names = [slug]
        if disp.lower() not in names:
            names.append(disp.lower())
        names += [a for a in ov.get("aliases", []) if a.lower() not in names]
        title = ov.get("title") or (f"{disp} — {team}" if team else disp)
        result.append({"Names": names, "Title": title, "Config": cfg})

    order = {slug: i for i, slug in enumerate(priority)}
    result.sort(key=lambda p: (order.get(p["Names"][0], len(order)), p["Names"][0]))

    OUT.write_text(json.dumps(result, ensure_ascii=False, indent=1), encoding="utf-8")
    print(f"已写入 {OUT}（{len(result)} 名选手，{OUT.stat().st_size // 1024} KB）")
    if misses:
        print(f"\n{len(misses)} 条没匹配上（不影响其余数据）：")
        for m in misses:
            print("  " + m)


if __name__ == "__main__":
    main()
