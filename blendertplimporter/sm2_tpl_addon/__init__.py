"""
Space Marine 2 TPL Importer for Blender
Based on LibSaber decompilation by Wildenhaus

Supports:
- Mesh geometry with proper UV mapping
- Compressed vertex/normal decompression
- Multiple UV channels
- Vertex colors
- Armature/skeleton import
- Material references
"""

bl_info = {
    "name": "Space Marine 2 TPL Importer",
    "author": "Based on LibSaber by Wildenhaus",
    "version": (1, 0, 0),
    "blender": (3, 0, 0),
    "location": "File > Import > Space Marine 2 TPL",
    "description": "Import Space Marine 2 TPL model files",
    "warning": "",
    "doc_url": "",
    "category": "Import-Export",
}

import bpy
from bpy.props import StringProperty, BoolProperty, FloatProperty, EnumProperty
from bpy_extras.io_utils import ImportHelper
from bpy.types import Operator, Panel

import os


class ImportSM2TPL(Operator, ImportHelper):
    """Import Space Marine 2 TPL file"""
    bl_idname = "import_scene.sm2_tpl"
    bl_label = "Import SM2 TPL"
    bl_options = {'REGISTER', 'UNDO', 'PRESET'}

    # File filter
    filename_ext = ".tpl"
    filter_glob: StringProperty(
        default="*.tpl",
        options={'HIDDEN'},
        maxlen=255,
    )

    # Import options
    import_armature: BoolProperty(
        name="Import Armature",
        description="Import skeleton/bones",
        default=True,
    )
    
    import_materials: BoolProperty(
        name="Import Materials",
        description="Create materials with texture references",
        default=True,
    )
    
    flip_uvs: BoolProperty(
        name="Flip UV V-Coordinate",
        description="Flip V coordinate for correct texture mapping (recommended)",
        default=True,
    )
    
    scale_factor: FloatProperty(
        name="Scale",
        description="Scale factor for imported model",
        default=1.0,
        min=0.001,
        max=1000.0,
    )
    
    import_mode: EnumProperty(
        name="Import Mode",
        description="How to import the TPL file",
        items=[
            ('USF', "USF Format", "Import as USF/TPL format (for .tpl files)"),
            ('RAW', "Raw Geometry", "Import raw geometry data (for _data files)"),
        ],
        default='USF',
    )

    def execute(self, context):
        from . import tpl_importer
        
        keywords = {
            'import_armature': self.import_armature,
            'import_materials': self.import_materials,
            'flip_uvs': self.flip_uvs,
            'scale_factor': self.scale_factor,
            'import_mode': self.import_mode,
        }
        
        result = tpl_importer.load(context, self.filepath, **keywords)
        
        if 'FINISHED' in result:
            self.report({'INFO'}, f"Imported: {os.path.basename(self.filepath)}")
        else:
            self.report({'ERROR'}, "Import failed - check console for details")
        
        return result

    def draw(self, context):
        layout = self.layout
        
        box = layout.box()
        box.label(text="Import Options:", icon='IMPORT')
        box.prop(self, "import_mode")
        box.prop(self, "scale_factor")
        
        box = layout.box()
        box.label(text="Geometry:", icon='MESH_DATA')
        box.prop(self, "flip_uvs")
        box.prop(self, "import_materials")
        
        box = layout.box()
        box.label(text="Skeleton:", icon='ARMATURE_DATA')
        box.prop(self, "import_armature")


class SM2_PT_ImportPanel(Panel):
    """Panel in the 3D View sidebar"""
    bl_label = "SM2 TPL Importer"
    bl_idname = "SM2_PT_import_panel"
    bl_space_type = 'VIEW_3D'
    bl_region_type = 'UI'
    bl_category = 'SM2'

    def draw(self, context):
        layout = self.layout
        
        layout.label(text="Space Marine 2", icon='GHOST_ENABLED')
        layout.operator("import_scene.sm2_tpl", text="Import TPL", icon='IMPORT')
        
        layout.separator()
        layout.label(text="Tips:", icon='INFO')
        col = layout.column(align=True)
        col.scale_y = 0.8
        col.label(text="• Enable 'Flip UV' for correct textures")
        col.label(text="• Check console for import details")


def menu_func_import(self, context):
    self.layout.operator(ImportSM2TPL.bl_idname, text="Space Marine 2 TPL (.tpl)")


classes = (
    ImportSM2TPL,
    SM2_PT_ImportPanel,
)


def register():
    for cls in classes:
        bpy.utils.register_class(cls)
    bpy.types.TOPBAR_MT_file_import.append(menu_func_import)
    print("SM2 TPL Importer registered")


def unregister():
    bpy.types.TOPBAR_MT_file_import.remove(menu_func_import)
    for cls in reversed(classes):
        bpy.utils.unregister_class(cls)
    print("SM2 TPL Importer unregistered")


if __name__ == "__main__":
    register()
