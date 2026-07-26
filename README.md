# PlayerSkins

[![Release](https://img.shields.io/github/v/release/ANXSFAN/PlayerSkins?display_name=tag&sort=semver)](https://github.com/ANXSFAN/PlayerSkins/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/ANXSFAN/PlayerSkins/total)](https://github.com/ANXSFAN/PlayerSkins/releases)
[![License](https://img.shields.io/github/license/ANXSFAN/PlayerSkins)](LICENSE)
[![Stars](https://img.shields.io/github/stars/ANXSFAN/PlayerSkins?style=flat)](https://github.com/ANXSFAN/PlayerSkins/stargazers)
[![CounterStrikeSharp](https://img.shields.io/badge/CounterStrikeSharp-1.0.371-blue)](https://github.com/roflmuffin/CounterStrikeSharp)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)

给 **真人玩家** 上武器 / 刀 / 手套皮肤的 CounterStrikeSharp 插件，用于 **`-insecure` 离线打人机自用**。

配合 [CS2-Bot-Improver](https://github.com/ed0ard/CS2-Bot-Improver) 使用：它的 `BotRandomizer` 只给 **bot** 上皮肤（v1.4.3 起还有贴纸和挂件），本插件只给 **真人**（`!IsBot`）上皮肤，两者互不冲突。

---

## ⚠️ 安全须知（务必阅读）

- 本插件是 **服务器端** 插件，只在你 **自己当服务器打人机（listen server + `-insecure`）** 时生效。
- **绝对不要带着 Metamod / CounterStrikeSharp 去连 VAC 官方匹配服 —— 会被 VAC 封号。** 这是整套增强人机框架的通用纪律，跟本插件无关。
- 皮肤只存在于你本地服务器的显示，**不进 Steam 库存、不发送到 V 社服务器**，纯离线自娱自乐。
- 联机前请确保 Metamod 未加载（用 CS2-Bot-Improver 的启动器切换模式，或把 `addons` 移出游戏目录）。

> 一句话：**只在 `-insecure` 打人机时用，别碰联机服。**

---

## 前提条件

目标机需已安装 **CS2-Bot-Improver v1.4.2 或 v1.4.3**（即 Metamod + **CounterStrikeSharp 1.0.371**）。
本插件在 **CounterStrikeSharp 1.0.371 / net10.0** 上测试通过；CSSharp 版本差太多可能需要重新编译（见下方「从源码编译」）。

---

## 安装（普通用户）

1. 从 [Releases](../../releases) 下载 `PlayerSkins.zip`。
2. 解压得到 `PlayerSkins` 文件夹，整个放到：
   ```
   ...\Counter-Strike Global Offensive\game\csgo\addons\counterstrikesharp\plugins\
   ```
   最终结构：
   ```
   plugins\PlayerSkins\
     ├─ PlayerSkins.dll
     ├─ PlayerSkins.deps.json
     ├─ skins_en.json       (legacy 老模型皮肤位置判断)
     ├─ skins_db.json       (!skinsearch 皮肤名称库)
     ├─ stickers_db.json    (!stickersearch 贴纸名称库)
     └─ configs\            (运行时自动生成：每位玩家一份 <SteamID>.json)
   ```
3. 启动打人机（开图自动加载），或在服务器控制台执行：
   ```
   css_plugins reload PlayerSkins
   ```

---

## 指令

聊天框输入 `!`，或服务器控制台输入 `css_`。

所有指令都作用于 **当前手持的武器**（`!knife` / `!gloves` 除外）。

### 基础
| 指令 | 说明 | 例子 |
|---|---|---|
| `!skin <代号>` | 手持武器上皮肤 | 拿着 AK 输 `!skin 180` |
| `!knife <刀名\|defindex> [代号]` | 换刀（模型 + 皮肤） | `!knife karambit 415` |
| `!gloves <defindex> <代号>` | 换手套 | `!gloves 5030 10048` |
| `!reskin` | 立即重新应用全部已保存皮肤 | |
| `!clearskins` | 清空全部皮肤设置 | |

### 进阶（图案 / 磨损 / StatTrak / 品质）
| 指令 | 说明 | 例子 |
|---|---|---|
| `!seed <值>` | 图案种子（决定花色，如淬火蓝宝石、渐变%、大理石花纹） | 淬火蓝宝石：`!skin 44` → `!seed 661` |
| `!wear <0-1>` | 磨损（0=崭新，1=战痕） | `!wear 0.0001` |
| `!stattrak <数\|off>` / `!st` | StatTrak 金色计数器 | `!stattrak 1337` / `!st off` |
| `!quality <normal\|stattrak\|souvenir\|star>` | 物品品质（`souvenir`=纪念品金铭牌，如纪念品龙狙） | 拿 AWP `!skin 344` → `!quality souvenir` |

### 贴纸
| 指令 | 说明 | 例子 |
|---|---|---|
| `!sticker <槽0-4> <id\|clear> [磨损] [缩放] [旋转]` | 往 4 个槽位贴/撕贴纸 | `!sticker 0 76`（Titan 全息卡托2014） |
| `!stickerclear` / `!sc` | 清空手持武器全部贴纸 | |
| `!stickersearch <关键词>` / `!sss` | 搜贴纸代号（1万+张，中英文） | `!sss 皇冠` / `!sss katowice 2014` |

> ⚠️ **贴纸的显示时机**：贴纸只在武器模型「完整重建」时才渲染，因此 `!sticker` 后需要 **重进地图**（`map de_dust2` 等，或换图）才会显示——普通重生不刷新。皮肤 / 种子 / StatTrak / 品质这些则重生即生效。

> ⚠️ **换刀须知**：
> - 改刀之前**必须先用 `!knife <刀名>` 选一把真实的刀**（如 `!knife karambit`），不要直接对**默认刀**用 `!skin` / `!seed` 等指令——默认刀无法换皮，强行修改会触发客户端致命断言崩溃（`weapon_knife script file not found`）。本插件已拦截。
> - `!knife` 后 **重生一次** 刀的模型才会正确切换（刀模型和贴纸一样，在武器创建时才构建；局中直接改可能仍显示默认刀模型）。

### 搜索
| 指令 | 说明 | 例子 |
|---|---|---|
| `!skinsearch <关键词>` / `!ss` | 搜皮肤代号（中英文） | `!ss 龙` / `!ss awp dragon` |
| `!stickersearch <关键词>` / `!sss` | 搜贴纸代号 | `!sss titan` |

- 所有选择自动保存到插件目录 `configs/<你的SteamID>.json`，**重启游戏后仍生效**。
- **多人各自独立**：每位真人玩家有自己的一套配置，`!skin` 只改自己的，互不覆盖（朋友连进你的服务器一起打人机也没问题）。
- 若插件目录下存在旧版单文件 `config.json`，它会作为**新玩家的初始模板**（各自拿到独立副本），想从零开始输 `!clearskins` 即可。
- 刀名支持：`karambit` `butterfly` `m9` `bayonet` `flip` `talon` `stiletto` `ursus` `skeleton` `kukri` 等。
- 手套 defindex：`5027`(血猎) `5030`(运动) `5031`(驾驶) `5032`(缠绕) `5033`(摩托) `5034`(专业) `5035`(九头蛇) `4725`(狂牙)。

### 怎么找皮肤代号？

代号 = 皮肤的 **paint_index（图案模板编号）**。三种方式：
1. **局内搜**：`!ss <关键词>`，中英文都能搜，直接返回代号（最省事）。
2. 查 [bymykel.com/CSGO-API](https://bymykel.com/CSGO-API/)，找到皮肤看它的 `paint_index`。
3. 多普勒 / 伽玛多普勒等 **每个相位代号不同**（如爪刀红宝石 415、蓝宝石 416、黑珍珠 417、翡翠 568），认准相位。

---

## 从源码编译（开发者 / 需要适配其它 CSSharp 版本时）

需要 **.NET 10 SDK**（CounterStrikeSharp 1.0.371 目标框架为 net10.0）。

```bash
cd src
dotnet build -c Release
```

编译产物在 `src/bin/Release/net10.0/`，其中 `PlayerSkins.dll` + `PlayerSkins.deps.json` + `skins_en.json` + `skins_db.json` 就是要放进 `plugins/PlayerSkins/` 的全部文件。

- CSSharp 版本在 `src/PlayerSkins.csproj` 的 `PackageReference` 里改。
- 若某次 CS2 更新后皮肤不显示，多半是 CSSharp 核心 gamedata 偏移失效 —— 升级到修复了该版本的 CounterStrikeSharp 并重新编译即可（本插件写皮肤用的内存特征码取自 BotRandomizer，随其更新）。

### 刷新皮肤名称库（`skins_db.json`）

`skins_db.json` 由 [ByMykel/CSGO-API](https://github.com/ByMykel/CSGO-API) 的英文 + 简中 `skins.json` 按 `id` 关联生成（字段：`p`=paint_index, `w`=武器, `en`=英文名, `cn`=中文名）。出新皮肤想更新时，重新拉取两份 skins.json 合并生成即可。

---

## 致谢

- 写皮肤的底层实现思路参考自 [ed0ard/CS2-Bot-Improver](https://github.com/ed0ard/CS2-Bot-Improver) 的 `BotRandomizer`。
- 贴纸 / StatTrak 的属性写法（`ViewAsFloat` 位重解释、`sticker slot N ...` 属性）参考自 [Nereziel/cs2-WeaponPaints](https://github.com/Nereziel/cs2-WeaponPaints)。
- 皮肤 / 贴纸名称与 paint_index 数据来自 [ByMykel/CSGO-API](https://github.com/ByMykel/CSGO-API)。
- 基于 [roflmuffin/CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) 开发。

## 许可证

[MIT](LICENSE)。仅供离线打人机自用娱乐，请勿用于任何 VAC 保护的服务器。
