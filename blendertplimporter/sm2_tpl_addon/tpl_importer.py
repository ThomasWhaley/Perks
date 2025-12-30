"""
TPL Blender Importer
Imports parsed TPL data into Blender
"""

import bpy
import bmesh
import math
import os
from mathutils import Matrix, Vector
from typing import List, Dict, Optional, Tuple

from . import tpl_parser
from .tpl_parser import (
    load_tpl, TPLNode, MeshData, Face, UVSet, Bone,
    get_all_mesh_nodes, get_node_by_uid,
    decompress_normal_from_int16, snorm16_to_float
)


# ============================================================================
# MAIN LOAD FUNCTION
# ============================================================================

def load(context, filepath: str, **kwargs) -> set:
    """Main entry point for importing TPL files"""
    
    import_armature = kwargs.get('import_armature', True)
    import_materials = kwargs.get('import_materials', True)
    flip_uvs = kwargs.get('flip_uvs', True)
    scale_factor = kwargs.get('scale_factor', 1.0)
    import_mode = kwargs.get('import_mode', 'USF')
    
    print(f"[TPL Import] Loading: {filepath}")
    print(f"[TPL Import] Options: armature={import_armature}, materials={import_materials}, flip_uvs={flip_uvs}, scale={scale_factor}")
    
    # Load and parse the file (auto-detect format)
    root, format_type = load_tpl(filepath)
    
    if not root:
        print(f"[TPL Import] Failed to parse file (detected format: {format_type})")
        return {'CANCELLED'}
    
    print(f"[TPL Import] Successfully parsed as {format_type} format")
    
    # Get base name for objects
    base_name = os.path.splitext(os.path.basename(filepath))[0]
    
    # Create a collection for this import
    collection = bpy.data.collections.new(base_name)
    context.scene.collection.children.link(collection)
    
    # Build armature if needed
    armature_obj = None
    bone_map = {}  # uid -> bone_name
    
    if import_armature:
        armature_obj, bone_map = create_armature(root, base_name, collection, scale_factor)
    
    # Import all meshes
    mesh_nodes = get_all_mesh_nodes(root)
    print(f"[TPL Import] Found {len(mesh_nodes)} mesh nodes")
    
    for node in mesh_nodes:
        create_mesh_object(
            node, 
            collection, 
            armature_obj, 
            bone_map,
            root,
            flip_uvs=flip_uvs,
            scale_factor=scale_factor,
            import_materials=import_materials
        )
    
    print(f"[TPL Import] Import complete")
    return {'FINISHED'}


# ============================================================================
# ARMATURE CREATION
# ============================================================================

def create_armature(root: TPLNode, name: str, collection, scale_factor: float) -> Tuple[Optional[bpy.types.Object], Dict[int, str]]:
    """Create armature from node hierarchy"""
    
    # Collect all nodes that could be bones
    all_nodes = collect_all_nodes(root)
    
    if len(all_nodes) < 2:
        return None, {}
    
    # Create armature
    armature = bpy.data.armatures.new(f"{name}_Armature")
    armature_obj = bpy.data.objects.new(f"{name}_Armature", armature)
    collection.objects.link(armature_obj)
    
    # Enter edit mode to create bones
    bpy.context.view_layer.objects.active = armature_obj
    bpy.ops.object.mode_set(mode='EDIT')
    
    bone_map = {}  # uid -> bone_name
    
    for node in all_nodes:
        # Create bone
        bone_name = sanitize_bone_name(node.name or f"bone_{node.uid}")
        bone = armature.edit_bones.new(bone_name)
        bone_map[node.uid] = bone_name
        
        # Get world position from matrix
        mat = node_matrix_to_blender(node.world_matrix, scale_factor)
        
        bone.head = mat.translation
        bone.tail = bone.head + Vector((0, 0.05 * scale_factor, 0))
        
        # Set parent
        if node.parent and node.parent.uid in bone_map:
            parent_bone = armature.edit_bones.get(bone_map[node.parent.uid])
            if parent_bone:
                bone.parent = parent_bone
                # Connect if close enough
                if (bone.head - parent_bone.tail).length < 0.001:
                    bone.use_connect = True
    
    bpy.ops.object.mode_set(mode='OBJECT')
    
    print(f"[TPL Import] Created armature with {len(bone_map)} bones")
    return armature_obj, bone_map


def collect_all_nodes(node: TPLNode) -> List[TPLNode]:
    """Recursively collect all nodes"""
    result = [node]
    for child in node.children:
        result.extend(collect_all_nodes(child))
    return result


def sanitize_bone_name(name: str) -> str:
    """Make bone name valid for Blender"""
    # Replace invalid characters
    name = name.replace(':', '_').replace('|', '_').replace(' ', '_')
    return name[:63]  # Blender limit


def node_matrix_to_blender(mat: tpl_parser.Matrix4, scale: float = 1.0) -> Matrix:
    """Convert TPL matrix to Blender matrix"""
    return Matrix((
        (mat.rows[0].x, mat.rows[0].y, mat.rows[0].z, mat.rows[0].w * scale),
        (mat.rows[1].x, mat.rows[1].y, mat.rows[1].z, mat.rows[1].w * scale),
        (mat.rows[2].x, mat.rows[2].y, mat.rows[2].z, mat.rows[2].w * scale),
        (mat.rows[3].x, mat.rows[3].y, mat.rows[3].z, mat.rows[3].w),
    ))


# ============================================================================
# MESH CREATION
# ============================================================================

def create_mesh_object(
    node: TPLNode,
    collection,
    armature_obj: Optional[bpy.types.Object],
    bone_map: Dict[int, str],
    root: TPLNode,
    flip_uvs: bool = True,
    scale_factor: float = 1.0,
    import_materials: bool = True
) -> Optional[bpy.types.Object]:
    """Create a Blender mesh object from TPLNode"""
    
    mesh_data = node.mesh
    if not mesh_data or not mesh_data.vertices:
        return None
    
    name = node.name or f"mesh_{node.uid}"
    print(f"[TPL Import] Creating mesh: {name} ({len(mesh_data.vertices)} verts)")
    
    # Create mesh
    mesh = bpy.data.meshes.new(name)
    obj = bpy.data.objects.new(name, mesh)
    collection.objects.link(obj)
    
    # Apply scale to vertices
    vertices = [(v[0] * scale_factor, v[1] * scale_factor, v[2] * scale_factor) 
                for v in mesh_data.vertices]
    
    # Build faces - need to parse the face data properly
    faces = []
    if mesh_data.faces:
        # Faces might be stored differently, handle various formats
        face_data = mesh_data.faces
        
        # Check if we have proper triangle indices
        if hasattr(face_data[0], 'a') and hasattr(face_data[0], 'b') and hasattr(face_data[0], 'c'):
            # Face objects with a, b, c indices
            faces = [(f.a, f.b, f.c) for f in face_data if f.b != 0 or f.c != 0]
        else:
            # Assume flat list of indices, group into triangles
            for i in range(0, len(face_data) - 2, 3):
                faces.append((face_data[i], face_data[i+1], face_data[i+2]))
    
    # If no valid faces, try to create from vertex count (assume triangles)
    if not faces and len(vertices) >= 3:
        for i in range(0, len(vertices) - 2, 3):
            faces.append((i, i+1, i+2))
    
    # Create the mesh geometry
    mesh.from_pydata(vertices, [], faces)
    
    # Apply normals
    if mesh_data.normals and len(mesh_data.normals) == len(vertices):
        apply_normals(mesh, mesh_data.normals)
    
    # Apply UVs
    if mesh_data.uv_sets:
        apply_uvs(mesh, mesh_data.uv_sets, flip_uvs)
    
    # Apply vertex colors
    if mesh_data.colors:
        apply_vertex_colors(mesh, mesh_data.colors)
    
    # Apply materials
    if import_materials and mesh_data.materials:
        apply_materials(obj, mesh_data.materials)
    
    # Apply skinning
    if armature_obj and mesh_data.skins and mesh_data.bones:
        apply_skinning(obj, mesh_data, armature_obj, bone_map, root)
    
    # Update mesh
    mesh.update()
    mesh.validate()
    
    return obj


# ============================================================================
# NORMALS
# ============================================================================

def apply_normals(mesh: bpy.types.Mesh, normals: List[Tuple[float, float, float]]):
    """Apply custom normals to mesh"""
    try:
        # Ensure we have loop normals
        if not mesh.loops:
            return
        
        # Build per-loop normals from per-vertex normals
        loop_normals = []
        for loop in mesh.loops:
            if loop.vertex_index < len(normals):
                loop_normals.append(normals[loop.vertex_index])
            else:
                loop_normals.append((0, 0, 1))
        
        # Apply custom normals
        mesh.normals_split_custom_set(loop_normals)
        mesh.use_auto_smooth = True
        
    except Exception as e:
        print(f"[TPL Import] Warning: Could not apply normals: {e}")


# ============================================================================
# UVS
# ============================================================================

def apply_uvs(mesh: bpy.types.Mesh, uv_sets: List[UVSet], flip_v: bool = True):
    """Apply UV coordinates to mesh"""
    
    for i, uv_set in enumerate(uv_sets):
        if not uv_set.uvs:
            continue
        
        # Create UV layer
        uv_name = uv_set.name or f"UVMap_{i}"
        uv_layer = mesh.uv_layers.new(name=uv_name)
        
        if not uv_layer:
            continue
        
        # Apply UVs per loop
        for loop_idx, loop in enumerate(mesh.loops):
            vert_idx = loop.vertex_index
            
            if vert_idx < len(uv_set.uvs):
                u, v = uv_set.uvs[vert_idx]
                
                # Flip V coordinate (LibSaber always does this)
                if flip_v:
                    v = 1.0 - v
                
                uv_layer.data[loop_idx].uv = (u, v)
    
    print(f"[TPL Import] Applied {len(uv_sets)} UV set(s)")


# ============================================================================
# VERTEX COLORS
# ============================================================================

def apply_vertex_colors(mesh: bpy.types.Mesh, color_sets: List[List[Tuple[float, float, float, float]]]):
    """Apply vertex colors to mesh"""
    
    for i, colors in enumerate(color_sets):
        if not colors:
            continue
        
        # Create color attribute
        color_name = f"Color_{i}"
        
        # Use new color attributes API (Blender 3.2+)
        if hasattr(mesh, 'color_attributes'):
            color_attr = mesh.color_attributes.new(
                name=color_name,
                type='FLOAT_COLOR',
                domain='CORNER'
            )
            
            for loop_idx, loop in enumerate(mesh.loops):
                vert_idx = loop.vertex_index
                if vert_idx < len(colors):
                    color_attr.data[loop_idx].color = colors[vert_idx]
        else:
            # Fallback for older Blender
            color_layer = mesh.vertex_colors.new(name=color_name)
            for loop_idx, loop in enumerate(mesh.loops):
                vert_idx = loop.vertex_index
                if vert_idx < len(colors):
                    color_layer.data[loop_idx].color = colors[vert_idx]
    
    print(f"[TPL Import] Applied {len(color_sets)} color set(s)")


# ============================================================================
# MATERIALS
# ============================================================================

def apply_materials(obj: bpy.types.Object, materials: List[tpl_parser.Material]):
    """Create and apply materials to object"""
    
    for mat_data in materials:
        mat_name = mat_data.name or "Material"
        
        # Check if material already exists
        mat = bpy.data.materials.get(mat_name)
        if not mat:
            mat = bpy.data.materials.new(name=mat_name)
            mat.use_nodes = True
            
            # Store texture reference as custom property
            if mat_data.texture_name:
                mat["texture_name"] = mat_data.texture_name
            if mat_data.shader_name:
                mat["shader_name"] = mat_data.shader_name
            
            # Set up basic principled BSDF
            if mat.node_tree:
                nodes = mat.node_tree.nodes
                bsdf = nodes.get("Principled BSDF")
                if bsdf:
                    # Default settings
                    bsdf.inputs["Roughness"].default_value = 0.5
                    bsdf.inputs["Specular IOR Level"].default_value = 0.5
        
        obj.data.materials.append(mat)
    
    print(f"[TPL Import] Applied {len(materials)} material(s)")


# ============================================================================
# SKINNING
# ============================================================================

def apply_skinning(
    obj: bpy.types.Object,
    mesh_data: MeshData,
    armature_obj: bpy.types.Object,
    bone_map: Dict[int, str],
    root: TPLNode
):
    """Apply skinning weights to mesh"""
    
    if not mesh_data.skins or not mesh_data.bones:
        return
    
    # Create vertex groups for each bone
    bone_to_group = {}
    for bone in mesh_data.bones:
        # Find the node for this bone
        node = get_node_by_uid(root, bone.node_uid)
        if node and node.uid in bone_map:
            group_name = bone_map[node.uid]
            if group_name not in obj.vertex_groups:
                obj.vertex_groups.new(name=group_name)
            bone_to_group[mesh_data.bones.index(bone)] = group_name
    
    # Apply weights
    for vert_idx, skin in enumerate(mesh_data.skins):
        for i, (bone_idx, weight) in enumerate(zip(skin.bone_indices, skin.weights)):
            if weight > 0.0 and bone_idx in bone_to_group:
                group_name = bone_to_group[bone_idx]
                group = obj.vertex_groups.get(group_name)
                if group:
                    group.add([vert_idx], weight, 'REPLACE')
    
    # Parent to armature
    obj.parent = armature_obj
    
    # Add armature modifier
    mod = obj.modifiers.new(name="Armature", type='ARMATURE')
    mod.object = armature_obj
    mod.use_vertex_groups = True
    
    print(f"[TPL Import] Applied skinning with {len(mesh_data.bones)} bone(s)")
