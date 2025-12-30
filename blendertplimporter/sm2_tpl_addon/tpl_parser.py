"""
TPL Format Parser for Space Marine 2
Based on LibSaber decompilation by Wildenhaus

This module handles all binary parsing and data conversion.
Supports both:
- USF format (starts with Int32 version)
- 1SER format (starts with "1SER" magic)
"""

import struct
import math
from dataclasses import dataclass, field
from typing import List, Optional, Dict, Tuple, Any, BinaryIO
from enum import IntFlag
from io import BytesIO
from pathlib import Path


# ============================================================================
# FVF FLAGS (Flexible Vertex Format) - From LibSaber
# ============================================================================

class FVFFlags(IntFlag):
    """Vertex format flags determining what data is present in vertex buffers"""
    NONE = 0
    
    VERT = 1 << 0
    VERT_4D = 1 << 1
    VERT_2D = 1 << 2
    VERT_COMPR = 1 << 3
    MASKING_FLAGS = 1 << 4
    BS_INFO = 1 << 5
    
    WEIGHT4 = 1 << 6
    WEIGHT8 = 1 << 7
    INDICES = 1 << 8
    INDICES16 = 1 << 9
    
    NORM = 1 << 10
    NORM_COMPR = 1 << 11
    NORM_IN_VERT4 = 1 << 12
    
    TANG0 = 1 << 13
    TANG1 = 1 << 14
    TANG2 = 1 << 15
    TANG3 = 1 << 16
    TANG4 = 1 << 17
    TANG_COMPR = 1 << 18
    
    COLOR0 = 1 << 19
    COLOR1 = 1 << 20
    COLOR2 = 1 << 21
    COLOR3 = 1 << 22
    COLOR4 = 1 << 23
    COLOR5 = 1 << 24
    
    TEX0 = 1 << 25
    TEX1 = 1 << 26
    TEX2 = 1 << 27
    TEX3 = 1 << 28
    TEX4 = 1 << 29
    TEX5 = 1 << 30
    TEX0_COMPR = 1 << 31
    TEX1_COMPR = 1 << 32
    TEX2_COMPR = 1 << 33
    TEX3_COMPR = 1 << 34
    TEX4_COMPR = 1 << 35
    TEX5_COMPR = 1 << 36


# ============================================================================
# NUMERIC CONVERSION FUNCTIONS - From LibSaber
# ============================================================================

def snorm16_to_float(value: int) -> float:
    """Convert signed 16-bit normalized integer to float [-1.0, 1.0]"""
    return value / 32767.0


def snorm8_to_float(value: int) -> float:
    """Convert signed 8-bit normalized integer to float [-1.0, 1.0]"""
    return value / 127.0


def unorm8_to_float(value: int) -> float:
    """Convert unsigned 8-bit normalized integer to float [0.0, 1.0]"""
    return value / 255.0


def frac(x: float) -> float:
    """Return fractional part (HLSL frac)"""
    return x - math.floor(x)


def saturate(x: float) -> float:
    """Clamp to [0.0, 1.0] (HLSL saturate)"""
    return max(0.0, min(1.0, x))


def sign_f(x: int) -> float:
    """Return sign (-1, 0, or 1)"""
    if x > 0:
        return 1.0
    elif x < 0:
        return -1.0
    return 0.0


# ============================================================================
# NORMAL DECOMPRESSION - From LibSaber shader analysis
# ============================================================================

def decompress_normal_from_int16(w: int) -> Tuple[float, float, float]:
    """
    Decompress normal from packed Int16 value.
    
    From LibSaber's analysis of common_input.vsh:
        xz = (-1 + 2 * frac(float2(1/181, 1/181/181) * abs(w))) * float2(181/179, 181/180)
        y = sign(w) * sqrt(saturate(1 - xz.x² - xz.y²))
    """
    if w == -32768:
        w = 0
    
    abs_w = abs(w)
    
    frac_x = frac((1.0 / 181.0) * abs_w)
    frac_z = frac((1.0 / 181.0 / 181.0) * abs_w)
    
    x = (-1.0 + 2.0 * frac_x) * (181.0 / 179.0)
    z = (-1.0 + 2.0 * frac_z) * (181.0 / 180.0)
    
    y_squared = saturate(1.0 - x * x - z * z)
    y = sign_f(w) * math.sqrt(y_squared)
    
    return (x, y, z)


def decompress_normal_from_float(w: float) -> Tuple[float, float, float]:
    """
    Decompress normal from packed float value.
    
    From LibSaber's analysis of common_input.vsh:
        norm = -1.0 + 2.0 * float3(1/256, 1/256/256, 1/256/256/256) * w
    """
    divisors = (0.00390625, 0.0000152587890625, 0.000000059604644775390625)
    
    x = -1.0 + 2.0 * frac(divisors[0] * w)
    y = -1.0 + 2.0 * frac(divisors[1] * w)
    z = -1.0 + 2.0 * frac(divisors[2] * w)
    
    return (x, y, z)


# ============================================================================
# DATA STRUCTURES
# ============================================================================

@dataclass
class Vector2:
    x: float = 0.0
    y: float = 0.0
    
    def to_tuple(self) -> Tuple[float, float]:
        return (self.x, self.y)


@dataclass
class Vector3:
    x: float = 0.0
    y: float = 0.0
    z: float = 0.0
    
    def to_tuple(self) -> Tuple[float, float, float]:
        return (self.x, self.y, self.z)


@dataclass
class Vector4:
    x: float = 0.0
    y: float = 0.0
    z: float = 0.0
    w: float = 0.0
    
    def to_tuple(self) -> Tuple[float, float, float, float]:
        return (self.x, self.y, self.z, self.w)


@dataclass
class Matrix4:
    """4x4 Matrix (row-major)"""
    rows: List[Vector4] = field(default_factory=lambda: [
        Vector4(1, 0, 0, 0),
        Vector4(0, 1, 0, 0),
        Vector4(0, 0, 1, 0),
        Vector4(0, 0, 0, 1)
    ])
    
    def read(self, reader: BinaryIO):
        for row in self.rows:
            row.x, row.y, row.z, row.w = struct.unpack('<ffff', reader.read(16))
        return self
    
    @staticmethod
    def identity() -> 'Matrix4':
        return Matrix4()


@dataclass
class Face:
    """Triangle face"""
    a: int = 0
    b: int = 0
    c: int = 0


@dataclass
class UVSet:
    """UV coordinate set"""
    uvs: List[Tuple[float, float]] = field(default_factory=list)
    name: str = ""


@dataclass
class VertexSkin:
    """Skinning data per vertex"""
    bone_indices: List[int] = field(default_factory=list)
    weights: List[float] = field(default_factory=list)


@dataclass
class Bone:
    """Bone reference for skinning"""
    node_uid: int = 0
    bind_matrix: Matrix4 = field(default_factory=Matrix4)


@dataclass
class Material:
    """Material reference"""
    name: str = ""
    texture_name: str = ""
    shader_name: str = ""


@dataclass
class MeshData:
    """Complete mesh data"""
    vertices: List[Tuple[float, float, float]] = field(default_factory=list)
    normals: List[Tuple[float, float, float]] = field(default_factory=list)
    faces: List[Face] = field(default_factory=list)
    uv_sets: List[UVSet] = field(default_factory=list)
    colors: List[List[Tuple[float, float, float, float]]] = field(default_factory=list)
    tangents: List[Tuple[float, float, float, float]] = field(default_factory=list)
    skins: List[VertexSkin] = field(default_factory=list)
    bones: List[Bone] = field(default_factory=list)
    materials: List[Material] = field(default_factory=list)


@dataclass
class TPLNode:
    """Node in TPL hierarchy"""
    uid: int = 0
    name: str = ""
    local_matrix: Matrix4 = field(default_factory=Matrix4)
    world_matrix: Matrix4 = field(default_factory=Matrix4)
    mesh: Optional[MeshData] = None
    children: List['TPLNode'] = field(default_factory=list)
    parent: Optional['TPLNode'] = None
    maya_node_id: str = ""


# ============================================================================
# STRING UTILITIES
# ============================================================================

def read_string(reader: BinaryIO) -> str:
    """Read length-prefixed string (Int32 length + UTF8 data)"""
    length = struct.unpack('<i', reader.read(4))[0]
    if length <= 0 or length > 10000:
        return ""
    data = reader.read(length)
    return data.decode('utf-8', errors='replace')


def read_cstring(reader: BinaryIO) -> str:
    """Read null-terminated string"""
    chars = []
    while True:
        c = reader.read(1)
        if c == b'\x00' or c == b'':
            break
        chars.append(c)
    return b''.join(chars).decode('utf-8', errors='replace')


# ============================================================================
# PROPERTY SECTION PARSER
# ============================================================================

class PropertySection:
    """Parse property sections (key=value format)"""
    
    def __init__(self, data: str = ""):
        self.fields: Dict[str, Any] = {}
        if data:
            self._parse(data)
    
    def _parse(self, data: str):
        data = data.strip()
        if not data:
            return
        
        lines = data.replace(';', '\n').split('\n')
        for line in lines:
            line = line.strip()
            if '=' in line:
                key, _, value = line.partition('=')
                key = key.strip()
                value = value.strip()
                if value.startswith('"') and value.endswith('"'):
                    value = value[1:-1]
                self.fields[key] = value
    
    def get(self, key: str, default=None):
        return self.fields.get(key, default)


# ============================================================================
# 1SER FORMAT PARSER (Raw TPL format)
# ============================================================================

class SERParser:
    """Parser for 1SER format TPL files (raw game format)"""
    
    POSITION_SCALE = 0.01
    
    def __init__(self):
        self.tpl_data: bytes = b''
        self.tpl_geom_data: bytes = b''
        self.version = 0
        self.root: Optional[TPLNode] = None
        self.objects: List[TPLNode] = []
        
        # Counts from OGM1
        self.buffer_count = 0
        self.mesh_count = 0
        self.object_count = 0
        self.submesh_count = 0
    
    def load(self, filepath: str) -> bool:
        """Load 1SER format TPL file"""
        try:
            filepath = Path(filepath)
            
            # TPL files can be folders or files
            if filepath.is_dir():
                tpl_file = filepath / filepath.name
                tpl_data_file = filepath / f"{filepath.name}_data"
            else:
                tpl_file = filepath
                tpl_data_file = Path(str(filepath) + "_data")
            
            # Load main TPL file
            if not tpl_file.exists():
                print(f"[TPL Parser] TPL file not found: {tpl_file}")
                return False
            
            with open(tpl_file, 'rb') as f:
                self.tpl_data = f.read()
            
            # Check magic
            if self.tpl_data[:4] != b'1SER':
                print("[TPL Parser] Not a 1SER format file")
                return False
            
            # Load geometry data file
            if tpl_data_file.exists():
                with open(tpl_data_file, 'rb') as f:
                    self.tpl_geom_data = f.read()
                print(f"[TPL Parser] Loaded geometry data: {len(self.tpl_geom_data)} bytes")
            else:
                print(f"[TPL Parser] Warning: No geometry data file found at {tpl_data_file}")
            
            return self._parse()
            
        except Exception as e:
            print(f"[TPL Parser] Error loading file: {e}")
            import traceback
            traceback.print_exc()
            return False
    
    def _parse(self) -> bool:
        """Parse the 1SER TPL data"""
        try:
            # Parse TPL1 section
            if not self._parse_tpl1():
                return False
            
            # Parse OGM1 section
            if not self._parse_ogm1():
                return False
            
            # Parse geometry
            self._parse_geometry()
            
            # Build root node
            self._build_hierarchy()
            
            return True
            
        except Exception as e:
            print(f"[TPL Parser] Parse error: {e}")
            import traceback
            traceback.print_exc()
            return False
    
    def _parse_tpl1(self) -> bool:
        """Parse TPL1 section"""
        pos = self.tpl_data.find(b'TPL1')
        if pos < 0:
            print("[TPL Parser] TPL1 section not found")
            return False
        
        pos += 4
        self.version = struct.unpack_from('<I', self.tpl_data, pos)[0]
        pos += 4
        data_size = struct.unpack_from('<I', self.tpl_data, pos)[0]
        
        print(f"[TPL Parser] TPL1: version={self.version}, data_size={data_size}")
        return True
    
    def _parse_ogm1(self) -> bool:
        """Parse OGM1 (Object Geometry Manager) section"""
        pos = self.tpl_data.find(b'OGM1')
        if pos < 0:
            print("[TPL Parser] OGM1 section not found")
            return False
        
        pos += 4
        
        # Parse header counts
        self.buffer_count = struct.unpack_from('<H', self.tpl_data, pos)[0]
        pos += 2
        pos += 2  # unk
        self.mesh_count = struct.unpack_from('<H', self.tpl_data, pos)[0]
        pos += 2
        self.object_count = struct.unpack_from('<H', self.tpl_data, pos)[0]
        pos += 2
        self.submesh_count = struct.unpack_from('<H', self.tpl_data, pos)[0]
        pos += 2
        
        print(f"[TPL Parser] OGM1: {self.buffer_count} buffers, {self.mesh_count} meshes, "
              f"{self.object_count} objects, {self.submesh_count} submeshes")
        
        # Parse object names
        self._parse_object_names(pos)
        
        return True
    
    def _parse_object_names(self, start_pos: int):
        """Extract object names from OGM1 section"""
        ogm1_end = self.tpl_data.find(b'ANIM')
        if ogm1_end < 0:
            ogm1_end = len(self.tpl_data)
        
        pos = start_pos + 100  # Skip header
        
        while pos < ogm1_end - 10:
            name, new_pos = self._read_string(pos)
            if name and len(name) > 1 and len(name) < 100:
                if any(c.isalpha() for c in name):
                    node = TPLNode(
                        uid=len(self.objects),
                        name=name
                    )
                    self.objects.append(node)
                    pos = new_pos
                    continue
            pos += 1
        
        print(f"[TPL Parser] Found {len(self.objects)} objects")
    
    def _read_string(self, pos: int) -> Tuple[str, int]:
        """Read length-prefixed string"""
        if pos + 4 > len(self.tpl_data):
            return "", pos
        
        length = struct.unpack_from('<I', self.tpl_data, pos)[0]
        if length > 256 or length == 0:
            return "", pos
        
        pos += 4
        if pos + length > len(self.tpl_data):
            return "", pos
        
        try:
            s = self.tpl_data[pos:pos+length].decode('utf-8').rstrip('\0')
            return s, pos + length
        except:
            return "", pos
    
    def _parse_geometry(self):
        """Parse geometry from data file"""
        if not self.tpl_geom_data:
            print("[TPL Parser] No geometry data to parse")
            return
        
        # Create mesh node with geometry
        mesh_node = TPLNode(uid=0, name="mesh")
        mesh_node.mesh = MeshData()
        
        # Find vertex and face data boundaries
        # This is a heuristic - look for patterns
        data_len = len(self.tpl_geom_data)
        
        # Try to find face indices (sequence of valid uint16 triplets)
        face_offset = self._find_face_data_offset()
        
        if face_offset > 0:
            vertex_data_end = face_offset
        else:
            # Fallback: assume faces start at 90% of file
            vertex_data_end = int(data_len * 0.9)
            face_offset = vertex_data_end
        
        print(f"[TPL Parser] Vertex data: 0 - {vertex_data_end}, Face data: {face_offset} - {data_len}")
        
        # Parse vertices (8-byte stride: 3x int16 pos + int16 packed normal)
        seen_positions = {}
        vertex_count = vertex_data_end // 8
        
        for i in range(vertex_count):
            offset = i * 8
            if offset + 8 > len(self.tpl_geom_data):
                break
            
            x, y, z, packed = struct.unpack_from('<hhhH', self.tpl_geom_data, offset)
            
            pos = (x * self.POSITION_SCALE, y * self.POSITION_SCALE, z * self.POSITION_SCALE)
            pos_key = (round(pos[0], 4), round(pos[1], 4), round(pos[2], 4))
            
            if pos_key not in seen_positions:
                seen_positions[pos_key] = len(mesh_node.mesh.vertices)
                
                # Decompress normal
                nx, ny, nz = decompress_normal_from_int16(packed)
                
                mesh_node.mesh.vertices.append(pos)
                mesh_node.mesh.normals.append((nx, ny, nz))
        
        print(f"[TPL Parser] Parsed {len(mesh_node.mesh.vertices)} unique vertices")
        
        # Parse faces
        face_data_size = min(data_len - face_offset, 1000000)
        max_vertex_idx = len(mesh_node.mesh.vertices)
        
        for i in range(face_data_size // 6):
            offset = face_offset + i * 6
            if offset + 6 > len(self.tpl_geom_data):
                break
            
            a, b, c = struct.unpack_from('<HHH', self.tpl_geom_data, offset)
            
            # Validate indices
            if a < max_vertex_idx and b < max_vertex_idx and c < max_vertex_idx:
                mesh_node.mesh.faces.append(Face(a, b, c))
        
        print(f"[TPL Parser] Parsed {len(mesh_node.mesh.faces)} faces")
        
        # Store mesh node
        if mesh_node.mesh.vertices and mesh_node.mesh.faces:
            self.objects.insert(0, mesh_node)
    
    def _find_face_data_offset(self) -> int:
        """Try to find where face index data starts"""
        data_len = len(self.tpl_geom_data)
        
        # Look for OGM1 section in geometry data that might contain offset info
        pos = self.tpl_geom_data.find(b'OGM1')
        if pos >= 0:
            # Read counts after OGM1
            pass  # Would need to parse buffer definitions
        
        # Heuristic: scan for valid face patterns
        # Face data should have reasonable uint16 values that form triangles
        test_positions = [
            int(data_len * 0.8),
            int(data_len * 0.85),
            int(data_len * 0.9),
            int(data_len * 0.7),
        ]
        
        for test_pos in test_positions:
            # Align to 6 bytes (triangle)
            test_pos = (test_pos // 6) * 6
            valid_count = 0
            
            for i in range(100):  # Test 100 triangles
                offset = test_pos + i * 6
                if offset + 6 > data_len:
                    break
                
                a, b, c = struct.unpack_from('<HHH', self.tpl_geom_data, offset)
                
                # Check if these look like valid face indices
                max_expected = 100000  # Reasonable vertex count
                if a < max_expected and b < max_expected and c < max_expected:
                    if a != b and b != c and a != c:  # Not degenerate
                        valid_count += 1
            
            if valid_count > 80:  # 80% valid
                return test_pos
        
        return 0
    
    def _build_hierarchy(self):
        """Build node hierarchy"""
        if self.objects:
            self.root = TPLNode(uid=-1, name=".root.")
            for obj in self.objects:
                obj.parent = self.root
                self.root.children.append(obj)


# ============================================================================
# USF FORMAT PARSER
# ============================================================================

class USFParser:
    """Parse USF/TPL format files"""
    
    def __init__(self):
        self.version = 0
        self.source_path = ""
        self.file_type = ""
        self.root: Optional[TPLNode] = None
    
    def load(self, filepath: str) -> bool:
        """Load USF file from disk"""
        try:
            with open(filepath, 'rb') as f:
                reader = BytesIO(f.read())
            return self._parse(reader)
        except Exception as e:
            print(f"[TPL Parser] Error loading file: {e}")
            return False
    
    def _parse(self, reader: BinaryIO) -> bool:
        """Parse USF data"""
        try:
            self.version = struct.unpack('<i', reader.read(4))[0]
            self.source_path = read_string(reader)
            self.file_type = read_string(reader)
            
            # Skip options
            options_str = read_string(reader)
            
            # Parse scene
            scene_version = struct.unpack('<I', reader.read(4))[0]
            has_root = struct.unpack('<B', reader.read(1))[0]
            
            if has_root:
                self.root = self._parse_node(reader)
            
            return True
        except Exception as e:
            print(f"[TPL Parser] Parse error: {e}")
            import traceback
            traceback.print_exc()
            return False
    
    def _parse_node(self, reader: BinaryIO) -> TPLNode:
        """Parse a single node"""
        node = TPLNode()
        
        version = struct.unpack('<i', reader.read(4))[0]
        node.uid = struct.unpack('<i', reader.read(4))[0]
        node.name = read_string(reader)
        
        model_name = read_string(reader)
        affixes = read_string(reader)
        
        # PSES (property section)
        pses_version = struct.unpack('<i', reader.read(4))[0]
        pses_data = read_string(reader)
        
        # Matrices
        node.local_matrix.read(reader)
        node.world_matrix.read(reader)
        
        local_original = Matrix4()
        local_original.read(reader)
        
        source_id = read_string(reader)
        
        # Optional sections (flagged)
        # LWI Info
        if struct.unpack('<B', reader.read(1))[0]:
            self._skip_lwi_info(reader)
        
        # Mesh
        if struct.unpack('<B', reader.read(1))[0]:
            node.mesh = self._parse_mesh(reader)
        
        # Animation
        if struct.unpack('<B', reader.read(1))[0]:
            self._skip_animation(reader)
        
        # Actor
        if struct.unpack('<B', reader.read(1))[0]:
            self._skip_actor(reader)
        
        # Various optional sections based on version
        if version >= 0x102:
            if struct.unpack('<B', reader.read(1))[0]:
                self._skip_refloc(reader)
        
        if version >= 0x103:
            if struct.unpack('<B', reader.read(1))[0]:
                self._skip_light(reader)
        
        if version >= 0x104:
            if struct.unpack('<B', reader.read(1))[0]:
                self._skip_camera(reader)
        
        if version >= 0x106:
            if struct.unpack('<B', reader.read(1))[0]:
                self._skip_nav_wp(reader)
        
        if version >= 0x107:
            if struct.unpack('<B', reader.read(1))[0]:
                self._skip_nav_ns(reader)
        
        if version >= 0x10A:
            if struct.unpack('<B', reader.read(1))[0]:
                self._skip_ref_desc(reader)
        
        if version >= 0x10C:
            if struct.unpack('<B', reader.read(1))[0]:
                self._skip_decal(reader)
        
        if version >= 0x10D:
            if struct.unpack('<B', reader.read(1))[0]:
                self._skip_anim_extra(reader)
        
        if version >= 0x10E:
            if struct.unpack('<B', reader.read(1))[0]:
                self._skip_ecs(reader)
        
        if version >= 0x10B:
            node.maya_node_id = read_string(reader)
        
        # Children
        child_count = struct.unpack('<i', reader.read(4))[0]
        for _ in range(child_count):
            child = self._parse_node(reader)
            child.parent = node
            node.children.append(child)
        
        return node
    
    def _parse_mesh(self, reader: BinaryIO) -> MeshData:
        """Parse mesh data"""
        mesh = MeshData()
        
        version = struct.unpack('<i', reader.read(4))[0]
        
        # Vertices
        vertex_count = struct.unpack('<i', reader.read(4))[0]
        for _ in range(vertex_count):
            x, y, z = struct.unpack('<fff', reader.read(12))
            mesh.vertices.append((x, y, z))
        
        # Normals
        normal_count = struct.unpack('<i', reader.read(4))[0]
        for _ in range(normal_count):
            x, y, z = struct.unpack('<fff', reader.read(12))
            mesh.normals.append((x, y, z))
        
        # Colors
        if version < 0x102:
            color_count = struct.unpack('<i', reader.read(4))[0]
            if color_count > 0:
                colors = []
                for _ in range(color_count):
                    packed = struct.unpack('<I', reader.read(4))[0]
                    r = ((packed >> 0) & 0xFF) / 255.0
                    g = ((packed >> 8) & 0xFF) / 255.0
                    b = ((packed >> 16) & 0xFF) / 255.0
                    a = ((packed >> 24) & 0xFF) / 255.0
                    colors.append((r, g, b, a))
                mesh.colors.append(colors)
        else:
            color_set_count = struct.unpack('<i', reader.read(4))[0]
            for _ in range(color_set_count):
                color_count = struct.unpack('<i', reader.read(4))[0]
                colors = []
                for _ in range(color_count):
                    packed = struct.unpack('<I', reader.read(4))[0]
                    r = ((packed >> 0) & 0xFF) / 255.0
                    g = ((packed >> 8) & 0xFF) / 255.0
                    b = ((packed >> 16) & 0xFF) / 255.0
                    a = ((packed >> 24) & 0xFF) / 255.0
                    colors.append((r, g, b, a))
                mesh.colors.append(colors)
        
        # UV Sets
        uv_set_count = struct.unpack('<i', reader.read(4))[0]
        for i in range(uv_set_count):
            uv_count = struct.unpack('<i', reader.read(4))[0]
            uv_set = UVSet()
            for _ in range(uv_count):
                u, v = struct.unpack('<ff', reader.read(8))
                uv_set.uvs.append((u, v))
            
            if version >= 0x107:
                uv_set.name = read_string(reader)
            else:
                uv_set.name = f"uv{i}"
            
            mesh.uv_sets.append(uv_set)
        
        # Tangents
        if version >= 0x103:
            tangent_set_count = struct.unpack('<i', reader.read(4))[0]
            for _ in range(tangent_set_count):
                tangent_count = struct.unpack('<i', reader.read(4))[0]
                for _ in range(tangent_count):
                    x, y, z, w = struct.unpack('<ffff', reader.read(16))
                    mesh.tangents.append((x, y, z, w))
        
        # Faces
        face_count = struct.unpack('<i', reader.read(4))[0]
        for _ in range(face_count):
            data = struct.unpack('<I', reader.read(4))[0]
            mat_idx = data & 0x1FFFFFFF
            mesh.faces.append(Face(mat_idx, 0, 0))  # Material index stored in 'a'
        
        # Materials
        material_count = struct.unpack('<i', reader.read(4))[0]
        for _ in range(material_count):
            mat = Material()
            mat.name = read_string(reader)
            
            # Material data
            mat_version = struct.unpack('<i', reader.read(4))[0]
            if mat_version >= 0x10A:
                vertex_color_usage = struct.unpack('<I', reader.read(4))[0]
                mat_data_str = read_string(reader)
                props = PropertySection(mat_data_str)
                mat.texture_name = props.get('textureName', '')
                mat.shader_name = props.get('shadingMtl_Mtl', '')
            
            mesh.materials.append(mat)
        
        # Skinning
        bones_per_vertex = struct.unpack('<i', reader.read(4))[0]
        skin_count = struct.unpack('<i', reader.read(4))[0]
        
        for _ in range(skin_count):
            skin = VertexSkin()
            skin.bone_indices = list(struct.unpack(f'<{bones_per_vertex}i', reader.read(4 * bones_per_vertex)))
            skin.weights = list(struct.unpack(f'<{bones_per_vertex}f', reader.read(4 * bones_per_vertex)))
            mesh.skins.append(skin)
        
        if version >= 0x104:
            skinning_method = struct.unpack('<B', reader.read(1))[0]
        
        # Bones
        bone_count = struct.unpack('<i', reader.read(4))[0]
        for _ in range(bone_count):
            bone = Bone()
            bone.node_uid = struct.unpack('<i', reader.read(4))[0]
            bone.bind_matrix.read(reader)
            mesh.bones.append(bone)
        
        # Bone pairs
        if version >= 0x105:
            pair_count = struct.unpack('<i', reader.read(4))[0]
            reader.read(pair_count * 8)  # Skip
        
        # Blend shapes
        if version >= 0x106:
            blend_count = struct.unpack('<i', reader.read(4))[0]
            for _ in range(blend_count):
                key = read_string(reader)
                # Skip SPL data - complex format
        
        return mesh
    
    # Skip methods for optional sections
    def _skip_lwi_info(self, reader): pass
    def _skip_animation(self, reader): pass
    def _skip_actor(self, reader): pass
    def _skip_refloc(self, reader): pass
    def _skip_light(self, reader): pass
    def _skip_camera(self, reader): pass
    def _skip_nav_wp(self, reader): pass
    def _skip_nav_ns(self, reader): pass
    def _skip_ref_desc(self, reader): pass
    def _skip_decal(self, reader): pass
    def _skip_anim_extra(self, reader): pass
    def _skip_ecs(self, reader): pass


# ============================================================================
# UTILITY FUNCTIONS
# ============================================================================

def get_all_mesh_nodes(node: TPLNode) -> List[TPLNode]:
    """Recursively get all nodes with mesh data"""
    result = []
    if node.mesh:
        result.append(node)
    for child in node.children:
        result.extend(get_all_mesh_nodes(child))
    return result


def get_node_by_uid(root: TPLNode, uid: int) -> Optional[TPLNode]:
    """Find a node by UID"""
    if root.uid == uid:
        return root
    for child in root.children:
        result = get_node_by_uid(child, uid)
        if result:
            return result
    return None


# ============================================================================
# UNIFIED LOADER
# ============================================================================

def load_tpl(filepath: str) -> Tuple[Optional[TPLNode], str]:
    """
    Load a TPL file, auto-detecting format.
    
    Returns:
        Tuple of (root_node, format_type)
        format_type is 'USF', '1SER', or 'UNKNOWN'
    """
    try:
        with open(filepath, 'rb') as f:
            header = f.read(8)
    except Exception as e:
        print(f"[TPL Loader] Cannot read file: {e}")
        return None, 'UNKNOWN'
    
    # Check for 1SER format
    if header[:4] == b'1SER':
        print("[TPL Loader] Detected 1SER format")
        parser = SERParser()
        if parser.load(filepath):
            return parser.root, '1SER'
        return None, '1SER'
    
    # Try USF format (starts with int32 version, usually small number)
    version = struct.unpack('<i', header[:4])[0]
    if 0 < version < 1000:
        print(f"[TPL Loader] Trying USF format (version={version})")
        parser = USFParser()
        if parser.load(filepath):
            return parser.root, 'USF'
    
    # Unknown format - try both
    print("[TPL Loader] Unknown format, trying all parsers...")
    
    parser = SERParser()
    if parser.load(filepath):
        return parser.root, '1SER'
    
    parser = USFParser()
    if parser.load(filepath):
        return parser.root, 'USF'
    
    return None, 'UNKNOWN'
