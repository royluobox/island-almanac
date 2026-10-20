"""生成市场用的 240x240 透明 logo（4 倍超采样后缩放，边缘干净）。

用法：python tools/make-logo.py
输出：logo.png（项目根目录）
"""

from PIL import Image, ImageDraw, ImageFont

S = 960            # 画布（4x）
OUT = 240          # 最终尺寸
R = 208            # 圆角半径（4x）

img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
draw = ImageDraw.Draw(img)

# 圆角矩形底板：竖向渐变 #2E3040 -> #16171E
mask = Image.new("L", (S, S), 0)
ImageDraw.Draw(mask).rounded_rectangle([0, 0, S - 1, S - 1], radius=R, fill=255)

grad = Image.new("RGBA", (S, S))
gd = ImageDraw.Draw(grad)
top = (46, 48, 64)
bottom = (20, 21, 28)
for y in range(S):
    t = y / (S - 1)
    gd.line(
        [(0, y), (S, y)],
        fill=(
            round(top[0] + (bottom[0] - top[0]) * t),
            round(top[1] + (bottom[1] - top[1]) * t),
            round(top[2] + (bottom[2] - top[2]) * t),
            255,
        ),
    )
img.paste(grad, (0, 0), mask)

# 顶部一层极淡高光，避免在深色主题里显得死板
gloss = Image.new("RGBA", (S, S), (0, 0, 0, 0))
ImageDraw.Draw(gloss).rounded_rectangle(
    [24, 24, S - 25, S - 25], radius=R - 20, outline=(255, 255, 255, 26), width=6
)
img.alpha_composite(gloss)

# 主字「历」
font = ImageFont.truetype("C:/Windows/Fonts/msyhbd.ttc", 560)
text = "历"
box = draw.textbbox((0, 0), text, font=font)
tw, th = box[2] - box[0], box[3] - box[1]
tx = (S - tw) / 2 - box[0]
ty = (S - th) / 2 - box[1] - 10
draw.text((tx, ty), text, font=font, fill=(245, 246, 250, 255))

# 右上角小圆点：今天 / 有提醒
accent = (255, 106, 85, 255)
cx, cy, cr = 772, 176, 58
draw.ellipse([cx - cr, cy - cr, cx + cr, cy + cr], fill=accent)

img.resize((OUT, OUT), Image.LANCZOS).save("logo.png")
print("logo.png written:", Image.open("logo.png").size)
