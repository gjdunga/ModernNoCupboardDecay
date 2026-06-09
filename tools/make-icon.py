import math, os
from PIL import Image, ImageDraw

S = 1200
img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
d = ImageDraw.Draw(img)
INK = (12, 12, 12, 255)

def toe(cx, cy, w, h, rot):
    p = 40
    tile = Image.new("RGBA", (w + p*2, h + p*2), (0, 0, 0, 0))
    ImageDraw.Draw(tile).ellipse([p, p, p + w, p + h], fill=INK)
    tile = tile.rotate(rot, resample=Image.BICUBIC, expand=True)
    img.alpha_composite(tile, (int(cx - tile.width/2), int(cy - tile.height/2)))

def rounded_polygon(pts, r, steps=16):
    n = len(pts); out = []
    for i in range(n):
        P = pts[i]; A = pts[(i-1) % n]; B = pts[(i+1) % n]
        v1 = (A[0]-P[0], A[1]-P[1]); v2 = (B[0]-P[0], B[1]-P[1])
        l1 = math.hypot(*v1); l2 = math.hypot(*v2)
        u1 = (v1[0]/l1, v1[1]/l1); u2 = (v2[0]/l2, v2[1]/l2)
        ang = math.acos(max(-1, min(1, u1[0]*u2[0]+u1[1]*u2[1])))
        d_ = r / math.tan(ang/2)
        d_ = min(d_, l1/2, l2/2)
        rr = d_ * math.tan(ang/2)
        T1 = (P[0]+u1[0]*d_, P[1]+u1[1]*d_)
        T2 = (P[0]+u2[0]*d_, P[1]+u2[1]*d_)
        bis = (u1[0]+u2[0], u1[1]+u2[1]); bl = math.hypot(*bis)
        bis = (bis[0]/bl, bis[1]/bl)
        C = (P[0]+bis[0]*(rr/math.sin(ang/2)), P[1]+bis[1]*(rr/math.sin(ang/2)))
        a1 = math.atan2(T1[1]-C[1], T1[0]-C[0]); a2 = math.atan2(T2[1]-C[1], T2[0]-C[0])
        while a2 - a1 > math.pi: a2 -= 2*math.pi
        while a2 - a1 < -math.pi: a2 += 2*math.pi
        for s in range(steps+1):
            a = a1 + (a2-a1)*s/steps
            out.append((C[0]+rr*math.cos(a), C[1]+rr*math.sin(a)))
    return out

# four toe pads, fanned
toe(520, 400, 200, 300, 12)
toe(680, 400, 200, 300, -12)
toe(352, 520, 178, 272, 33)
toe(848, 520, 178, 272, -33)

# main metacarpal pad: solid rounded triangle (apex up)
d.polygon(rounded_polygon([(600, 575), (792, 905), (408, 905)], r=82), fill=INK)

# Canonical listing icon: 512x512 wolf paw at templates/icon.png (repo-root relative
# to this script, so it works regardless of the current working directory).
out = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "templates", "icon.png"))
os.makedirs(os.path.dirname(out), exist_ok=True)
img.resize((512, 512), Image.LANCZOS).save(out)
print(f"wrote {out}")
