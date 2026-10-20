"""把 Data/holiday-YYYY.json（holiday-cn 的原始年份文件）合并成插件内置的 Data/holidays.json。

用法：
    python tools/gen-holidays.py

新一年官方公布放假安排后：
    1) 下载该年文件到 Data/，例如
       curl -o Data/holiday-2027.json https://cdn.jsdelivr.net/gh/NateScarlet/holiday-cn@master/2027.json
    2) 重跑本脚本 → 重新打包
"""

import datetime
import glob
import json
import os

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DATA = os.path.join(ROOT, "Data")

years = []
for path in sorted(glob.glob(os.path.join(DATA, "holiday-*.json"))):
    with open(path, encoding="utf-8") as fh:
        raw = json.load(fh)

    if not raw.get("days"):
        print(f"跳过 {os.path.basename(path)}：官方还没公布放假安排")
        continue

    years.append(
        {
            "year": raw["year"],
            "papers": raw.get("papers", []),
            "days": raw["days"],
        }
    )

merged = {
    "_note": (
        "中国法定节假日/调休数据，来源 https://github.com/NateScarlet/holiday-cn"
        "（据国务院办公厅公告生成）。仅供离线兜底，运行期会自动在线更新。"
    ),
    "updated": datetime.date.today().isoformat(),
    "years": years,
}

out = os.path.join(DATA, "holidays.json")
with open(out, "w", encoding="utf-8") as fh:
    json.dump(merged, fh, ensure_ascii=False, indent=1)

print(f"写入 {out}：" + "，".join(f"{y['year']} 年 {len(y['days'])} 天" for y in years))
