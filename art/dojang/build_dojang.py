# Builds the dojang room around the AvatarQuest digital wall and exports it for Unity.
# Run headless:  blender --background --factory-startup --python art/dojang/build_dojang.py
#
# Coordinates: Blender X = Unity X, Blender Y = Unity Z, Blender Z = Unity Y.
# The digital wall (Unity Space/Wall1: 4 x 2.25 m quad at z=0, bottom on the floor)
# sits in the opening of the front wall at Blender y=0; the room extends toward -Y.
import bpy, bmesh, math, os, random
import numpy as np
from mathutils import Vector

ROOT = os.path.dirname(os.path.abspath(__file__))
PROJ = os.path.abspath(os.path.join(ROOT, "..", "..", "GCT600_Client", "Assets", "Dojang"))
TEX = os.path.join(PROJ, "Textures")
MODELS = os.path.join(PROJ, "Models")
os.makedirs(TEX, exist_ok=True); os.makedirs(MODELS, exist_ok=True)

# ---------------- dimensions (metres) ----------------
W = 7.0          # room width (x)
D = 5.0          # room depth (front wall at y=0, flag wall at y=-D)
H = 2.95         # coffer ceiling height
SOFFIT_Z = 2.62  # underside of perimeter soffit
SOFFIT_W = 0.55  # soffit depth from the wall
SCREEN_W, SCREEN_H = 4.0, 2.25
MAT_T = 0.012    # puzzle mat thickness (top surface just above the existing floor planes)
WALL_T = 0.12
X0, X1 = -W / 2, W / 2

for o in list(bpy.data.objects): bpy.data.objects.remove(o)
for m in list(bpy.data.meshes): bpy.data.meshes.remove(m)

col = bpy.context.scene.collection
MATS = {}
def mat(name, color=(0.9, 0.9, 0.9), emission=None):
    if name in MATS: return MATS[name]
    m = bpy.data.materials.new(name); m.use_nodes = True
    b = m.node_tree.nodes.get("Principled BSDF")
    b.inputs["Base Color"].default_value = (*color, 1)
    if emission:
        b.inputs["Emission Color"].default_value = (*emission, 1)
        b.inputs["Emission Strength"].default_value = 5
    MATS[name] = m; return m

def add_obj(name, bm, material, parent=None):
    me = bpy.data.meshes.new(name); bm.to_mesh(me); bm.free()
    ob = bpy.data.objects.new(name, me); col.objects.link(ob)
    if isinstance(material, (list, tuple)):
        for mm in material: me.materials.append(mm)
    else:
        me.materials.append(material)
    if parent: ob.parent = parent
    return ob

def box_into(bm, mn, mx, uv_scale=1.0):
    """Axis-aligned box with world-scale box-projected UVs."""
    mn, mx = Vector(mn), Vector(mx)
    r = bmesh.ops.create_cube(bm, size=1.0)
    vs = r["verts"]
    for v in vs:
        v.co = Vector((mn.x if v.co.x < 0 else mx.x, mn.y if v.co.y < 0 else mx.y, mn.z if v.co.z < 0 else mx.z))
    return vs

def box(name, mn, mx, material, parent=None):
    bm = bmesh.new(); box_into(bm, mn, mx); return add_obj(name, bm, material, parent)

def boxproject_uv(ob, scale=1.0):
    me = ob.data
    uv = me.uv_layers.new(name="UVMap") if not me.uv_layers else me.uv_layers[0]
    for p in me.polygons:
        n = p.normal
        ax = max(range(3), key=lambda i: abs(n[i]))
        for li in p.loop_indices:
            co = me.vertices[me.loops[li].vertex_index].co
            if ax == 0: u, v = co.y, co.z
            elif ax == 1: u, v = co.x, co.z
            else: u, v = co.x, co.y
            uv.data[li].uv = (u * scale, v * scale)

# ---------------- textures ----------------
def save_png(name, arr, colorspace="sRGB"):
    h, w = arr.shape[:2]
    if arr.shape[2] == 3: arr = np.concatenate([arr, np.ones((h, w, 1), np.float32)], 2)
    img = bpy.data.images.new(name, w, h, alpha=True, float_buffer=False)
    img.colorspace_settings.name = colorspace
    img.pixels.foreach_set(np.clip(arr, 0, 1).astype(np.float32).ravel())
    path = os.path.join(TEX, name + ".png")
    img.filepath_raw = path; img.file_format = "PNG"; img.save()
    return img

def srgb(c): return np.array(c, np.float32) / 255.0

def height_to_normal(hgt, strength):
    gy, gx = np.gradient(hgt)
    n = np.dstack([-gx * strength, -gy * strength, np.ones_like(hgt)])
    n /= np.linalg.norm(n, axis=2, keepdims=True)
    return n * 0.5 + 0.5

def make_mat_textures(ppm=384, tile=1.0):
    """Interlocking EVA puzzle mats: blue field with a one-tile red border."""
    w, h = int(W * ppm), int(D * ppm)
    xs = (np.arange(w) + 0.5) / ppm; ys = (np.arange(h) + 0.5) / ppm
    X, Y = np.meshgrid(xs, ys)                 # X along room width, Y along depth (row 0 = front wall side handled by UV)
    period, amp = 1.0 / 7.0, 0.018
    def teeth(t):                              # trapezoid square wave in [-1, 1]
        ph = (t / period) % 1.0
        return np.clip((np.abs(ph - 0.5) * 4 - 1) * 2.2, -1, 1)
    tx = np.floor((X - amp * teeth(Y)) / tile).astype(int)
    ty = np.floor((Y - amp * teeth(X)) / tile).astype(int)
    nx, ny = int(round(W / tile)), int(round(D / tile))
    tx = np.clip(tx, 0, nx - 1); ty = np.clip(ty, 0, ny - 1)
    border = (tx == 0) | (tx == nx - 1) | (ty == 0) | (ty == ny - 1)
    blue, red = srgb((32, 98, 196)), srgb((214, 58, 52))
    alb = np.where(border[..., None], red, blue).astype(np.float32)
    # per-tile tiny tint variation + fine noise
    rng = np.random.default_rng(7)
    tint = rng.normal(1.0, 0.025, (nx, ny))
    alb *= tint[tx, ty][..., None]
    alb *= (1 + rng.normal(0, 0.012, (h, w)))[..., None]
    # seams: pixels whose tile id differs from a neighbour
    tid = tx * 100 + ty
    seam = np.zeros((h, w), bool)
    seam[:, 1:] |= tid[:, 1:] != tid[:, :-1]; seam[1:, :] |= tid[1:, :] != tid[:-1, :]
    seam2 = seam.copy(); seam2[:, :-1] |= seam[:, 1:]; seam2[:-1, :] |= seam[1:, :]
    alb[seam2] *= 0.55
    # open edges of the outer cut-off teeth along the walls are hidden under the walls
    hgt = np.where(seam2, -1.0, 0.0).astype(np.float32)
    k = np.ones(5) / 5
    for _ in range(2):
        hgt = np.apply_along_axis(lambda r: np.convolve(r, k, "same"), 1, hgt)
        hgt = np.apply_along_axis(lambda c: np.convolve(c, k, "same"), 0, hgt)
    save_png("T_DojangMat_Albedo", alb)
    save_png("T_DojangMat_Normal", height_to_normal(hgt, 3.0), "Non-Color")
    # tileable fine diamond emboss (detail normal) - repeats every 2 cm
    n = 64; u = (np.arange(n) + 0.5) / n
    U, V = np.meshgrid(u, u)
    dmd = np.abs(((U + V) % 0.5) - 0.25) + np.abs(((U - V) % 0.5) - 0.25)
    save_png("T_DojangMat_DetailNormal", height_to_normal(-dmd * 6, 1.0), "Non-Color")

def make_wood_texture():
    w, h = 256, 1024
    rng = np.random.default_rng(3)
    x = np.arange(w)[None, :] / w; y = np.arange(h)[:, None] / h
    warp = 0.03 * np.sin(y * 9 + 1.3) + 0.015 * np.sin(y * 31)
    grain = np.sin((x + warp) * 2 * np.pi * 14 + 3 * np.sin(y * 4)) * 0.5 + 0.5
    fine = rng.normal(0, 1, (1, w)).repeat(h, 0)
    fine = np.convolve(fine[0], np.ones(3) / 3, "same")[None, :].repeat(h, 0)
    t = 0.65 * grain + 0.35 * (fine * 0.5 + 0.5)
    light, dark = srgb((222, 182, 135)), srgb((190, 140, 92))
    alb = light * (1 - t[..., None] * 0.6) + dark * (t[..., None] * 0.6)
    save_png("T_DojangWood_Albedo", alb.astype(np.float32))

make_mat_textures(); make_wood_texture()

# ---------------- materials ----------------
M_WALL = mat("M_Dojang_WallWhite", (0.86, 0.85, 0.83))
M_CEIL = mat("M_Dojang_Ceiling", (0.88, 0.87, 0.86))
M_WOOD = mat("M_Dojang_Wood", (0.75, 0.55, 0.35))
M_MAT = mat("M_Dojang_Mat", (0.2, 0.4, 0.8))
M_BLACK = mat("M_Dojang_BlackMatte", (0.02, 0.02, 0.02))
M_LOGO = mat("M_Dojang_Logo", (0.015, 0.015, 0.015))
M_FLAG = mat("M_Dojang_Flag", (1, 1, 1))
M_LED = mat("M_Dojang_LED", (1, 1, 1), emission=(1.0, 0.86, 0.68))
M_DOWN = mat("M_Dojang_Downlight", (1, 1, 1), emission=(1.0, 0.88, 0.72))
M_DOOR = mat("M_Dojang_Door", (0.80, 0.79, 0.77))
M_METAL = mat("M_Dojang_Metal", (0.6, 0.6, 0.6))

root = bpy.data.objects.new("Dojang", None); col.objects.link(root)

# ---------------- floor: puzzle mats ----------------
bm = bmesh.new()
box_into(bm, (X0, -D, -0.02), (X1, 0, MAT_T))
fl = add_obj("Floor_Mat", bm, M_MAT, root)
uv = fl.data.uv_layers.new(name="UVMap")
for p in fl.data.polygons:
    for li in p.loop_indices:
        co = fl.data.vertices[fl.data.loops[li].vertex_index].co
        uv.data[li].uv = ((co.x - X0) / W, (co.y + D) / D)

# ---------------- walls (boxes outside the room volume) ----------------
sx0, sx1 = -SCREEN_W / 2, SCREEN_W / 2
bm = bmesh.new()
box_into(bm, (X0 - WALL_T, 0, 0), (sx0, WALL_T, H))           # left of screen
box_into(bm, (sx1, 0, 0), (X1 + WALL_T, WALL_T, H))           # right of screen
box_into(bm, (sx0, 0, SCREEN_H), (sx1, WALL_T, H))            # above screen
wf = add_obj("Wall_Front", bm, M_WALL, root)
box("Wall_Back", (X0 - WALL_T, -D - WALL_T, 0), (X1 + WALL_T, -D, H), M_WALL, root)
# right wall has a door opening near the front wall
DOOR_Y0, DOOR_Y1, DOOR_H = -1.55, -0.6, 2.1
bm = bmesh.new()
box_into(bm, (X0 - WALL_T, -D, 0), (X0, 0, H))
lw = add_obj("Wall_Left", bm, M_WALL, root)
bm = bmesh.new()
box_into(bm, (X1, -D, 0), (X1 + WALL_T, DOOR_Y0, H))
box_into(bm, (X1, DOOR_Y1, 0), (X1 + WALL_T, 0, H))
box_into(bm, (X1, DOOR_Y0, DOOR_H), (X1 + WALL_T, DOOR_Y1, H))
rw = add_obj("Wall_Right", bm, M_WALL, root)

# ---------------- ceiling: coffer + perimeter soffit with cove LED ----------------
box("Ceiling_Coffer", (X0 - WALL_T, -D - WALL_T, H), (X1 + WALL_T, WALL_T, H + 0.08), M_CEIL, root)
bm = bmesh.new()
box_into(bm, (X0, -D, SOFFIT_Z), (X1, -D + SOFFIT_W, H))                      # back
box_into(bm, (X0, -SOFFIT_W, SOFFIT_Z), (X1, 0, H))                            # front
box_into(bm, (X0, -D + SOFFIT_W, SOFFIT_Z), (X0 + SOFFIT_W, -SOFFIT_W, H))     # left
box_into(bm, (X1 - SOFFIT_W, -D + SOFFIT_W, SOFFIT_Z), (X1, -SOFFIT_W, H))     # right
sof = add_obj("Ceiling_Soffit", bm, M_CEIL, root)
# cove LED: thin emissive band on the inner faces of the soffit, just below the coffer
LED_T, LED_H = 0.004, 0.03
ix0, ix1, iy0, iy1 = X0 + SOFFIT_W, X1 - SOFFIT_W, -D + SOFFIT_W, -SOFFIT_W
bm = bmesh.new()
z0, z1 = H - 0.06 - LED_H, H - 0.06
# strips sit just proud of the soffit's inner faces (room side), with a 1 mm gap to avoid z-fighting
g = 0.001
box_into(bm, (ix0, iy0 + g, z0), (ix1, iy0 + g + LED_T, z1))
box_into(bm, (ix0, iy1 - g - LED_T, z0), (ix1, iy1 - g, z1))
box_into(bm, (ix0 + g, iy0, z0), (ix0 + g + LED_T, iy1, z1))
box_into(bm, (ix1 - g - LED_T, iy0, z0), (ix1 - g, iy1, z1))
add_obj("LED_Cove", bm, M_LED, root)
# hidden LED lip so the cove line reads as a slot from below
bm = bmesh.new()
box_into(bm, (ix0, iy0, SOFFIT_Z), (ix1, iy0 + 0.02, SOFFIT_Z + 0.05))
box_into(bm, (ix0, iy1 - 0.02, SOFFIT_Z), (ix1, iy1, SOFFIT_Z + 0.05))
box_into(bm, (ix0, iy0, SOFFIT_Z), (ix0 + 0.02, iy1, SOFFIT_Z + 0.05))
box_into(bm, (ix1 - 0.02, iy0, SOFFIT_Z), (ix1, iy1, SOFFIT_Z + 0.05))
add_obj("Ceiling_CoveLip", bm, M_CEIL, root)

# ---------------- downlights (emissive discs; Unity spot lights are placed on these) ----------------
DOWNLIGHTS = []
def downlight(x, y):
    bm = bmesh.new()
    bmesh.ops.create_cone(bm, cap_ends=True, segments=20, radius1=0.045, radius2=0.045, depth=0.006)
    for v in bm.verts: v.co += Vector((x, y, SOFFIT_Z - 0.002))
    DOWNLIGHTS.append((x, y))
    return bm
bm_all = bmesh.new()
def merge(dst, src):
    me = bpy.data.meshes.new("tmp"); src.to_mesh(me); src.free(); dst.from_mesh(me); bpy.data.meshes.remove(me)
for x in np.linspace(-2.6, 2.6, 5): merge(bm_all, downlight(float(x), -0.28))                  # above screen
for x in (-3.15, -2.75, -2.35, -0.9, -0.3, 0.3, 0.9, 2.35, 2.75, 3.15): merge(bm_all, downlight(x, -D + 0.28))  # flag wall
for y in (-1.9, -3.1): merge(bm_all, downlight(X0 + 0.28, y)); merge(bm_all, downlight(X1 - 0.28, y))         # side walls
add_obj("Downlights", bm_all, M_DOWN, root)

# ---------------- digital wall bezel ----------------
BZ, BD = 0.05, 0.035
bm = bmesh.new()
box_into(bm, (sx0 - BZ, -BD, 0), (sx0, 0.0, SCREEN_H + BZ))
box_into(bm, (sx1, -BD, 0), (sx1 + BZ, 0.0, SCREEN_H + BZ))
box_into(bm, (sx0, -BD, SCREEN_H), (sx1, 0.0, SCREEN_H + BZ))
add_obj("Screen_Bezel", bm, M_BLACK, root)

# ---------------- flag wall ----------------
YB = -D
SLAT_X = 2.1                      # inner edge of the slat fields
PANEL_X = 1.3                     # half width of the centre feature panel
PANEL_D = 0.05
bm = bmesh.new()
rng = random.Random(5)
pitch, sw, sd = 0.08, 0.045, 0.03
def slats(xa, xb):
    n = int((xb - xa) / pitch)
    off = (xb - xa - n * pitch) / 2
    for i in range(n):
        x = xa + off + i * pitch + (pitch - sw) / 2
        box_into(bm, (x, YB, 0), (x + sw, YB + sd, SOFFIT_Z))
slats(X0 + 0.02, -SLAT_X); slats(SLAT_X, X1 - 0.02)
sl = add_obj("Wood_Slats", bm, M_WOOD, root)
# UV: grain along the slat length, random offset per slat
uv = sl.data.uv_layers.new(name="UVMap")
for p in sl.data.polygons:
    seed = round(p.center.x / pitch)
    r = random.Random(seed)
    du, dv = r.random(), r.random()
    for li in p.loop_indices:
        co = sl.data.vertices[sl.data.loops[li].vertex_index].co
        n = p.normal
        u = (co.x if abs(n.y) > 0.5 else co.y) / 0.05
        uv.data[li].uv = (u * 0.25 + du, co.z / 2.6 + dv)
# slat backing (darker wall behind the battens reads as shadow in the reference)
box("Wood_SlatBacking_L", (X0, YB, 0), (-SLAT_X, YB + 0.005, SOFFIT_Z), M_WOOD, root)
box("Wood_SlatBacking_R", (SLAT_X, YB, 0), (X1, YB + 0.005, SOFFIT_Z), M_WOOD, root)
# centre feature panel, lifted off the wall with LED edges
box("Feature_Panel", (-PANEL_X, YB, 0), (PANEL_X, YB + PANEL_D, SOFFIT_Z), M_WALL, root)
bm = bmesh.new()
box_into(bm, (-PANEL_X - 0.025, YB, 0), (-PANEL_X - 0.01, YB + 0.02, SOFFIT_Z))
box_into(bm, (PANEL_X + 0.01, YB, 0), (PANEL_X + 0.025, YB + 0.02, SOFFIT_Z))
add_obj("LED_PanelEdges", bm, M_LED, root)

# flag canvas: 3:2, image mapped on the front face (faces +Y, readable from inside the room)
FW, FH, FT = 0.9, 0.6, 0.03
fz = 2.12
fy = YB + PANEL_D
bm = bmesh.new()
box_into(bm, (-FW / 2, fy, fz - FH / 2), (FW / 2, fy + FT, fz + FH / 2))
flag = add_obj("Flag", bm, M_FLAG, root)
uv = flag.data.uv_layers.new(name="UVMap")
for p in flag.data.polygons:
    front = p.normal.y > 0.5
    for li in p.loop_indices:
        co = flag.data.vertices[flag.data.loops[li].vertex_index].co
        if front:
            # viewer faces -Y, so their right is -X: u runs from +X to -X
            uv.data[li].uv = ((FW / 2 - co.x) / FW, (co.z - (fz - FH / 2)) / FH)
        else:
            uv.data[li].uv = (0.01, 0.01)   # sides/back sample the white margin

# logo: traced from the supplied artwork
exec(open(os.path.join(ROOT, "trace_logo.py")).read())
logo, info = trace_logo(os.path.join(ROOT, "logo_source.webp"), width_m=2.0, depth=0.025)
dg = bpy.context.evaluated_depsgraph_get()
me = bpy.data.meshes.new_from_object(logo.evaluated_get(dg))
bpy.data.objects.remove(logo)
lg = bpy.data.objects.new("Logo", me); col.objects.link(lg)
me.materials.clear(); me.materials.append(M_LOGO)
# stand it up facing +Y, reading toward -X
for v in me.vertices:
    x, y, z = v.co
    v.co = Vector((-x, z + PANEL_D + YB + 0.0125, y + 1.33))
lg.parent = root

# ---------------- door (right wall, near the screen) ----------------
dfw = 0.05
bm = bmesh.new()
box_into(bm, (X1 - 0.02, DOOR_Y0 - dfw, 0), (X1 + 0.005, DOOR_Y0, DOOR_H + dfw))
box_into(bm, (X1 - 0.02, DOOR_Y1, 0), (X1 + 0.005, DOOR_Y1 + dfw, DOOR_H + dfw))
box_into(bm, (X1 - 0.02, DOOR_Y0, DOOR_H), (X1 + 0.005, DOOR_Y1, DOOR_H + dfw))
add_obj("Door_Frame", bm, M_DOOR, root)
box("Door_Leaf", (X1 + 0.01, DOOR_Y0, 0.005), (X1 + 0.05, DOOR_Y1, DOOR_H), M_DOOR, root)
bm = bmesh.new()
box_into(bm, (X1 + 0.0, DOOR_Y1 - 0.12, 1.0), (X1 + 0.01, DOOR_Y1 - 0.07, 1.06))
box_into(bm, (X1 - 0.06, DOOR_Y1 - 0.24, 1.02), (X1 + 0.0, DOOR_Y1 - 0.07, 1.04))
add_obj("Door_Handle", bm, M_METAL, root)

# world-scale UVs for plain surfaces
for ob in root.children:
    if ob.type == "MESH" and not ob.data.uv_layers:
        boxproject_uv(ob)

# ---------------- light rig export (positions for Unity) ----------------
import json
rig = {"downlights": [[x, SOFFIT_Z - 0.01, y] for (x, y) in DOWNLIGHTS],   # in Unity coords (x, y, z)
       "cove": {"x0": ix0, "x1": ix1, "z0": iy0, "z1": iy1, "y": H - 0.075},
       "panel_edges": [[-PANEL_X - 0.0175, YB + 0.02], [PANEL_X + 0.0175, YB + 0.02]],
       "room": {"w": W, "d": D, "h": H, "soffit": SOFFIT_Z}}
with open(os.path.join(ROOT, "light_rig.json"), "w") as f: json.dump(rig, f, indent=1)

bpy.ops.wm.save_as_mainfile(filepath=os.path.join(ROOT, "dojang.blend"))
bpy.ops.export_scene.fbx(filepath=os.path.join(MODELS, "Dojang.fbx"), use_selection=False,
                         object_types={"EMPTY", "MESH"}, apply_unit_scale=True, apply_scale_options="FBX_SCALE_UNITS",
                         bake_space_transform=True, axis_forward="-Z", axis_up="Y", mesh_smooth_type="FACE",
                         path_mode="STRIP", embed_textures=False, add_leaf_bones=False)
print("RESULT", json.dumps({"objects": len(root.children), "downlights": len(DOWNLIGHTS), "logo": info}))
