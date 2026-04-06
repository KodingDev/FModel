"""
BlenderLink - FModel Blender Integration Addon v3.1
Receives meshes, animations, and materials from FModel via HTTP.
Builds game-agnostic PBR materials using heuristic texture categorization,
CachedExpressionData (ConnectedOutputs, MaterialFunctions), and
special handling for Unlit/VFX shaders.
Uses io_scene_ueformat for .uemodel/.ueanim import.
"""

bl_info = {
    "name": "BlenderLink",
    "author": "",
    "version": (3, 1, 0),
    "blender": (4, 2, 0),
    "location": "3D Viewport > Sidebar > BlenderLink",
    "description": "Receive meshes and animations from FModel over the network",
    "category": "Import-Export",
}

ADDON_VERSION = "3.1.0"

import bpy
import json
import math
import os
import re
import threading
import traceback
from collections import defaultdict
from http.server import HTTPServer, BaseHTTPRequestHandler

# ---------------------------------------------------------------------------
# io_scene_ueformat bridge
# ---------------------------------------------------------------------------

_ueformat_available = False

def _init_ueformat():
    global _ueformat_available
    try:
        from bl_ext.user_default.io_scene_ueformat.importer.logic import UEFormatImport
        from bl_ext.user_default.io_scene_ueformat.options import UEModelOptions, UEAnimOptions
        _ueformat_available = True
        return UEFormatImport, UEModelOptions, UEAnimOptions
    except ImportError:
        pass
    try:
        from io_scene_ueformat.importer.logic import UEFormatImport
        from io_scene_ueformat.options import UEModelOptions, UEAnimOptions
        _ueformat_available = True
        return UEFormatImport, UEModelOptions, UEAnimOptions
    except ImportError:
        pass
    print("[BlenderLink] WARNING: io_scene_ueformat not found.")
    return None, None, None

UEFormatImport, UEModelOptions, UEAnimOptions = None, None, None


# ===================================================================
# UE ENUMS
# ===================================================================

class ShadingModel:
    Unlit = 0
    DefaultLit = 1
    Subsurface = 2
    PreintegratedSkin = 3
    ClearCoat = 4
    SubsurfaceProfile = 5
    TwoSidedFoliage = 6
    Hair = 7
    Cloth = 8
    Eye = 9

class BlendMode:
    Opaque = 0
    Masked = 1
    Translucent = 2
    Additive = 3
    Modulate = 4


# ===================================================================
# TEXTURE LOADING
# ===================================================================

def import_image(assets_root, path):
    if not path:
        return None
    if "." in path:
        game_path = path.split(".")[0]
        name = path.split(".")[-1]
    else:
        game_path = path
        name = os.path.basename(path)

    game_path = game_path[1:] if game_path.startswith("/") else game_path
    texture_path = os.path.join(assets_root, game_path + ".png")

    if existing := bpy.data.images.get(name):
        return existing

    if not os.path.exists(texture_path):
        tga_path = os.path.join(assets_root, game_path + ".tga")
        if os.path.exists(tga_path):
            texture_path = tga_path
        else:
            return None

    return bpy.data.images.load(texture_path, check_existing=True)


# ===================================================================
# HELPER UTILITIES
# ===================================================================

def _is_white(c):
    return abs(c.get("R", 1) - 1) < 0.01 and abs(c.get("G", 1) - 1) < 0.01 and abs(c.get("B", 1) - 1) < 0.01

def _is_black(c):
    return abs(c.get("R", 0)) < 0.01 and abs(c.get("G", 0)) < 0.01 and abs(c.get("B", 0)) < 0.01

def _vec_to_tuple(v, alpha=True):
    if alpha:
        return (v.get("R", 0), v.get("G", 0), v.get("B", 0), v.get("A", 1))
    return (v.get("R", 0), v.get("G", 0), v.get("B", 0))



# ###################################################################
#
#  UV PANNER NODE GROUP
#
# ###################################################################

def _get_uv_panner_group():
    """UV panning with time driver. Inputs: UV, SpeedX, SpeedY. Output: UV."""
    name = ".UE_UV_Panner"
    if name in bpy.data.node_groups:
        return bpy.data.node_groups[name]

    group = bpy.data.node_groups.new(name, 'ShaderNodeTree')
    gi = group.interface
    gi.new_socket("UV", in_out='INPUT', socket_type='NodeSocketVector')
    gi.new_socket("Speed X", in_out='INPUT', socket_type='NodeSocketFloat')
    gi.new_socket("Speed Y", in_out='INPUT', socket_type='NodeSocketFloat')
    gi.new_socket("UV", in_out='OUTPUT', socket_type='NodeSocketVector')

    nodes = group.nodes
    links = group.links

    inp = nodes.new('NodeGroupInput')
    inp.location = (-600, 0)
    out = nodes.new('NodeGroupOutput')
    out.location = (200, 0)

    time_node = nodes.new('ShaderNodeValue')
    time_node.location = (-600, -200)
    time_node.label = "Time"
    time_node.outputs[0].default_value = 0.0
    try:
        drv = time_node.outputs[0].driver_add("default_value")
        drv.driver.expression = "frame / 30.0"
    except Exception:
        pass

    combine = nodes.new('ShaderNodeCombineXYZ')
    combine.location = (-400, -100)
    links.new(inp.outputs['Speed X'], combine.inputs['X'])
    links.new(inp.outputs['Speed Y'], combine.inputs['Y'])

    scale = nodes.new('ShaderNodeVectorMath')
    scale.operation = 'SCALE'
    scale.location = (-200, -100)
    links.new(combine.outputs['Vector'], scale.inputs[0])
    links.new(time_node.outputs[0], scale.inputs['Scale'])

    add = nodes.new('ShaderNodeVectorMath')
    add.operation = 'ADD'
    add.location = (0, 0)
    links.new(inp.outputs['UV'], add.inputs[0])
    links.new(scale.outputs['Vector'], add.inputs[1])

    links.new(add.outputs['Vector'], out.inputs['UV'])
    return group


# ###################################################################
#
#  MATERIAL BUILDER — heuristic PBR with CachedExpressionData awareness
#
# ###################################################################

# --- Texture categorization ---
_DIFFUSE_NAMES = frozenset({
    'diffuse', 'd', 'base color', 'basecolor', 'basecolormap', 'albedo',
    'diffusemap', 'pm_diffuse', 'litdiffuse', 'shadeddiffuse',
    'base texture', 'base color texture', 'base_tex', 'color map',
    'colour map', 'texture',
})
_NORMAL_NAMES = frozenset({
    'normals', 'n', 'normal', 'normalmap', 'pm_normals', 'normal map',
    'normal texture', 'bumpmap',
})
_MASK_NAMES = frozenset({
    'specularmasks', 's', 'srm', 'orm', 'mro', 'mromap', 'basemask',
    'pm_specularmasks', 'arm', 'rma', 'mra', 'packedtexture',
    'metallicroughnessocclusionspeculartexture', 'metallic texture',
    'roughness texture',
})
_EMISSIVE_NAMES = frozenset({
    'emissive', 'emissivemask', 'emissivetexture', 'pm_emissive',
    'emission texture', 'glow texture', 'emissive texture',
})
_OPACITY_NAMES = frozenset({
    'm', 'mask', 'masktexture', 'opacitymask', 'opacity texture',
    'alpha texture', 'transparency mask',
})
_SUBSURFACE_NAMES = frozenset({
    'subsurface texture', 'subsurface color texture', 'sss texture',
})

_SUFFIX_MAP = {
    '_b': 'diffuse', '_d': 'diffuse', '_bc': 'diffuse', '_diffuse': 'diffuse',
    '_n': 'normal', '_normal': 'normal', '_nrm': 'normal',
    '_m': 'mask', '_orm': 'mask', '_srm': 'mask', '_mra': 'mask',
    '_packed': 'mask', '_mrs': 'mask', '_mros': 'mask', '_mro': 'mask',
    '_arm': 'mask', '_rma': 'mask',
    '_em': 'emissive', '_e': 'emissive', '_emissive': 'emissive',
    '_sss': 'subsurface',
}

def _categorize_texture(name):
    n = name.casefold().strip()
    if n in _DIFFUSE_NAMES:   return 'diffuse'
    if n in _NORMAL_NAMES:    return 'normal'
    if n in _MASK_NAMES:      return 'mask'
    if n in _EMISSIVE_NAMES:  return 'emissive'
    if n in _OPACITY_NAMES:   return 'opacity'
    if n in _SUBSURFACE_NAMES: return 'subsurface'
    for suffix, cat in _SUFFIX_MAP.items():
        if n.endswith(suffix):
            return cat
    return None


# --- Channel packing ---
_ACRONYM_PACKINGS = {
    'mros': {'metallic': 0, 'roughness': 1, 'ao': 2, 'specular': 3},
    'mrso': {'metallic': 0, 'roughness': 1, 'specular': 2, 'ao': 3},
    'rmao': {'roughness': 0, 'metallic': 1, 'ao': 2, 'specular': -1},
    'orm':  {'ao': 0, 'roughness': 1, 'metallic': 2, 'specular': -1},
    'mro':  {'metallic': 0, 'roughness': 1, 'ao': 2, 'specular': -1},
    'srm':  {'specular': 0, 'roughness': 1, 'metallic': 2, 'ao': -1},
    'arm':  {'ao': 0, 'roughness': 1, 'metallic': 2, 'specular': -1},
    'rma':  {'roughness': 0, 'metallic': 1, 'ao': 2, 'specular': -1},
    'mra':  {'metallic': 0, 'roughness': 1, 'ao': 2, 'specular': -1},
    'mrs':  {'metallic': 0, 'roughness': 1, 'specular': 2, 'ao': -1},
    'mr':   {'metallic': 0, 'roughness': 1, 'ao': -1, 'specular': -1},
    'rm':   {'roughness': 0, 'metallic': 1, 'ao': -1, 'specular': -1},
}
_DEFAULT_PACKING = {'ao': 0, 'roughness': 1, 'metallic': 2, 'specular': -1}

def _detect_channel_packing(original_name, texture_path):
    sources = []
    if original_name:
        sources.append(original_name.casefold())
    if texture_path:
        fname = os.path.basename(texture_path.split(".")[0]).casefold()
        sources.append(fname)

    for name in sources:
        if 'metallicroughness' in name:
            if 'specular' in name and ('occlusion' in name or 'ao' in name):
                return _ACRONYM_PACKINGS['mros']
            if 'occlusion' in name or 'ao' in name:
                return _ACRONYM_PACKINGS['mro']
            if 'specular' in name:
                return _ACRONYM_PACKINGS['mrs']
            return _ACRONYM_PACKINGS['mr']
        if 'roughnessmetallic' in name:
            return _ACRONYM_PACKINGS['rm']
        if 'occlusionroughnessmetallic' in name:
            return _ACRONYM_PACKINGS['orm']
        for acronym, packing in sorted(_ACRONYM_PACKINGS.items(), key=lambda x: -len(x[0])):
            pattern = rf'(?:^|[_\s])({re.escape(acronym)})(?:$|[_\s]|map|texture)'
            if re.search(pattern, name):
                return packing
            if name.endswith(f'_{acronym}') or name.endswith(acronym):
                return packing

    norm = (original_name or "").casefold()
    if norm in ('specularmasks', 's', 'pm_specularmasks'):
        return {'specular': 0, 'roughness': 1, 'metallic': 2, 'ao': -1}
    if norm in ('basemask',):
        return {'ao': 0, 'roughness': 1, 'metallic': 2, 'specular': -1}

    return _DEFAULT_PACKING


# --- Node group factories (heuristic path) ---

def _get_dissolve_group():
    name = ".UE_Dissolve"
    if name in bpy.data.node_groups:
        return bpy.data.node_groups[name]

    group = bpy.data.node_groups.new(name, 'ShaderNodeTree')
    gi = group.interface
    gi.new_socket("Color", in_out='INPUT', socket_type='NodeSocketColor')
    gi.new_socket("Emission", in_out='INPUT', socket_type='NodeSocketColor')
    gi.new_socket("Rate", in_out='INPUT', socket_type='NodeSocketFloat')
    gi.new_socket("Scale", in_out='INPUT', socket_type='NodeSocketFloat')
    gi.new_socket("Emissive Power", in_out='INPUT', socket_type='NodeSocketFloat')
    gi.new_socket("Edge Color", in_out='INPUT', socket_type='NodeSocketColor')
    gi.new_socket("Color", in_out='OUTPUT', socket_type='NodeSocketColor')
    gi.new_socket("Emission", in_out='OUTPUT', socket_type='NodeSocketColor')
    gi.new_socket("Alpha", in_out='OUTPUT', socket_type='NodeSocketFloat')

    nodes = group.nodes
    links = group.links

    inp = nodes.new('NodeGroupInput'); inp.location = (-800, 0)
    out = nodes.new('NodeGroupOutput'); out.location = (600, 0)

    tex_coord = nodes.new('ShaderNodeTexCoord'); tex_coord.location = (-800, -300)
    noise = nodes.new('ShaderNodeTexNoise'); noise.location = (-600, -300)
    noise.inputs['Detail'].default_value = 4.0
    links.new(tex_coord.outputs['Object'], noise.inputs['Vector'])
    links.new(inp.outputs['Scale'], noise.inputs['Scale'])

    compare = nodes.new('ShaderNodeMath'); compare.operation = 'GREATER_THAN'; compare.location = (-200, -100)
    links.new(noise.outputs['Fac'], compare.inputs[0])
    links.new(inp.outputs['Rate'], compare.inputs[1])

    edge_inner = nodes.new('ShaderNodeMath'); edge_inner.operation = 'SUBTRACT'; edge_inner.location = (-400, -200)
    links.new(inp.outputs['Rate'], edge_inner.inputs[0]); edge_inner.inputs[1].default_value = 0.05

    edge_compare = nodes.new('ShaderNodeMath'); edge_compare.operation = 'LESS_THAN'; edge_compare.location = (-200, -250)
    links.new(noise.outputs['Fac'], edge_compare.inputs[0])
    links.new(inp.outputs['Rate'], edge_compare.inputs[1])

    edge_compare2 = nodes.new('ShaderNodeMath'); edge_compare2.operation = 'GREATER_THAN'; edge_compare2.location = (-200, -400)
    links.new(noise.outputs['Fac'], edge_compare2.inputs[0])
    links.new(edge_inner.outputs[0], edge_compare2.inputs[1])

    edge_mask = nodes.new('ShaderNodeMath'); edge_mask.operation = 'MULTIPLY'; edge_mask.location = (0, -300)
    links.new(edge_compare.outputs[0], edge_mask.inputs[0])
    links.new(edge_compare2.outputs[0], edge_mask.inputs[1])

    edge_power = nodes.new('ShaderNodeMath'); edge_power.operation = 'MULTIPLY'; edge_power.location = (200, -300)
    links.new(edge_mask.outputs[0], edge_power.inputs[0])
    links.new(inp.outputs['Emissive Power'], edge_power.inputs[1])

    edge_emit = nodes.new('ShaderNodeMix'); edge_emit.data_type = 'RGBA'; edge_emit.blend_type = 'MULTIPLY'
    edge_emit.location = (400, -200); edge_emit.inputs[0].default_value = 1.0
    links.new(inp.outputs['Edge Color'], edge_emit.inputs[6])
    edge_emit.inputs[7].default_value = (1, 1, 1, 1)
    links.new(edge_power.outputs[0], edge_emit.inputs[0])

    links.new(inp.outputs['Color'], out.inputs['Color'])

    add_emit = nodes.new('ShaderNodeMix'); add_emit.data_type = 'RGBA'; add_emit.blend_type = 'ADD'
    add_emit.location = (400, 0); add_emit.inputs[0].default_value = 1.0
    links.new(inp.outputs['Emission'], add_emit.inputs[6])
    links.new(edge_emit.outputs[2], add_emit.inputs[7])
    links.new(add_emit.outputs[2], out.inputs['Emission'])

    links.new(compare.outputs[0], out.inputs['Alpha'])
    return group


def _get_color_change_group():
    name = ".UE_ColorChange"
    if name in bpy.data.node_groups:
        return bpy.data.node_groups[name]

    group = bpy.data.node_groups.new(name, 'ShaderNodeTree')
    gi = group.interface
    gi.new_socket("Base Color", in_out='INPUT', socket_type='NodeSocketColor')
    gi.new_socket("Mask", in_out='INPUT', socket_type='NodeSocketFloat')
    gi.new_socket("Change Color", in_out='INPUT', socket_type='NodeSocketColor')
    gi.new_socket("Rate", in_out='INPUT', socket_type='NodeSocketFloat')
    gi.new_socket("Color", in_out='OUTPUT', socket_type='NodeSocketColor')

    nodes = group.nodes
    links = group.links

    inp = nodes.new('NodeGroupInput'); inp.location = (-400, 0)
    out = nodes.new('NodeGroupOutput'); out.location = (200, 0)

    factor = nodes.new('ShaderNodeMath'); factor.operation = 'MULTIPLY'; factor.location = (-200, -100)
    links.new(inp.outputs['Mask'], factor.inputs[0])
    links.new(inp.outputs['Rate'], factor.inputs[1])

    mix = nodes.new('ShaderNodeMix'); mix.data_type = 'RGBA'; mix.blend_type = 'MIX'; mix.location = (0, 0)
    links.new(factor.outputs[0], mix.inputs[0])
    links.new(inp.outputs['Base Color'], mix.inputs[6])
    links.new(inp.outputs['Change Color'], mix.inputs[7])

    links.new(mix.outputs[2], out.inputs['Color'])
    return group


# --- Material setup ---

def _build_material(material_slot, material_data, assets_root):
    """Build a Blender shader node graph using heuristic texture categorization
    enhanced with CachedExpressionData (ConnectedOutputs, MaterialFunctions)."""
    material_name = material_data.get("Name", "Material")

    material_slot.material = bpy.data.materials.new(material_name)
    mat = material_slot.material
    mat.use_nodes = True
    mat.surface_render_method = "DITHERED"

    nodes = mat.node_tree.nodes
    nodes.clear()
    links = mat.node_tree.links
    links.clear()

    # CachedExpressionData from the cooked base material
    connected_outputs = material_data.get("ConnectedOutputs", [])
    material_functions = material_data.get("MaterialFunctions", [])

    scalars = {}
    for s in material_data.get("Scalars", []):
        scalars[s["Name"].casefold()] = s["Value"]
    vectors = {}
    for v in material_data.get("Vectors", []):
        vectors[v["Name"].casefold()] = v["Value"]
    switches = {}
    for s in material_data.get("Switches", []):
        switches[s["Name"].casefold()] = s["Value"]
    comp_masks = {}
    for c in material_data.get("ComponentMasks", []):
        comp_masks[c["Name"].casefold()] = c["Value"]

    def S(name, default=0.0): return scalars.get(name.casefold(), default)
    def V(name, default=None): return vectors.get(name.casefold(), default)
    def SW(name, default=False): return switches.get(name.casefold(), default)

    shading_model = material_data.get("ShadingModel", ShadingModel.DefaultLit)
    override_blend = material_data.get("OverrideBlendMode", 0)
    base_blend = material_data.get("BaseBlendMode", 0)
    blend_mode = override_blend if override_blend != 0 else base_blend
    opacity_clip = material_data.get("OpacityMaskClipValue", 0.333)
    two_sided = material_data.get("TwoSided", False)

    output = nodes.new("ShaderNodeOutputMaterial"); output.location = (600, 0)
    bsdf = nodes.new("ShaderNodeBsdfPrincipled"); bsdf.location = (200, 0)
    links.new(bsdf.outputs['BSDF'], output.inputs['Surface'])

    tex_by_cat = {}
    extra_textures = []

    for tex_info in material_data.get("Textures", []):
        tex_name = tex_info.get("Name", "")
        tex_path = tex_info.get("Value", "")
        if not tex_path: continue
        image = import_image(assets_root, tex_path)
        if image is None: continue
        category = _categorize_texture(tex_name)
        if category and category not in tex_by_cat:
            tex_by_cat[category] = (image, tex_info)
        else:
            extra_textures.append((image, tex_info, tex_info.get("OriginalName", tex_name)))

    # --- Texture fallback system ---
    # When textures don't match categories, infer from metadata
    if not tex_by_cat and extra_textures:
        remaining = list(extra_textures)
        extra_textures.clear()
        for image, tex_info, display_name in remaining:
            is_srgb = tex_info.get("sRGB", True)
            # CompressionSettings: 0=Default, 3=Normalmap, 4=Masks
            compression = tex_info.get("CompressionSettings", 0)

            if compression == 3 and 'normal' not in tex_by_cat:
                tex_by_cat['normal'] = (image, tex_info)
            elif compression == 4 and 'mask' not in tex_by_cat:
                tex_by_cat['mask'] = (image, tex_info)
            elif not is_srgb and 'normal' not in tex_by_cat:
                # Non-sRGB likely normal or mask
                tex_by_cat['normal'] = (image, tex_info)
            elif is_srgb and 'diffuse' not in tex_by_cat:
                # sRGB likely diffuse or emissive
                if (shading_model == ShadingModel.Unlit and
                        "EmissiveColor" in connected_outputs):
                    tex_by_cat['emissive'] = (image, tex_info)
                else:
                    tex_by_cat['diffuse'] = (image, tex_info)
            else:
                extra_textures.append((image, tex_info, display_name))

    def _tex_node(image, tex_info, loc, label=None, force_non_color=False):
        n = nodes.new("ShaderNodeTexImage")
        n.image = image; n.image.alpha_mode = 'CHANNEL_PACKED'
        if force_non_color: n.image.colorspace_settings.name = "Non-Color"
        else: n.image.colorspace_settings.name = "sRGB" if tex_info.get("sRGB", True) else "Non-Color"
        n.interpolation = "Smart"; n.location = loc
        if label: n.label = label
        return n

    def _math(op, loc, v1=None, v2=None, label=None, hide=True):
        n = nodes.new("ShaderNodeMath"); n.operation = op; n.location = loc
        if v1 is not None: n.inputs[0].default_value = v1
        if v2 is not None: n.inputs[1].default_value = v2
        if label: n.label = label; n.hide = hide
        return n

    def _clamp01(socket, loc):
        mn = _math('MAXIMUM', loc, v2=0.0)
        links.new(socket, mn.inputs[0])
        mx = _math('MINIMUM', (loc[0] + 100, loc[1]), v2=1.0)
        links.new(mn.outputs[0], mx.inputs[0])
        return mx.outputs[0]

    # UV chain
    uv_output = None
    panner_speed = V("panner speed")
    uv_scale_vec = V("uv scale")
    uv_offset0 = V("uvoffset0")

    has_panning = panner_speed and not _is_black(panner_speed)
    has_uv_transform = (uv_scale_vec and not (
        abs(uv_scale_vec.get("R", 1) - 1) < 0.01 and abs(uv_scale_vec.get("G", 1) - 1) < 0.01
    )) or (uv_offset0 and not _is_black(uv_offset0))

    if has_panning or has_uv_transform:
        tex_coord = nodes.new("ShaderNodeTexCoord"); tex_coord.location = (-1600, 200)
        uv_output = tex_coord.outputs['UV']
        if has_uv_transform:
            mapping = nodes.new("ShaderNodeMapping"); mapping.location = (-1400, 200); mapping.label = "UV Scale/Offset"
            links.new(uv_output, mapping.inputs['Vector'])
            if uv_scale_vec: mapping.inputs['Scale'].default_value = (uv_scale_vec.get("R", 1), uv_scale_vec.get("G", 1), 1.0)
            if uv_offset0: mapping.inputs['Location'].default_value = (uv_offset0.get("R", 0), uv_offset0.get("G", 0), 0.0)
            uv_output = mapping.outputs['Vector']
        if has_panning:
            panner_group = _get_uv_panner_group()
            panner = nodes.new("ShaderNodeGroup"); panner.node_tree = panner_group
            panner.location = (-1200, 200); panner.label = "UV Panner"
            links.new(uv_output, panner.inputs['UV'])
            panner.inputs['Speed X'].default_value = panner_speed.get("R", 0)
            panner.inputs['Speed Y'].default_value = panner_speed.get("G", 0)
            uv_output = panner.outputs['UV']

    def _connect_uv(tex_node_instance):
        if uv_output is not None: links.new(uv_output, tex_node_instance.inputs['Vector'])

    # Base color chain
    base_color_out = None; base_alpha_out = None

    if 'diffuse' in tex_by_cat:
        img, info = tex_by_cat['diffuse']
        diffuse_tex = _tex_node(img, info, (-1000, 500), "Base Color"); _connect_uv(diffuse_tex)
        base_color_out = diffuse_tex.outputs['Color']; base_alpha_out = diffuse_tex.outputs['Alpha']

        bc_vec = V("basecolor")
        if bc_vec and not _is_white(bc_vec):
            tint = nodes.new("ShaderNodeMix"); tint.data_type = 'RGBA'; tint.blend_type = 'MULTIPLY'
            tint.location = (-700, 500); tint.label = "BaseColor Tint"; tint.inputs[0].default_value = 1.0
            links.new(base_color_out, tint.inputs[6]); tint.inputs[7].default_value = _vec_to_tuple(bc_vec)
            base_color_out = tint.outputs[2]

        bci = S("base color intensity", -1)
        if bci >= 0 and abs(bci - 0.5) > 0.01:
            mix = nodes.new("ShaderNodeMix"); mix.data_type = 'RGBA'; mix.blend_type = 'MIX'
            mix.location = (-500, 500); mix.label = "Base Color Intensity"
            mix.inputs[0].default_value = min(1.0, bci * 2.0); mix.inputs[6].default_value = (1, 1, 1, 1)
            links.new(base_color_out, mix.inputs[7]); base_color_out = mix.outputs[2]

        hue_vec = V("hue"); hue_shift = 0.0
        if hue_vec and not _is_white(hue_vec): hue_shift = hue_vec.get("R", 0)
        sat = S("saturation", 0); val = S("value", 1); desat = S("desaturation", 0)
        eff_sat = max(0.0, 1.0 - desat + sat)
        if abs(eff_sat - 1.0) > 0.01 or abs(val - 1.0) > 0.01 or abs(hue_shift) > 0.01:
            hsv = nodes.new("ShaderNodeHueSaturation"); hsv.location = (-300, 500); hsv.label = "HSV Adjust"
            hsv.inputs['Hue'].default_value = 0.5 + hue_shift; hsv.inputs['Saturation'].default_value = eff_sat
            hsv.inputs['Value'].default_value = val; hsv.inputs['Fac'].default_value = 1.0
            links.new(base_color_out, hsv.inputs['Color']); base_color_out = hsv.outputs['Color']

        contrast = S("contrast", 1.0)
        if abs(contrast - 1.0) > 0.01:
            bc_node = nodes.new("ShaderNodeBrightContrast"); bc_node.location = (-150, 500); bc_node.label = "Contrast"
            bc_node.inputs['Bright'].default_value = 0.0; bc_node.inputs['Contrast'].default_value = (contrast - 1.0) * 2.0
            links.new(base_color_out, bc_node.inputs['Color']); base_color_out = bc_node.outputs['Color']

    # Mask map chain
    has_mask = 'mask' in tex_by_cat
    if has_mask:
        img, info = tex_by_cat['mask']
        mask_tex = _tex_node(img, info, (-1000, 0), "Mask Map", force_non_color=True); _connect_uv(mask_tex)
        sep = nodes.new("ShaderNodeSeparateColor"); sep.location = (-700, 0); sep.label = "Separate Channels"
        links.new(mask_tex.outputs['Color'], sep.inputs['Color'])
        orig_name = info.get("OriginalName", info.get("Name", ""))
        tex_path = info.get("Value", "")
        packing = _detect_channel_packing(orig_name, tex_path)

        metallic_ch = packing.get('metallic', -1)
        if metallic_ch >= 0 and metallic_ch <= 2:
            m_out = sep.outputs[metallic_ch]
            m_mul = S("metallic mul", 1.0); m_add = S("metallic add", 0.0)
            if abs(m_mul - 1.0) > 0.001:
                n = _math('MULTIPLY', (-500, -40), v2=m_mul, label="Metallic Mul"); links.new(m_out, n.inputs[0]); m_out = n.outputs[0]
            if abs(m_add) > 0.001:
                n = _math('ADD', (-400, -40), v2=m_add, label="Metallic Add"); links.new(m_out, n.inputs[0]); m_out = n.outputs[0]
            links.new(_clamp01(m_out, (-300, -40)), bsdf.inputs['Metallic'])
        else:
            bsdf.inputs['Metallic'].default_value = S("metallic", 0.0)

        roughness_ch = packing.get('roughness', -1)
        if roughness_ch >= 0 and roughness_ch <= 2:
            r_out = sep.outputs[roughness_ch]
            r_mul = S("roughness mul", 1.0); r_add = S("roughness add", 0.0)
            if abs(r_mul - 1.0) > 0.001:
                n = _math('MULTIPLY', (-500, 20), v2=r_mul, label="Roughness Mul"); links.new(r_out, n.inputs[0]); r_out = n.outputs[0]
            if abs(r_add) > 0.001:
                n = _math('ADD', (-400, 20), v2=r_add, label="Roughness Add"); links.new(r_out, n.inputs[0]); r_out = n.outputs[0]
            links.new(_clamp01(r_out, (-300, 20)), bsdf.inputs['Roughness'])
        else:
            bsdf.inputs['Roughness'].default_value = min(1.0, max(0.0, S("roughness", 0.5) * S("roughness mul", 1.0) + S("roughness add", 0.0)))

        ao_ch = packing.get('ao', -1)
        if ao_ch >= 0 and ao_ch <= 2 and base_color_out is not None:
            ao_out = sep.outputs[ao_ch]
            ao_mul = S("occlusion mul", 1.0); ao_add = S("occlusion add", 0.0)
            if abs(ao_mul - 1.0) > 0.001:
                n = _math('MULTIPLY', (-500, 80), v2=ao_mul, label="Occlusion Mul"); links.new(ao_out, n.inputs[0]); ao_out = n.outputs[0]
            if abs(ao_add) > 0.001:
                n = _math('ADD', (-400, 80), v2=ao_add, label="Occlusion Add"); links.new(ao_out, n.inputs[0]); ao_out = n.outputs[0]
            ao_mix = nodes.new("ShaderNodeMix"); ao_mix.data_type = 'RGBA'; ao_mix.blend_type = 'MULTIPLY'
            ao_mix.location = (-50, 300); ao_mix.label = "AO"; ao_mix.inputs[0].default_value = 1.0
            links.new(base_color_out, ao_mix.inputs[6])
            ao_color = nodes.new("ShaderNodeCombineColor"); ao_color.location = (-200, 200); ao_color.label = "AO Gray"; ao_color.hide = True
            links.new(ao_out, ao_color.inputs[0]); links.new(ao_out, ao_color.inputs[1]); links.new(ao_out, ao_color.inputs[2])
            links.new(ao_color.outputs[0], ao_mix.inputs[7]); base_color_out = ao_mix.outputs[2]

        spec_ch = packing.get('specular', -1)
        specular_input = 'Specular IOR Level' if 'Specular IOR Level' in bsdf.inputs else 'Specular'
        if spec_ch >= 0 and spec_ch <= 2: links.new(sep.outputs[spec_ch], bsdf.inputs[specular_input])
        else:
            spec_val = S("specular", -1)
            if spec_val >= 0: bsdf.inputs[specular_input].default_value = spec_val
    else:
        bsdf.inputs['Metallic'].default_value = S("metallic", 0.0)
        bsdf.inputs['Roughness'].default_value = min(1.0, max(0.0, S("roughness", 0.5) * S("roughness mul", 1.0) + S("roughness add", 0.0)))
        spec_val = S("specular", -1)
        if spec_val >= 0:
            specular_input = 'Specular IOR Level' if 'Specular IOR Level' in bsdf.inputs else 'Specular'
            bsdf.inputs[specular_input].default_value = spec_val

    # Color change
    change_rate = S("changecolor rate", 0.0); change_color = V("changecolor")
    if change_rate > 0.001 and change_color and base_color_out is not None:
        change_mask_img = None
        for img_item, info_item, dname in extra_textures:
            if 'changecolor' in dname.casefold() and 'mask' in dname.casefold():
                change_mask_img = (img_item, info_item); break
        cc_group = _get_color_change_group()
        cc_node = nodes.new("ShaderNodeGroup"); cc_node.node_tree = cc_group; cc_node.location = (0, 500); cc_node.label = "Color Change"
        links.new(base_color_out, cc_node.inputs['Base Color'])
        cc_node.inputs['Change Color'].default_value = _vec_to_tuple(change_color)
        cc_node.inputs['Rate'].default_value = change_rate
        if change_mask_img:
            mask_n = _tex_node(change_mask_img[0], change_mask_img[1], (-200, 600), "ChangeColor Mask"); _connect_uv(mask_n)
            links.new(mask_n.outputs['Color'], cc_node.inputs['Mask'])
        else: cc_node.inputs['Mask'].default_value = 1.0
        base_color_out = cc_node.outputs['Color']

    # Connect base color
    if base_color_out is not None: links.new(base_color_out, bsdf.inputs['Base Color'])

    # Normal map
    if 'normal' in tex_by_cat:
        img, info = tex_by_cat['normal']
        normal_tex = _tex_node(img, info, (-1000, -350), "Normal Map", force_non_color=True); _connect_uv(normal_tex)
        nmap = nodes.new("ShaderNodeNormalMap"); nmap.location = (-500, -350); nmap.label = "Normal Map"
        nmap.inputs['Strength'].default_value = S("normal map intensity", 1.0)
        links.new(normal_tex.outputs['Color'], nmap.inputs['Color']); links.new(nmap.outputs['Normal'], bsdf.inputs['Normal'])

    # Emissive
    emissive_intensity = S("emissive texture intensity", 0.0)
    base_emissive_intensity = S("base emissive intensity", 0.0)
    emissive_connected = False

    if 'emissive' in tex_by_cat:
        img, info = tex_by_cat['emissive']
        emissive_tex = _tex_node(img, info, (-1000, -600), "Emissive"); _connect_uv(emissive_tex)
        strength = emissive_intensity if emissive_intensity > 0 else 1.0
        links.new(emissive_tex.outputs['Color'], bsdf.inputs['Emission Color'])
        bsdf.inputs['Emission Strength'].default_value = strength; emissive_connected = True
    elif base_emissive_intensity > 0.001 and base_color_out is not None:
        bsdf.inputs['Emission Strength'].default_value = base_emissive_intensity

    capture_emission = S("captureemission", 0.0)
    if capture_emission > 0:
        bsdf.inputs['Emission Strength'].default_value = max(bsdf.inputs['Emission Strength'].default_value, capture_emission)

    # Fresnel
    fresnel_color = V("fresnel emissive color"); fresnel_exp = S("fresnel exponent", 5.0)
    if fresnel_color and not _is_black(fresnel_color):
        fres = nodes.new("ShaderNodeFresnel"); fres.location = (-500, -750); fres.label = "Fresnel Rim"
        fres.inputs['IOR'].default_value = 1.0 + (1.0 / max(fresnel_exp, 0.1))
        fres_mul = nodes.new("ShaderNodeMix"); fres_mul.data_type = 'RGBA'; fres_mul.blend_type = 'MULTIPLY'
        fres_mul.location = (-300, -750); fres_mul.label = "Fresnel Color"; fres_mul.inputs[0].default_value = 1.0
        fres_gray = nodes.new("ShaderNodeCombineColor"); fres_gray.location = (-400, -800); fres_gray.hide = True
        links.new(fres.outputs['Fac'], fres_gray.inputs[0]); links.new(fres.outputs['Fac'], fres_gray.inputs[1]); links.new(fres.outputs['Fac'], fres_gray.inputs[2])
        links.new(fres_gray.outputs[0], fres_mul.inputs[6]); fres_mul.inputs[7].default_value = _vec_to_tuple(fresnel_color)
        fresnel_fraction = S("fresnel fraction", 0.04)
        if emissive_connected:
            add_node = nodes.new("ShaderNodeMix"); add_node.data_type = 'RGBA'; add_node.blend_type = 'ADD'
            add_node.location = (-100, -650); add_node.label = "Add Fresnel"; add_node.inputs[0].default_value = fresnel_fraction
            for link in list(links):
                if link.to_socket == bsdf.inputs['Emission Color']:
                    links.new(link.from_socket, add_node.inputs[6]); links.remove(link); break
            links.new(fres_mul.outputs[2], add_node.inputs[7]); links.new(add_node.outputs[2], bsdf.inputs['Emission Color'])
        else:
            links.new(fres_mul.outputs[2], bsdf.inputs['Emission Color'])
            bsdf.inputs['Emission Strength'].default_value = max(bsdf.inputs['Emission Strength'].default_value, fresnel_fraction)

    # Subsurface
    is_sss = shading_model in (ShadingModel.Subsurface, ShadingModel.PreintegratedSkin, ShadingModel.SubsurfaceProfile, ShadingModel.TwoSidedFoliage)
    if is_sss:
        sss_color = V("subsurface color")
        if sss_color and not _is_black(sss_color):
            bsdf.inputs['Subsurface Weight'].default_value = 0.5
            bsdf.inputs['Subsurface Radius'].default_value = _vec_to_tuple(sss_color, alpha=False)
        if 'subsurface' in tex_by_cat:
            img, info = tex_by_cat['subsurface']
            sss_tex = _tex_node(img, info, (-1000, -900), "Subsurface"); _connect_uv(sss_tex)
            links.new(sss_tex.outputs['Color'], bsdf.inputs['Subsurface Radius']); bsdf.inputs['Subsurface Weight'].default_value = 0.5

    if shading_model == ShadingModel.ClearCoat:
        bsdf.inputs['Coat Weight'].default_value = 1.0; bsdf.inputs['Coat Roughness'].default_value = S("roughness", 0.5) * 0.5
    if shading_model == ShadingModel.Hair:
        aniso = S("anisotropy", 0.0)
        if abs(aniso) > 0.01: bsdf.inputs['Anisotropic'].default_value = aniso
    if shading_model == ShadingModel.Cloth:
        bsdf.inputs['Sheen Weight'].default_value = 1.0

    # Dissolve
    dissolve_rate = S("dissolve rate", 0.0)
    if dissolve_rate > 0.001:
        dissolve_grp = _get_dissolve_group()
        diss = nodes.new("ShaderNodeGroup"); diss.node_tree = dissolve_grp; diss.location = (200, -400); diss.label = "Dissolve"
        if base_color_out is not None: links.new(base_color_out, diss.inputs['Color'])
        diss.inputs['Emission'].default_value = (0, 0, 0, 1)
        for link in list(links):
            if link.to_socket == bsdf.inputs['Emission Color']: links.new(link.from_socket, diss.inputs['Emission']); break
        diss.inputs['Rate'].default_value = dissolve_rate
        diss.inputs['Scale'].default_value = S("dissolve scale", 3.0)
        diss.inputs['Emissive Power'].default_value = S("dissolve emissive power", 6.0)
        dissolve_color = V("dissolve emissive color")
        if dissolve_color: diss.inputs['Edge Color'].default_value = _vec_to_tuple(dissolve_color)
        else: diss.inputs['Edge Color'].default_value = (1, 1, 0, 1)
        for link in list(links):
            if link.to_socket == bsdf.inputs['Emission Color']: links.remove(link); break
        links.new(diss.outputs['Emission'], bsdf.inputs['Emission Color'])
        if base_alpha_out is not None:
            alpha_mul = _math('MULTIPLY', (200, -500), label="Dissolve Alpha")
            links.new(base_alpha_out, alpha_mul.inputs[0]); links.new(diss.outputs['Alpha'], alpha_mul.inputs[1])
            base_alpha_out = alpha_mul.outputs[0]
        else: base_alpha_out = diss.outputs['Alpha']

    # --- Unlit / VFX material handling ---
    if shading_model == ShadingModel.Unlit:
        # Detect fire-type shaders by material functions or color parameter patterns
        has_fire_funcs = any("fire" in f.casefold() for f in material_functions)
        has_color_gradient = V("color1") is not None and V("color2") is not None
        is_fire_shader = has_fire_funcs or has_color_gradient

        if is_fire_shader:
            # Fire/VFX shader: noise texture → color gradient → emission
            c1 = V("color1"); c2 = V("color2"); c3 = V("color3")

            # --- Noise texture with UV panning ---
            noise_tex_node = None
            for img, info, dname in extra_textures:
                noise_tex_node = _tex_node(img, info, (-1400, -1200), dname, force_non_color=True)
                _connect_uv(noise_tex_node)
                break  # Use first available texture

            if noise_tex_node is None and 'diffuse' not in tex_by_cat:
                # No textures at all — create a noise procedural
                noise_tex_node = nodes.new("ShaderNodeTexNoise")
                noise_tex_node.location = (-1400, -1200)
                noise_tex_node.label = "Procedural Noise"
                noise_tex_node.inputs['Scale'].default_value = 5.0

            # UV panning for the noise texture
            main_u_speed = S("mainuspeed", 0.0); main_v_speed = S("mainvspeed", 0.0)
            main_u_tile = S("mainutilling", 1.0); main_v_tile = S("mainvtilling", 1.0)
            if (abs(main_u_speed) > 0.001 or abs(main_v_speed) > 0.001) and noise_tex_node:
                tc = nodes.new("ShaderNodeTexCoord"); tc.location = (-1800, -1200)
                mapping = nodes.new("ShaderNodeMapping"); mapping.location = (-1600, -1200)
                mapping.label = "Fire UV"
                mapping.inputs['Scale'].default_value = (main_u_tile, main_v_tile, 1.0)
                links.new(tc.outputs['UV'], mapping.inputs['Vector'])

                panner_group = _get_uv_panner_group()
                panner = nodes.new("ShaderNodeGroup"); panner.node_tree = panner_group
                panner.location = (-1400, -1100); panner.label = "Fire Panner"
                links.new(mapping.outputs['Vector'], panner.inputs['UV'])
                panner.inputs['Speed X'].default_value = main_u_speed
                panner.inputs['Speed Y'].default_value = main_v_speed
                if noise_tex_node.type == 'TEX_IMAGE':
                    links.new(panner.outputs['UV'], noise_tex_node.inputs['Vector'])

            # --- Color gradient from Color1/Color2/Color3 (HDR values) ---
            if c1 and c2:
                # Normalize HDR colors for the color ramp, then multiply strength back
                def _hdr_mag(c):
                    return max(c.get("R", 0), c.get("G", 0), c.get("B", 0), 0.001)
                def _norm_color(c, mag):
                    return (c.get("R", 0)/mag, c.get("G", 0)/mag, c.get("B", 0)/mag, c.get("A", 1))

                max_intensity = max(_hdr_mag(c1), _hdr_mag(c2), _hdr_mag(c3) if c3 else 0.001)

                ramp = nodes.new("ShaderNodeValToRGB"); ramp.location = (-800, -1200); ramp.label = "Fire Gradient"
                cr = ramp.color_ramp
                # Position 0 = tip (Color1 = brightest), Position 1 = root (Color3 = darkest)
                cr.elements[0].color = _norm_color(c1, max_intensity)
                cr.elements[0].position = 0.0
                cr.elements[1].position = 1.0
                if c3:
                    cr.elements[1].color = _norm_color(c3, max_intensity)
                    e = cr.elements.new(0.5)
                    e.color = _norm_color(c2, max_intensity)
                else:
                    cr.elements[1].color = _norm_color(c2, max_intensity)

                # Drive gradient with noise texture
                if noise_tex_node:
                    noise_out = noise_tex_node.outputs.get('Fac', noise_tex_node.outputs[0])
                    if noise_tex_node.type == 'TEX_IMAGE':
                        noise_out = noise_tex_node.outputs['Color']
                        # Convert color to value via RGB to BW
                        rgb2bw = nodes.new("ShaderNodeRGBToBW"); rgb2bw.location = (-1000, -1200); rgb2bw.hide = True
                        links.new(noise_out, rgb2bw.inputs['Color'])
                        noise_out = rgb2bw.outputs['Val']
                    links.new(noise_out, ramp.inputs['Fac'])

                # Multiply gradient by HDR intensity
                hdr_mul = nodes.new("ShaderNodeMix"); hdr_mul.data_type = 'RGBA'; hdr_mul.blend_type = 'MULTIPLY'
                hdr_mul.location = (-500, -1200); hdr_mul.label = "HDR Intensity"
                hdr_mul.inputs[0].default_value = 1.0
                links.new(ramp.outputs['Color'], hdr_mul.inputs[6])
                hdr_mul.inputs[7].default_value = (max_intensity, max_intensity, max_intensity, 1.0)

                # Connect to emission
                links.new(hdr_mul.outputs[2], bsdf.inputs['Emission Color'])
                bsdf.inputs['Emission Strength'].default_value = 1.0
                emissive_connected = True

                # Also use the noise as alpha source for translucency
                if noise_tex_node:
                    alpha_out = noise_tex_node.outputs.get('Alpha', noise_tex_node.outputs[0])
                    if noise_tex_node.type == 'TEX_IMAGE':
                        alpha_out = noise_tex_node.outputs['Alpha']
                    # Modulate alpha by scalar params
                    alpha_val = S("alpha", S("alphaedge", 0.5))
                    alpha_mul = nodes.new("ShaderNodeMath"); alpha_mul.operation = 'MULTIPLY'
                    alpha_mul.location = (-300, -1400); alpha_mul.label = "Fire Alpha"; alpha_mul.hide = True
                    links.new(alpha_out, alpha_mul.inputs[0])
                    alpha_mul.inputs[1].default_value = min(1.0, alpha_val * 10.0)  # Scale up tiny alpha values
                    links.new(alpha_mul.outputs[0], bsdf.inputs['Alpha'])
                    base_alpha_out = alpha_mul.outputs[0]

            if blend_mode == BlendMode.Opaque:
                blend_mode = BlendMode.Translucent

        elif "EmissiveColor" in connected_outputs and not emissive_connected:
            # Unlit with emissive output — treat uncategorized textures as emissive
            if 'emissive' not in tex_by_cat and extra_textures:
                # Promote first sRGB extra texture to emissive
                for idx, (img, info, dname) in enumerate(extra_textures):
                    if info.get("sRGB", True):
                        emissive_tex = _tex_node(img, info, (-1000, -600), "Emissive"); _connect_uv(emissive_tex)
                        links.new(emissive_tex.outputs['Color'], bsdf.inputs['Emission Color'])
                        bsdf.inputs['Emission Strength'].default_value = S("emissive intensity", 1.0)
                        emissive_connected = True
                        extra_textures.pop(idx)
                        break
            elif base_color_out is not None and not emissive_connected:
                # Connect base color to emission for unlit
                links.new(base_color_out, bsdf.inputs['Emission Color'])
                bsdf.inputs['Emission Strength'].default_value = 1.0
                emissive_connected = True

        # Fresnel rim for unlit VFX
        fresnel_c = V("fresnelcolor")
        if fresnel_c and not _is_black(fresnel_c):
            fres_power = S("rgbfresnelpower", S("fresnel exponent", 5.0))
            fres = nodes.new("ShaderNodeFresnel"); fres.location = (-500, -1400); fres.label = "VFX Fresnel"
            fres.inputs['IOR'].default_value = 1.0 + (1.0 / max(fres_power, 0.1))
            fres_mix = nodes.new("ShaderNodeMix"); fres_mix.data_type = 'RGBA'; fres_mix.blend_type = 'MULTIPLY'
            fres_mix.location = (-300, -1400); fres_mix.label = "Fresnel Rim"; fres_mix.inputs[0].default_value = 1.0
            fres_gray = nodes.new("ShaderNodeCombineColor"); fres_gray.location = (-400, -1450); fres_gray.hide = True
            links.new(fres.outputs['Fac'], fres_gray.inputs[0]); links.new(fres.outputs['Fac'], fres_gray.inputs[1]); links.new(fres.outputs['Fac'], fres_gray.inputs[2])
            links.new(fres_gray.outputs[0], fres_mix.inputs[6]); fres_mix.inputs[7].default_value = _vec_to_tuple(fresnel_c)
            if emissive_connected:
                add_fres = nodes.new("ShaderNodeMix"); add_fres.data_type = 'RGBA'; add_fres.blend_type = 'ADD'
                add_fres.location = (-100, -1300); add_fres.label = "Add Fresnel Rim"; add_fres.inputs[0].default_value = 1.0
                for link in list(links):
                    if link.to_socket == bsdf.inputs['Emission Color']:
                        links.new(link.from_socket, add_fres.inputs[6]); links.remove(link); break
                links.new(fres_mix.outputs[2], add_fres.inputs[7])
                links.new(add_fres.outputs[2], bsdf.inputs['Emission Color'])
            else:
                links.new(fres_mix.outputs[2], bsdf.inputs['Emission Color'])
                bsdf.inputs['Emission Strength'].default_value = 1.0

        # Force translucent for unlit VFX if not already masked
        if blend_mode == BlendMode.Opaque and (
                "Opacity" in connected_outputs or "OpacityMask" in connected_outputs):
            blend_mode = BlendMode.Translucent

    # Alpha / opacity
    if 'opacity' in tex_by_cat:
        img, info = tex_by_cat['opacity']
        opacity_tex = _tex_node(img, info, (-1000, -200), "Opacity Mask"); _connect_uv(opacity_tex)
        base_alpha_out = opacity_tex.outputs['Color']

    if base_alpha_out is not None:
        if blend_mode in (BlendMode.Masked, BlendMode.Translucent, BlendMode.Additive):
            links.new(base_alpha_out, bsdf.inputs['Alpha'])
    elif blend_mode == BlendMode.Masked and 'diffuse' in tex_by_cat:
        for node in nodes:
            if node.type == 'TEX_IMAGE' and node.label == "Base Color":
                links.new(node.outputs['Alpha'], bsdf.inputs['Alpha']); break

    transparency = S("transparency", 0.0)
    if transparency > 0.001 and blend_mode in (BlendMode.Translucent, BlendMode.Additive):
        if base_alpha_out is not None:
            alpha_scale = _math('MULTIPLY', (100, -200), v2=(1.0 - transparency), label="Transparency")
            links.new(base_alpha_out, alpha_scale.inputs[0])
            for link in list(links):
                if link.to_socket == bsdf.inputs['Alpha']: links.remove(link); break
            links.new(alpha_scale.outputs[0], bsdf.inputs['Alpha'])
        else: bsdf.inputs['Alpha'].default_value = 1.0 - transparency

    # Material settings
    if blend_mode == BlendMode.Masked:
        mat.surface_render_method = "DITHERED"; mat.use_backface_culling = not two_sided; mat.alpha_threshold = opacity_clip
        try: mat.blend_method = 'CLIP'
        except Exception: pass
    elif blend_mode in (BlendMode.Translucent, BlendMode.Additive, BlendMode.Modulate):
        mat.surface_render_method = "BLENDED"; mat.use_backface_culling = not two_sided
        try: mat.blend_method = 'BLEND'
        except Exception: pass
    else:
        mat.surface_render_method = "DITHERED"; mat.use_backface_culling = not two_sided
    if shading_model == ShadingModel.TwoSidedFoliage: mat.use_backface_culling = False

    # Extra textures
    extra_y = -1100
    for img, info, display_name in extra_textures:
        tn = _tex_node(img, info, (-1400, extra_y), display_name); tn.hide = True; _connect_uv(tn); extra_y -= 40

    # Metadata
    mat["ue_shading_model"] = shading_model; mat["ue_blend_mode"] = blend_mode
    mat["ue_base_material"] = material_data.get("BaseMaterialPath", "")
    mat["ue_material_path"] = material_data.get("Path", "")
    mat["ue_two_sided"] = two_sided; mat["ue_shader_mode"] = "heuristic"
    if connected_outputs: mat["ue_connected_outputs"] = ", ".join(connected_outputs)
    if material_functions: mat["ue_material_functions"] = ", ".join(material_functions)

    for key, val in scalars.items(): mat[f"ue_s_{key}"] = val
    for key, val in vectors.items(): mat[f"ue_v_{key}"] = f"R:{val.get('R',0):.3f} G:{val.get('G',0):.3f} B:{val.get('B',0):.3f} A:{val.get('A',1):.3f}"
    for key, val in switches.items(): mat[f"ue_sw_{key}"] = val

    packing_info = _detect_channel_packing(
        tex_by_cat.get('mask', (None, {}))[1].get('OriginalName', ''),
        tex_by_cat.get('mask', (None, {}))[1].get('Value', '')
    ) if has_mask else 'none'
    outputs_str = ",".join(connected_outputs[:5]) if connected_outputs else "none"
    print(f"[BlenderLink] Material: {material_name} | shading={shading_model} blend={blend_mode} "
          f"tex={len(tex_by_cat)}+{len(extra_textures)} outputs=[{outputs_str}]")


# ###################################################################
#
#  MATERIAL DISPATCHER
#
# ###################################################################

def setup_material(material_slot, material_data, assets_root):
    """Build a Blender material using all available cooked data."""
    try:
        _build_material(material_slot, material_data, assets_root)
    except Exception as e:
        print(f"[BlenderLink] Material setup failed for '{material_data.get('Name', '?')}': {e}")
        traceback.print_exc()


# ===================================================================
# EXPORT PACKET PROCESSOR
# ===================================================================

def _process_export(packet):
    global UEFormatImport, UEModelOptions, UEAnimOptions

    if UEFormatImport is None:
        UEFormatImport, UEModelOptions, UEAnimOptions = _init_ueformat()
        if UEFormatImport is None:
            print("[BlenderLink] Cannot import — io_scene_ueformat not available")
            return None

    try:
        metadata = packet.get('MetaData', {})
        assets_root = metadata.get('AssetsRoot', '')
        settings = metadata.get('Settings', {})
        exports = packet.get('Exports', [])

        scale = 0.01 if settings.get("ScaleDown", True) else 1.0

        for export in exports:
            export_name = export.get('Name', 'Untitled')
            meshes_data = export.get('Meshes', [])
            animation = export.get('Animation', None)

            if settings.get('ImportIntoCollection', True):
                collection = bpy.data.collections.get(export_name)
                if collection is None:
                    collection = bpy.data.collections.new(export_name)
                    bpy.context.scene.collection.children.link(collection)
                lc = _find_layer_collection(bpy.context.view_layer.layer_collection, collection.name)
                if lc:
                    bpy.context.view_layer.active_layer_collection = lc

            for mesh_data in meshes_data:
                mesh_path = mesh_data.get('Path', '')
                mesh_name = mesh_data.get('Name', export_name)

                clean_path = mesh_path.split(".")[0]
                if clean_path.startswith("/"): clean_path = clean_path[1:]
                uemodel_path = os.path.join(assets_root, clean_path + ".uemodel")

                if not os.path.exists(uemodel_path):
                    print(f"[BlenderLink] .uemodel not found: {uemodel_path}")
                    continue

                model_options = UEModelOptions(
                    scale_factor=scale,
                    bone_length=settings.get("BoneLength", 4.0),
                    reorient_bones=settings.get("ReorientBones", False),
                    import_sockets=settings.get("ImportSockets", True),
                    import_virtual_bones=settings.get("ImportVirtualBones", False),
                    import_collision=settings.get("ImportCollision", False),
                    target_lod=settings.get("TargetLOD", 0),
                )

                imported_object, model_data = UEFormatImport(model_options).import_file(uemodel_path)
                if imported_object is None:
                    print(f"[BlenderLink] Failed to import: {uemodel_path}")
                    continue

                imported_object.name = mesh_name
                mesh_obj = _get_armature_mesh(imported_object)

                if mesh_obj and mesh_obj.data:
                    bpy.context.view_layer.objects.active = mesh_obj
                    bpy.ops.object.mode_set(mode='EDIT')
                    bpy.ops.mesh.select_all(action='SELECT')
                    bpy.ops.mesh.set_normals_from_faces()
                    bpy.ops.object.mode_set(mode='OBJECT')

                materials = mesh_data.get('Materials', [])
                if mesh_obj and materials:
                    for mat_data in materials:
                        slot_idx = mat_data.get("Slot", -1)
                        if 0 <= slot_idx < len(mesh_obj.material_slots):
                            setup_material(mesh_obj.material_slots[slot_idx], mat_data, assets_root)

                if settings.get("ImportAt3DCursor", False):
                    imported_object.location += bpy.context.scene.cursor.location

                # Animation
                if animation and imported_object:
                    armature = imported_object if imported_object.type == 'ARMATURE' else None
                    if armature is None and mesh_obj:
                        for mod in mesh_obj.modifiers:
                            if mod.type == 'ARMATURE' and mod.object:
                                armature = mod.object; break
                        if armature is None and mesh_obj.parent and mesh_obj.parent.type == 'ARMATURE':
                            armature = mesh_obj.parent

                    if armature:
                        for section in animation.get('Sections', []):
                            anim_path = section.get('Path', '')
                            if not anim_path: continue
                            clean_anim = anim_path.split(".")[0]
                            if clean_anim.startswith("/"): clean_anim = clean_anim[1:]
                            ueanim_path = os.path.join(assets_root, clean_anim + ".ueanim")
                            if not os.path.exists(ueanim_path):
                                print(f"[BlenderLink] .ueanim not found: {ueanim_path}"); continue

                            anim_options = UEAnimOptions(scale_factor=scale, override_skeleton=armature)
                            bpy.context.view_layer.objects.active = armature; armature.select_set(True)
                            result = UEFormatImport(anim_options).import_file(ueanim_path)
                            action = result[0] if isinstance(result, tuple) else result

                            if action and settings.get("UpdateTimelineLength", True):
                                bpy.context.scene.frame_start = 0
                                bpy.context.scene.frame_end = max(1, int(action.frame_range[1]))
                                bpy.context.scene.render.fps = 30
                    else:
                        print("[BlenderLink] No armature found for animation")

        print(f"[BlenderLink] Import complete: {len(exports)} export(s) processed")
    except Exception as e:
        print(f"[BlenderLink] Error: {e}")
        traceback.print_exc()
    return None


def _find_layer_collection(layer_col, name):
    if layer_col.name == name: return layer_col
    for child in layer_col.children:
        found = _find_layer_collection(child, name)
        if found: return found
    return None


def _get_armature_mesh(obj):
    if obj.type == 'MESH': return obj
    for child in obj.children:
        if child.type == 'MESH': return child
    return None


# ===================================================================
# HTTP SERVER
# ===================================================================

_server = None
_server_thread = None


class FModelHandler(BaseHTTPRequestHandler):
    def log_message(self, format, *args): pass

    def do_GET(self):
        if self.path.endswith('/ping'):
            self.send_response(200)
            self.send_header('Content-Type', 'application/json')
            self.end_headers()
            self.wfile.write(json.dumps({"status": "ok", "addon": "BlenderLink", "version": ADDON_VERSION}).encode())
        else:
            self.send_response(404); self.end_headers()

    def do_POST(self):
        if self.path.endswith('/data'):
            content_length = int(self.headers.get('Content-Length', 0))
            body = self.rfile.read(content_length)
            try:
                packet = json.loads(body.decode('utf-8'))
                bpy.app.timers.register(lambda: _process_export(packet), first_interval=0.0)
                self.send_response(200)
                self.send_header('Content-Type', 'application/json')
                self.end_headers()
                self.wfile.write(json.dumps({"status": "ok"}).encode())
            except Exception as e:
                self.send_response(500)
                self.send_header('Content-Type', 'application/json')
                self.end_headers()
                self.wfile.write(json.dumps({"status": "error", "message": str(e)}).encode())
                traceback.print_exc()
        elif self.path.endswith('/execute'):
            content_length = int(self.headers.get('Content-Length', 0))
            body = self.rfile.read(content_length).decode('utf-8')
            try:
                payload = json.loads(body)
                code = payload.get("code", body)
            except (json.JSONDecodeError, AttributeError):
                code = body

            # Execute Python on Blender's main thread and wait for result
            result_holder = {"done": False, "result": None, "error": None}
            event = threading.Event()

            def _run_code():
                try:
                    exec_globals = {"bpy": bpy, "os": os, "json": json, "math": math,
                                    "import_image": import_image, "__result__": None}
                    exec(code, exec_globals)
                    result_holder["result"] = exec_globals.get("__result__", "ok")
                except Exception as e:
                    result_holder["error"] = f"{type(e).__name__}: {e}"
                    traceback.print_exc()
                result_holder["done"] = True
                event.set()
                return None

            bpy.app.timers.register(_run_code, first_interval=0.0)
            event.wait(timeout=30)

            if result_holder["error"]:
                self.send_response(500)
                self.send_header('Content-Type', 'application/json')
                self.end_headers()
                self.wfile.write(json.dumps({"status": "error", "message": result_holder["error"]}).encode())
            else:
                self.send_response(200)
                self.send_header('Content-Type', 'application/json')
                self.end_headers()
                res = result_holder["result"]
                if isinstance(res, (dict, list)):
                    self.wfile.write(json.dumps({"status": "ok", "result": res}).encode())
                else:
                    self.wfile.write(json.dumps({"status": "ok", "result": str(res) if res else "ok"}).encode())
        else:
            self.send_response(404); self.end_headers()


def start_server(port=24280):
    global _server, _server_thread
    stop_server()
    _server = HTTPServer(('0.0.0.0', port), FModelHandler)
    _server_thread = threading.Thread(target=_server.serve_forever, daemon=True)
    _server_thread.start()
    print(f"[BlenderLink] Server started on port {port}")


def stop_server():
    global _server, _server_thread
    if _server: _server.shutdown(); _server = None
    if _server_thread: _server_thread.join(timeout=2); _server_thread = None


# ===================================================================
# ADDON REGISTRATION
# ===================================================================

class BlenderLinkPreferences(bpy.types.AddonPreferences):
    bl_idname = __name__

    port: bpy.props.IntProperty(name="Server Port", default=24280, min=1024, max=65535,
        description="HTTP port for receiving FModel exports")
    auto_start: bpy.props.BoolProperty(name="Auto-Start Server", default=True,
        description="Automatically start the server when Blender opens")

    def draw(self, context):
        layout = self.layout
        layout.prop(self, "port"); layout.prop(self, "auto_start")
        row = layout.row()
        row.operator("blenderlink.start_server", icon='PLAY')
        row.operator("blenderlink.stop_server", icon='PAUSE')
        if _server: layout.label(text=f"Server running on port {self.port}", icon='CHECKMARK')
        else: layout.label(text="Server stopped", icon='ERROR')
        if not _ueformat_available: layout.label(text="io_scene_ueformat not found", icon='ERROR')


class BLENDERLINK_OT_StartServer(bpy.types.Operator):
    bl_idname = "blenderlink.start_server"
    bl_label = "Start Server"
    bl_description = "Start the BlenderLink HTTP server"

    def execute(self, context):
        prefs = context.preferences.addons[__name__].preferences
        start_server(prefs.port)
        self.report({'INFO'}, f"BlenderLink server started on port {prefs.port}")
        return {'FINISHED'}


class BLENDERLINK_OT_StopServer(bpy.types.Operator):
    bl_idname = "blenderlink.stop_server"
    bl_label = "Stop Server"
    bl_description = "Stop the BlenderLink HTTP server"

    def execute(self, context):
        stop_server()
        self.report({'INFO'}, "BlenderLink server stopped")
        return {'FINISHED'}


class BLENDERLINK_PT_Panel(bpy.types.Panel):
    bl_label = "BlenderLink"
    bl_idname = "BLENDERLINK_PT_panel"
    bl_space_type = 'VIEW_3D'
    bl_region_type = 'UI'
    bl_category = "BlenderLink"

    def draw(self, context):
        layout = self.layout
        prefs = context.preferences.addons[__name__].preferences
        if _server:
            layout.label(text=f"Listening on port {prefs.port}", icon='CHECKMARK')
            layout.operator("blenderlink.stop_server", icon='PAUSE')
        else:
            layout.label(text="Server not running", icon='ERROR')
            layout.operator("blenderlink.start_server", icon='PLAY')
        if not _ueformat_available:
            layout.label(text="io_scene_ueformat required", icon='ERROR')


classes = (
    BlenderLinkPreferences,
    BLENDERLINK_OT_StartServer,
    BLENDERLINK_OT_StopServer,
    BLENDERLINK_PT_Panel,
)


def register():
    global UEFormatImport, UEModelOptions, UEAnimOptions
    for cls in classes:
        bpy.utils.register_class(cls)
    UEFormatImport, UEModelOptions, UEAnimOptions = _init_ueformat()
    prefs = bpy.context.preferences.addons.get(__name__)
    if prefs and prefs.preferences.auto_start:
        bpy.app.timers.register(lambda: _auto_start(), first_interval=1.0)


def _auto_start():
    try:
        prefs = bpy.context.preferences.addons.get(__name__)
        if prefs and prefs.preferences.auto_start:
            start_server(prefs.preferences.port)
    except Exception:
        pass
    return None


def unregister():
    stop_server()
    for cls in reversed(classes):
        bpy.utils.unregister_class(cls)


if __name__ == "__main__":
    register()
