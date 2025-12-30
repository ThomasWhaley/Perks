using SaberTools.Common;
using SaberTools.USF;
using SharpGLTF.Animations;
using SharpGLTF.Geometry;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using COLOR_UV = SharpGLTF.Geometry.VertexTypes.VertexColor2Texture4;
using MESH_BUILDER = SharpGLTF.Geometry.MeshBuilder<SharpGLTF.Geometry.VertexTypes.VertexPositionNormalTangent, SharpGLTF.Geometry.VertexTypes.VertexColor2Texture4, SharpGLTF.Geometry.VertexTypes.VertexEmpty>;
using MESH_BUILDER_SKIN = SharpGLTF.Geometry.MeshBuilder<SharpGLTF.Geometry.VertexTypes.VertexPositionNormalTangent, SharpGLTF.Geometry.VertexTypes.VertexColor2Texture4, SharpGLTF.Geometry.VertexTypes.VertexJoints8>;
using VERTEX_BUILDER = SharpGLTF.Geometry.VertexBuilder<SharpGLTF.Geometry.VertexTypes.VertexPositionNormalTangent, SharpGLTF.Geometry.VertexTypes.VertexColor2Texture4, SharpGLTF.Geometry.VertexTypes.VertexEmpty>;
using VERTEX_BUILDER_SKIN = SharpGLTF.Geometry.VertexBuilder<SharpGLTF.Geometry.VertexTypes.VertexPositionNormalTangent, SharpGLTF.Geometry.VertexTypes.VertexColor2Texture4, SharpGLTF.Geometry.VertexTypes.VertexJoints8>;
using VERTEX_JOINTS = SharpGLTF.Geometry.VertexTypes.VertexJoints8;
using VERTEX_NORMAL = SharpGLTF.Geometry.VertexTypes.VertexPositionNormalTangent;

// This file is part of Model Converter for Space Marine 2
// Copyright (C) 2025 Neo_Kesha
// 
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// 
// See <https://www.gnu.org/licenses/> for more details.


namespace SaberTools.Convert
{
    public static class UsfConverter
    {
        private static KeyValuePair<UsfNode, NodeBuilder>[] nodes = new KeyValuePair<UsfNode, NodeBuilder>[0];
        private static HashSet<NodeBuilder> bones = new HashSet<NodeBuilder>();
        private static Dictionary<int, NodeBuilder> idToFakeNode = new Dictionary<int, NodeBuilder>();
        private static UsfSaberActor currentTplDesc = null;
        private static bool noSkin = false;
        public static SceneBuilder ConvertUsf(Usf usf, bool noSkin = false)
        {
            UsfConverter.noSkin = noSkin;
            var gltfScene = new SceneBuilder(usf.type);
            gltfScene.Extras = JsonValue.Create(new UsfExtra(usf));
            gltfScene.AddNode(PreconvertNode(usf.scene.root, gltfScene));
            ConvertNode(usf.scene.root, gltfScene);
            FillExtras(usf.scene.root, gltfScene);
            return gltfScene;
        }

        static VERTEX_BUILDER_SKIN generateVertex(Vector3 v, Vector3 n, Vector4 tang, Vector4[] c, Vector2[] t, (int, float)[] bindings)
        {
            return new VERTEX_BUILDER_SKIN(new VERTEX_NORMAL(v, n / n.Length(), tang), new COLOR_UV(c[0], c[1], t[0], t[1], t[2], t[3]), new VERTEX_JOINTS(bindings));
        }
        static VERTEX_BUILDER generateVertexRigid(Vector3 v, Vector3 n, Vector4 tang, Vector4[] c, Vector2[] t)
        {
            return new VERTEX_BUILDER(new VERTEX_NORMAL(v, n / n.Length(), tang), new COLOR_UV(c[0], c[1], t[0], t[1], t[2], t[3]));
        }

        public static NodeBuilder PreconvertNode(UsfNode usfNode, SceneBuilder gltfScene)
        {
            NodeBuilder gltfNode = new NodeBuilder(usfNode.name);
            if (nodes.Length <= usfNode.uid)
            {
                Array.Resize(ref nodes, usfNode.uid + 1);
            }
            var dupes = nodes.Select(n => n.Key).Where(n => n != null && n.name == usfNode.name).ToArray();
            if (dupes.Length > 0)
            {
                gltfNode.Name += "." + dupes.Length.ToString();
            }
            nodes[usfNode.uid] = new KeyValuePair<UsfNode, NodeBuilder>(usfNode, gltfNode);
            gltfNode.SetLocalTransform(new SharpGLTF.Transforms.AffineTransform(ConvertMatrix(usfNode.local)), true);
            foreach (var node in usfNode.children)
            {
                var child = PreconvertNode(node, gltfScene);
                if (child == null)
                {
                    continue;
                }
                if (UsfConverter.noSkin && child.Name == "skinned_geometry")
                {
                    continue;
                }
                gltfNode.AddNode(child);
            }

            gltfNode.Extras = JsonValue.Create(new UsfNodeExtra(usfNode));
            return gltfNode;
        }
        private static void ConvertNode(UsfNode usfNode, SceneBuilder gltfScene)
        {
            var gltfNode = nodes[usfNode.uid].Value;
            if (UsfConverter.noSkin && gltfNode.Name == "skinned_geometry")
            {
                return;
            }
            if (usfNode.actor != null)
            {
                currentTplDesc = usfNode.actor;
            }
            if (usfNode.cameraInfo != null)
            {
                UsfCameraInfo cameraInfo = usfNode.cameraInfo;
                var camera = new CameraBuilder.Perspective(cameraInfo.aspectHW, cameraInfo.fov, 0);
                var decomposed = gltfNode.LocalTransform.GetDecomposed();
                //gltfScene.AddCamera(camera, decomposed);
            }
            if (usfNode.mesh != null)
            {
                var isSkinned = usfNode.mesh.bones.Length > 0;
                IMeshBuilder<MaterialBuilder> mesh;
                if (isSkinned)
                {
                    mesh = new MESH_BUILDER_SKIN();
                }
                else
                {
                    mesh = new MESH_BUILDER();
                }

                var matIndexToMap = new Dictionary<int, List<int>>();
                var materials = new SharpGLTF.Materials.MaterialBuilder[usfNode.mesh.materials.Length];
                for (int i = 0; i < materials.Length; ++i)
                {
                    var material = new SharpGLTF.Materials.MaterialBuilder().WithDoubleSide(true);

                    var nameFiled = (ValueString)usfNode.mesh.materials[i].data["material"]["name"];
                    if (nameFiled != null) material.Name = nameFiled.Value;
                    materials[i] = material;
                    var layersField = usfNode.mesh.materials[i].data["layers"]["0"];
                    if (layersField != null)
                    {
                        material.WithBaseColor(new Vector4(1, 1, 1, 1));
                        var channelBaseColor = material.GetChannel(KnownChannel.BaseColor);
                        var textureBaseColor = channelBaseColor.UseTexture();
                        textureBaseColor.Name = ((ValueString)layersField["textureName"]).Value;

                    }

                    var colorChannelMap = new List<int>();
                    var colorSetsField = (ValueArray)usfNode.mesh.materials[i].data["colorSets"];
                    if (colorSetsField != null)
                    {
                        foreach (var set in colorSetsField.Value)
                        {
                            var channel = (ValueInt)set["colorChannelIdx"];
                            if (channel.Value != -1)
                                colorChannelMap.Add(channel.Value);
                        }
                    }
                    else
                    {
                        colorChannelMap.Add(-1);
                        colorChannelMap.Add(-1);
                        colorChannelMap.Add(-1);
                        colorChannelMap.Add(-1);
                    }

                    if (usfNode.mesh.materials[i].data["extraVertexColorData"] != null)
                    {
                        if (usfNode.mesh.materials[i].data["extraVertexColorData"]["colorR"] != null)
                        {
                            var idx = ((ValueInt)usfNode.mesh.materials[i].data["extraVertexColorData"]["colorR"]["colorSetIdx"]).Value;
                            colorChannelMap.Add(idx);
                        }
                        else
                        {
                            colorChannelMap.Add(-1);
                        }
                        if (usfNode.mesh.materials[i].data["extraVertexColorData"]["colorG"] != null)
                        {
                            var idx = ((ValueInt)usfNode.mesh.materials[i].data["extraVertexColorData"]["colorG"]["colorSetIdx"]).Value;
                            colorChannelMap.Add(idx);
                        }
                        else
                        {
                            colorChannelMap.Add(-1);
                        }
                        if (usfNode.mesh.materials[i].data["extraVertexColorData"]["colorB"] != null)
                        {
                            var idx = ((ValueInt)usfNode.mesh.materials[i].data["extraVertexColorData"]["colorB"]["colorSetIdx"]).Value;
                            colorChannelMap.Add(idx);
                        }
                        else
                        {
                            colorChannelMap.Add(-1);
                        }
                        if (usfNode.mesh.materials[i].data["extraVertexColorData"]["colorA"] != null)
                        {
                            var idx = ((ValueInt)usfNode.mesh.materials[i].data["extraVertexColorData"]["colorA"]["colorSetIdx"]).Value;
                            colorChannelMap.Add(idx);
                        }
                        else
                        {
                            colorChannelMap.Add(-1);
                        }
                    }
                    else
                    {
                        colorChannelMap.Add(-1);
                        colorChannelMap.Add(-1);
                        colorChannelMap.Add(-1);
                        colorChannelMap.Add(-1);
                    }
                    //failsafe
                    while (colorChannelMap.Count < 8) colorChannelMap.Add(-1);

                    matIndexToMap.Add(i, colorChannelMap);
                }



                mesh.Extras = JsonValue.Create(new UsfMeshExtra(usfNode.mesh.materials, usfNode.mesh.skinningMethod));
                mesh.Name = usfNode.name;

                for (var i = 0; i < usfNode.mesh.vertixes.Length / 3; ++i)
                {
                    var colors1 = new Vector4[2] { new Vector4(1, 1, 1, 1), new Vector4(1, 1, 1, 1) };
                    var colors2 = new Vector4[2] { new Vector4(1, 1, 1, 1), new Vector4(1, 1, 1, 1) };
                    var colors3 = new Vector4[2] { new Vector4(1, 1, 1, 1), new Vector4(1, 1, 1, 1) };
                    var uvSets1 = new Vector2[4] { new Vector2(0, 0), new Vector2(0, 0), new Vector2(0, 0), new Vector2(0, 0) };
                    var uvSets2 = new Vector2[4] { new Vector2(0, 0), new Vector2(0, 0), new Vector2(0, 0), new Vector2(0, 0) };
                    var uvSets3 = new Vector2[4] { new Vector2(0, 0), new Vector2(0, 0), new Vector2(0, 0), new Vector2(0, 0) };
                    var v1 = ConvertVector(usfNode.mesh.vertixes[0 + i * 3]);
                    var v2 = ConvertVector(usfNode.mesh.vertixes[1 + i * 3]);
                    var v3 = ConvertVector(usfNode.mesh.vertixes[2 + i * 3]);
                    var n1 = ConvertVector(usfNode.mesh.normals[0 + i * 3]);
                    var n2 = ConvertVector(usfNode.mesh.normals[1 + i * 3]);
                    var n3 = ConvertVector(usfNode.mesh.normals[2 + i * 3]);
                    var tang1 = ConvertVector(usfNode.mesh.tangents[0][0 + i * 3]);
                    var tang2 = ConvertVector(usfNode.mesh.tangents[0][1 + i * 3]);
                    var tang3 = ConvertVector(usfNode.mesh.tangents[0][2 + i * 3]);
                    var primitive = mesh.UsePrimitive(materials[usfNode.mesh.faces[i].materialIndex]);
                    for (int j = 0; j < usfNode.mesh.uvSets.Length; j++)
                    {
                        if (j >= uvSets1.Length)
                        {
                            break;
                        }
                        uvSets1[j] = ConvertVector(usfNode.mesh.uvSets[j].uvs[0 + i * 3]);
                        uvSets2[j] = ConvertVector(usfNode.mesh.uvSets[j].uvs[1 + i * 3]);
                        uvSets3[j] = ConvertVector(usfNode.mesh.uvSets[j].uvs[2 + i * 3]);
                    }

                    var colorsSrc1 = new Vector4[usfNode.mesh.colors.Length];
                    var colorsSrc2 = new Vector4[usfNode.mesh.colors.Length];
                    var colorsSrc3 = new Vector4[usfNode.mesh.colors.Length];
                    for (var k = 0; k < colorsSrc1.Length; ++k)
                    {
                        colorsSrc1[k] = ConvertColor(usfNode.mesh.colors[k][0 + i * 3]);
                        colorsSrc2[k] = ConvertColor(usfNode.mesh.colors[k][1 + i * 3]);
                        colorsSrc3[k] = ConvertColor(usfNode.mesh.colors[k][2 + i * 3]);
                    }

                    var colorChannelMap = matIndexToMap[(int)usfNode.mesh.faces[i].materialIndex];

                    colors1 = BuildColor(colorsSrc1, colorChannelMap);
                    colors2 = BuildColor(colorsSrc2, colorChannelMap);
                    colors3 = BuildColor(colorsSrc1, colorChannelMap);
                    if (isSkinned)
                    {
                        var w1 = ConvertSkin(usfNode.mesh.skins[0 + i * 3]);
                        var w2 = ConvertSkin(usfNode.mesh.skins[1 + i * 3]);
                        var w3 = ConvertSkin(usfNode.mesh.skins[2 + i * 3]);
                        primitive.AddTriangle(generateVertex(v1, n1, tang1, colors1, uvSets1, w1), generateVertex(v2, n2, tang2, colors2, uvSets2, w2), generateVertex(v3, n3, tang3, colors3, uvSets3, w3));
                    }
                    else
                    {
                        primitive.AddTriangle(generateVertexRigid(v1, n1, tang1, colors1, uvSets1), generateVertexRigid(v2, n2, tang2, colors2, uvSets2), generateVertexRigid(v3, n3, tang3, colors3, uvSets3));
                    }

                }
                if (isSkinned)
                {
                    (NodeBuilder, Matrix4x4)[] joints = new (NodeBuilder, Matrix4x4)[usfNode.mesh.bones.Length];
                    for (var i = 0; i < usfNode.mesh.bones.Length; ++i)
                    {
                        var bone = usfNode.mesh.bones[i];
                        Matrix4x4 matrix;
                        NodeBuilder jointNode = nodes[bone.usfNodeUid].Value;
                        (NodeBuilder, Matrix4x4) joint = (jointNode, ConvertMatrix(bone.bindMatrix));
                        joints[i] = joint;

                    }
                    gltfScene.AddSkinnedMesh(mesh, joints);
                }
                else
                {
                    gltfScene.AddRigidMesh(mesh, gltfNode);
                }
            }
            if (usfNode.animation != null)
            {
                foreach (var animDesc in currentTplDesc.animations)
                {
                    var anim = usfNode.animation;
                    var translationTrack = anim.splTranslate;
                    var rotationTrack = anim.splRotation;
                    var scaleTrack = anim.splScale;

                    var translationSampler = CurveSampler.CreateSampler(GetKeyframes(translationTrack, animDesc, anim.initialTranslate.ToVector3()), true);
                    var rotationSampler = CurveSampler.CreateSampler(GetKeyframesRot(rotationTrack, animDesc, anim.initialRotation.ToQuaternion()), true);
                    var scaleSampler = CurveSampler.CreateSampler(GetKeyframes(scaleTrack, animDesc, anim.initialScale.ToVector3()), true);

                    gltfNode.SetTranslationTrack(animDesc.name, translationSampler);
                    gltfNode.SetRotationTrack(animDesc.name, rotationSampler);
                    gltfNode.SetScaleTrack(animDesc.name, scaleSampler);

                }
            }
            for (var i = 0; i < usfNode.children.Count; ++i)
            {
                ConvertNode(usfNode.children[i], gltfScene);
            }

        }

        private static List<(float, Vector3)> GetKeyframes(UsfSpl saberSpline, Anim animDesc, Vector3 def)
        {
            var spl = saberSpline.ConvertData(saberSpline.Get());
            List<(float, Vector3)> keyframes = new List<(float, Vector3)>();
            var begin = animDesc.begin;
            var end = animDesc.end;
            var fps = (end - begin) / animDesc.time;
            for (var i = begin; i <= end; i++)
            {
                var t = i / fps - begin / fps;
                keyframes.Add((t, (i < spl.Length) ? spl[i] : def));
            }

            return keyframes;
        }

        private static List<(float, Quaternion)> GetKeyframesRot(UsfSpl saberSpline, Anim animDesc, Quaternion def)
        {
            var spl = saberSpline.ConvertDataRot(saberSpline.Get());
            List<(float, Quaternion)> keyframes = new List<(float, Quaternion)>();
            var begin = animDesc.begin;
            var end = animDesc.end;
            var fps = (end - begin) / animDesc.time;
            for (var i = begin; i <= end; i++)
            {
                var t = i / fps - begin / fps;
                keyframes.Add((t, (i < spl.Length) ? spl[i] : def));

            }

            return keyframes;
        }

        private static Vector4[] BuildColor(Vector4[] colors, List<int> channelMap)
        {
            var src = colors.SelectMany(c => new float[] { c.X, c.Y, c.Z, c.W }).ToList();
            Vector4[] result = { new Vector4(1, 1, 1, 1), new Vector4(1, 1, 1, 1) };

            result[0].X = (channelMap[0] != -1) ? src[channelMap[0]] : 0.0f;
            result[0].Y = (channelMap[1] != -1) ? src[channelMap[1]] : 0.0f;
            result[0].Z = (channelMap[2] != -1) ? src[channelMap[2]] : 0.0f;
            result[0].W = (channelMap[3] != -1) ? src[channelMap[3]] : 1.0f;
            result[1].X = (channelMap[4] != -1) ? src[channelMap[4]] : 0.0f;
            result[1].Y = (channelMap[5] != -1) ? src[channelMap[5]] : 0.0f;
            result[1].Z = (channelMap[6] != -1) ? src[channelMap[6]] : 0.0f;
            result[1].W = (channelMap[7] != -1) ? src[channelMap[7]] : 1.0f;
            return result;
        }
        private static void FillExtras(UsfNode usfNode, SceneBuilder gltfScene)
        {
            //JsonValue affixes = JsonValue.Create(usfNode.affixes);
            //JsonValue pses = JsonValue.Create(usfNode.pses.psstr);
        }
        private static (int, float)[] ConvertSkin(UsfSkinVtx skin)
        {
            var w = new (int, float)[skin.weight.Length - 1];
            double sum = 0.0;
            for (var i = 0; i < skin.weight.Length - 1; ++i)
            {
                w[i] = (skin.boneIdx[i], skin.weight[i]);
                sum += skin.weight[i];
            }
            for (var i = 0; i < skin.weight.Length - 1; ++i)
            {
                w[i].Item2 = (float)(w[i].Item2 / sum);
            }
            return w;
        }
        private static Vector4 ConvertColor(UInt32 c)
        {
            var r = (c & (0xFF << 0)) >> 0;
            var g = (c & (0xFF << 8)) >> 8;
            var b = (c & (0xFF << 16)) >> 16;
            var a = (c & (0xFF << 24)) >> 24;
            return new Vector4(r / 255.0f, g / 255.0f, b / 255.0f, a / 255.0f);
        }
        private static Matrix4x4 ConvertMatrix(UsfMatrix4 m)
        {
            return new Matrix4x4(m.rows[0].x, m.rows[0].y, m.rows[0].z, m.rows[0].w, m.rows[1].x, m.rows[1].y, m.rows[1].z, m.rows[1].w, m.rows[2].x, m.rows[2].y, m.rows[2].z, m.rows[2].w, m.rows[3].x, m.rows[3].y, m.rows[3].z, m.rows[3].w);
        }
        private static Vector2 ConvertVector(UsfVector2 v2)
        {
            return new Vector2(v2.x, v2.y);
        }
        private static Vector3 ConvertVector(UsfVector3 v3)
        {
            return new Vector3(v3.x, v3.y, v3.z);
        }
        private static Vector4 ConvertVector(UsfVector4 v4)
        {
            return new Vector4(v4.x, v4.y, v4.z, v4.w);
        }
    }

    public class UsfMeshExtra
    {
        public UsfMeshExtra() { }
        public UsfMeshExtra(UsfMaterial[] materials, SkinnginMethod skinngMethod)
        {
            this.materials = materials;
            this.skinngMethod = skinngMethod;
        }

        public UsfMaterial[] materials { get; set; }
        public SkinnginMethod skinngMethod { get; set; }
    }

    public class UsfMaterialExtra
    {
        public string texture { get; set; }
        public string submaterial { get; set; }
    }

    public class UsfNodeExtra
    {
        public UsfNodeExtra() { }
        public UsfNodeExtra(UsfNode node)
        {
            this.pses = node.pses;
            this.affixes = node.affixes;
            this.actor = node.actor;
            this.mayaNodeId = node.mayaNodeId;
            this.actions = new Dictionary<string, Dictionary<int, CommentFramePair>>();
            if (this.actor != null && this.actor.animations != null)
            {
                foreach (var anim in this.actor.animations)
                {
                    var actionList = new Dictionary<int, CommentFramePair>();
                    foreach (var action in anim.actionFrames)
                    {
                        var data = new CommentFramePair();
                        data.comment = action.comment;
                        data.frame = action.frame - anim.begin;
                        actionList.Add(action.id, data);
                    }
                    this.actions.Add(anim.name, actionList);
                }
            }

        }

        public UsfPses pses { get; set; }
        public string affixes { get; set; }
        public string region { get; set; }
        public string mayaNodeId { get; set; }
        public UsfSaberActor actor { get; set; }
        public Dictionary<string, Dictionary<int, CommentFramePair>> actions { get; set; }
    }
    public class CommentFramePair
    {
        public string comment { get; set; }
        public int frame { get; set; }
    }

    public class UsfExtra
    {
        public UsfExtra() { }
        public UsfExtra(Usf usf)
        {
            name = usf.sourcePath;
            type = usf.type;
            ps = usf.options.str;
        }
        public string name { get; set; } = "";
        public string type { get; set; } = "tpl";
        public string ps { get; set; } = "";
    }

}
