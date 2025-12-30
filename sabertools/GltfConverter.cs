using Newtonsoft.Json;
using SaberTools;
using SaberTools.Common;
using SaberTools.Convert;
using SaberTools.USF;
using SharpGLTF.Memory;
using SharpGLTF.Schema2;
using System.Numerics;
using System.Text.Json.Nodes;
using static Program;

// This file is part of Model Converter for Space Marine 2
// Copyright (C) 2025 Neo_Kesha
// 
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// 
// See <https://www.gnu.org/licenses/> for more details.

namespace ModelConverter.Convert
{

    public static class GltfConverter
    {
        public static Usf ConvertGltf(Scene gltfScene, ConvertParams settings)
        {
            using (var configStream = new FileStream("config.json", FileMode.Open, FileAccess.Read))
            {
                var reader = new StreamReader(configStream);
                ConvertConfig.currentConfig = JsonConvert.DeserializeObject<ConvertConfig>(reader.ReadToEnd());
            }
            if (ConvertConfig.currentConfig == null)
            {
                throw new Exception("No config.json found");
            }
            ConvertConfig.currentConfig.isConstructor = gltfScene.Name.Contains(ConvertConfig.currentConfig.regionRules.modelNameContains);
            usfNodes.Clear();
            bones.Clear();
            boneToUsf.Clear();
            uidToGltfNode.Clear();
            indexToUsfUid.Clear();

            settings.isCharacter = gltfScene.VisualChildren.Where(n => n.Name == "ROOT").FirstOrDefault() != null;

            return Convert(gltfScene, settings);
        }
        public static Usf Convert(Scene gltfScene, ConvertParams settings)
        {
            var armatureSourceNode = gltfScene.VisualChildren.Where(n => n.Name == "ROOT").FirstOrDefault();
            var skinsSourceNode = gltfScene.VisualChildren.Where(n => n.Name == "skinned_geometry").FirstOrDefault();
            var hierarchySourceNode = gltfScene.VisualChildren.Where(n => n.Name == "hierarchy").FirstOrDefault();

            nodes.Clear();
            skinGroups.Clear();
            uidToNode.Clear();
            indexToNode.Clear();
            animationLength.Clear();
            FixNames(gltfScene.VisualChildren);
            foreach (var gltfNode in gltfScene.VisualChildren)
            {
                PrecreateNode(gltfNode, true);
            }
            foreach (var node in nodes)
            {
                EvaluateType(node);
            }
            foreach (var node in nodes)
            {
                CollectNodes(node);
            }
            foreach (var node in nodes)
            {
                EvaluateMetadata(node);
            }
            foreach (var node in nodes)
            {
                ExtractMaterials(node);
            }
            foreach (var node in nodes)
            {
                ProcessAnimations(node);
            }

            var usf = new Usf();
            NodeWrapper nodeRoot = nodes.Where(n => n.type == NodeType.ROOT).FirstOrDefault();
            NodeWrapper nodeCharRoot = nodes.Where(n => n.type == NodeType.CHAR_ROOT).FirstOrDefault();
            NodeWrapper nodeHierarchy = nodes.Where(n => n.gltfNode.Name == "hierarchy").FirstOrDefault();
            NodeWrapper nodeDx = nodes.Where(n => n.type == NodeType.DX).FirstOrDefault();
            NodeWrapper nodeSkinnedGeom = null;

            var characterName = gltfScene.Name;
            NodeWrapper nodeActor = null;
            if (settings.isCharacter)
            {
                nodeActor = nodes.Where(n => n.type == NodeType.CHAR).FirstOrDefault();
                if (nodeActor == null)
                {
                    var characterNode = UsfNode.BuildCharNode(characterName);
                    nodeActor = new NodeWrapper();
                    nodeActor.usfNode = characterNode;
                    nodeActor.type = NodeType.CHAR;
                    nodeActor.affixes = characterNode.affixes;
                    nodeActor.usfUid = characterNode.uid;

                    LogInfo("characterBuilderExportTplDesc node not found, building default node");
                }
            }
            else
            {
                nodeActor = nodes.Where(n => n.type == NodeType.ACTOR).FirstOrDefault();
                if (nodeActor == null)
                {
                    foreach (var child in gltfScene.VisualChildren)
                    {
                        var childNode = indexToNode[child.LogicalIndex];
                        if (childNode.type == NodeType.LOCATOR && (childNode.gltfNode.Name.Equals("tpl_desc") || nodeActor == null))
                        {
                            nodeActor = childNode;
                        }
                    }
                }

                if (nodeActor != null)
                {
                    nodeActor.type = NodeType.ACTOR;
                    LogInfo(String.Format("Using {0} node as an Actor node", nodeActor.usfNode.name));
                }
            }

            UsfNodeExtra extra = null;
            if (nodeActor.gltfNode != null && nodeActor.gltfNode.Extras != null)
            {
                extra = JsonConvert.DeserializeObject<UsfNodeExtra>(nodeActor.gltfNode.Extras.ToJsonString());
                nodeActor.usfNode.actor = extra.actor;
            }
            else
            {
                nodeActor.usfNode.actor = new UsfSaberActor();
            }

            if (settings.isCharacter)
            {
                nodeActor.usfNode.actor.tplName = "characters\\" + characterName + ".tpl";
                nodeActor.usfNode.affixes = "export_per_object_geom_streaming\n\n\n";
                nodeActor.usfNode.pses.ps["tpl_name"] = new ValueString("characters\\" + characterName + ".tpl");
            } else
            {
                nodeActor.usfNode.actor.tplName = "weapons\\" + characterName + ".tpl";
                nodeActor.usfNode.affixes = "\n\n\n";
                nodeActor.usfNode.pses.ps["tpl_name"] = new ValueString("weapons\\" + characterName + ".tpl");
            }
            nodeActor.usfNode.sourceId = "|" + characterName;
            nodeActor.usfNode.name = characterName;
            nodeActor.usfNode.mayaNodeId = "saberActor";
            nodeActor.usfNode.actor = new UsfSaberActor();
            nodeActor.usfNode.pses.ps = new Ps();
            nodeActor.usfNode.pses.ps["tpl_obj"] = new ValueString("");
            nodeActor.usfNode.pses.ps["__type"] = new ValueString("iactor");
            nodeActor.usfNode.pses.ps["ExportOptions"] = new ValueProperty();
            nodeActor.usfNode.pses.ps["ExportOptions"]["generateCollisionData"] = new ValueBool(true);
            nodeActor.usfNode.pses.ps["ExportOptions"]["buildSkinCompound"] = new ValueBool(true);
            AddAnimations(nodeActor, extra);

            if (nodeRoot == null)
            {
                var rootNode = UsfNode.BuildRootNode();
                nodeRoot = new NodeWrapper();
                nodeRoot.usfNode = rootNode;
                nodeRoot.type = NodeType.ROOT;
                nodeRoot.affixes = rootNode.affixes;
                nodeRoot.usfUid = rootNode.uid;

                LogInfo(".root. node not found, building default node");
            }

            if (settings.isCharacter)
            {
                if (nodeSkinnedGeom == null)
                {
                    var skinNode = UsfNode.BuildSkinnedGeometryNode();
                    nodeSkinnedGeom = new NodeWrapper();
                    nodeSkinnedGeom.usfNode = skinNode;
                    nodeSkinnedGeom.type = NodeType.SKINNED_GEOM;
                    nodeSkinnedGeom.affixes = skinNode.affixes;
                    nodeSkinnedGeom.usfUid = skinNode.uid;
                }
                nodeRoot.usfNode.AddChild(nodeSkinnedGeom.usfNode);

                if (nodeDx == null)
                {
                    var dxNode = UsfNode.BuildDxNode();
                    nodeDx = new NodeWrapper();
                    nodeDx.usfNode = dxNode;
                    nodeDx.type = NodeType.DX;
                    nodeDx.affixes = dxNode.affixes;
                    nodeDx.usfUid = dxNode.uid;

                    LogInfo("DX node not found, building default node");
                }
                nodeRoot.usfNode.AddChild(nodeDx.usfNode);

                nodeActor.usfNode.sourceId = "|characterBuilderExportTplDesc";
                nodeRoot.usfNode.AddChild(nodeActor.usfNode);

                if (nodeCharRoot == null)
                {
                    var characterRootNode = UsfNode.BuildCharacterROOTNode();
                    nodeCharRoot = new NodeWrapper();
                    nodeCharRoot.usfNode = characterRootNode;
                    nodeCharRoot.type = NodeType.CHAR_ROOT;
                    nodeCharRoot.affixes = characterRootNode.affixes;
                    nodeCharRoot.usfUid = characterRootNode.uid;

                    LogInfo("Character ROOT node not found, building default node");
                }
                nodeRoot.usfNode.AddChild(nodeCharRoot.usfNode);
            } else
            {
                nodeRoot.usfNode.AddChild(nodeActor.usfNode);
            }


            usf.Initialize(characterName, true);
            usf.scene.root = nodeRoot.usfNode;

            foreach (var node in nodes)
            {
                ConvertMesh(node, settings);
            }

            if (settings.isCharacter)
            {
                AttachNodes(nodeCharRoot);
                MergeNodes(nodeCharRoot, nodeHierarchy);
                BuildSkinnedGeometryExperimental(nodeSkinnedGeom.usfNode, skinsSourceNode, settings);
                FixSkinNamespaces(nodeSkinnedGeom.usfNode);
                GenerateNamespaces(nodeCharRoot.usfNode);
            } else
            {
                AttachNodes(nodeActor);
                GenerateNamespaces(nodeActor.usfNode);
            }

            if (settings.isCharacter)
            {
                var rootId = nodeCharRoot.usfUid;
                var skinId = nodeSkinnedGeom.usfUid;
                nodeActor.usfNode.actor.sourceUsfUids = new int[] { rootId, skinId };
            }

            return usf;
        }
        private static List<NodeWrapper> nodes = new List<NodeWrapper>();
        private static Dictionary<string, HashSet<NodeWrapper>> skinGroups = new Dictionary<string, HashSet<NodeWrapper>>();
        private static Dictionary<int, NodeWrapper> uidToNode = new Dictionary<int, NodeWrapper>();
        private static Dictionary<int, NodeWrapper> indexToNode = new Dictionary<int, NodeWrapper>();
        private static Dictionary<string, int> animationLength = new Dictionary<string, int>();
        
        public static void AddAnimations(NodeWrapper nodeChar, UsfNodeExtra extra)
        {
            if (animationLength.Count > 0)
            {
                var idx = 0;
                var animCnt = 0;
                nodeChar.usfNode.actor.animations = new Anim[animationLength.Count];
                foreach (var animName in animationLength.Keys)
                {
                    Anim animDesc = new Anim();
                    var length = animationLength[animName];
                    animDesc.name = animName;
                    animDesc.begin = idx;
                    animDesc.end = idx + length - 1;
                    animDesc.time = length / 30.0f;
                    idx = ((animDesc.end / 10) + 1) * 10;
                    var actionList = new List<ActionFrame>();
                    if (extra != null && extra.actions != null && extra.actions.ContainsKey(animName))
                    {
                        foreach (var actionId in extra.actions[animName].Keys)
                        {
                            var actionFrameData = extra.actions[animName][actionId];
                            var actionFrame = new ActionFrame();
                            actionFrame.id = actionId;
                            actionFrame.comment = actionFrameData.comment;
                            actionFrame.frame = actionFrameData.frame + animDesc.begin;
                            actionList.Add(actionFrame);
                        }
                    }
                    animDesc.actionFrames = actionList.ToArray();
                    nodeChar.usfNode.actor.animations[animCnt] = animDesc;
                    ++animCnt;
                }
            }
        }
        public static void PrecreateNode(Node gltfNode, bool includeSkinnedGeom = false)
        {
            if (gltfNode.Name == "skinned_geometry" && !includeSkinnedGeom)
            {
                return;
            }

            var node = new NodeWrapper();
            node.gltfNode = gltfNode;
            node.gltfIndex = gltfNode.LogicalIndex;
            node.usfNode = PreconvertNode(gltfNode);
            node.usfNode.uid = (int)UsfNodeCommonUID.Top + nodes.Count;
            node.usfUid = node.usfNode.uid;
            indexToNode.Add(node.gltfIndex, node);
            uidToNode.Add(node.usfUid, node);
            node.usfUid = node.usfNode.uid;
            if (gltfNode.Extras != null)
            {
                var extras = JsonConvert.DeserializeObject<UsfNodeExtra>(gltfNode.Extras.ToJsonString());
                if (extras.affixes != null) node.storedAffixes = extras.affixes;
                if (extras.pses != null) node.usfNode.pses = extras.pses;
                if (node.storedAffixes != null) LogInfo(String.Format("Node {0} extracted stored affixes from Extras", node.usfNode.name));
            }

            var groupName = node.usfNode.name.ToLower().Replace("_l_", "_L_").Replace("_r_", "_R_");
            if (groupName.Contains("_rend"))
            {
                var orig = groupName;
                groupName = groupName.Split("_rend")[0];
                //HACK
                if (orig.Contains("_rend1"))
                {
                    groupName += "1";
                }
                if (orig.Contains("_rend2"))
                {
                    groupName += "2";
                }
                if (orig.Contains("_rend3"))
                {
                    groupName += "3";
                }
                if (orig.Contains("_rend4"))
                {
                    groupName += "4";
                }
            }
            if (groupName.Contains("_sim"))
            {
                groupName = groupName.Split("_sim")[0];
            }
            if (groupName.Contains("_lod"))
            {
                groupName = groupName.Split("_lod")[0];
            }
            if (groupName.Contains("_cdt"))
            {
                groupName = groupName.Split("_cdt")[0];
            }
            node.groupName = groupName;
            nodes.Add(node);
            foreach (var child in gltfNode.VisualChildren)
            {
                PrecreateNode(child);
            }
        }
        public static UsfNode PreconvertNode(Node gltfNode)
        {
            var usfNode = new UsfNode();
            usfNode.pses = new UsfPses(true);
            var name = gltfNode.Name;
            if (name == null && gltfNode.Mesh != null)
            {
                name = gltfNode.Mesh.Name;
                gltfNode.Name = name;
            }
            else if (gltfNode.Mesh != null)
            {
                gltfNode.Mesh.Name = name;
            }
            usfNode.name = gltfNode.Name;
            usfNode.mayaNodeId = "";
            usfNode.local = ConvertMatrix(gltfNode.LocalMatrix);
            usfNode.localOriginal = ConvertMatrix(gltfNode.LocalMatrix);
            usfNode.world = ConvertMatrix(gltfNode.WorldMatrix);

            if (gltfNode.IsTransformAnimated)
            {
                var root = gltfNode.LogicalParent;
                var anims = root.LogicalAnimations;
                foreach (var anim in anims)
                {
                    var keysTranslation = 0;
                    var translationChannel = anim.FindTranslationChannel(gltfNode);
                    if (translationChannel != null)
                    {
                        var translationSampler = translationChannel.GetTranslationSampler();
                        keysTranslation = translationSampler.GetLinearKeys().Count();
                    }

                    var keysScale = 0;
                    var sizeChannel = anim.FindScaleChannel(gltfNode);
                    if (sizeChannel != null)
                    {
                        var sizeSampler = sizeChannel.GetScaleSampler();
                        keysScale = sizeSampler.GetLinearKeys().Count();
                    }

                    var keysRotation = 0;
                    var rotationChannel = anim.FindRotationChannel(gltfNode);
                    if (rotationChannel != null)
                    {
                        var rotationSampler = rotationChannel.GetRotationSampler();
                        keysRotation = rotationSampler.GetLinearKeys().Count();
                    }
                    
                    var keys = Math.Max(keysTranslation, keysScale);
                    keys = Math.Max(keys, keysRotation);
                    var animName = anim.Name;
                    if (animationLength.ContainsKey(animName))
                    {
                        animationLength[animName] = Math.Max(animationLength[animName], keys);
                    }
                    else
                    {
                        animationLength[animName] = keys;
                    }
                }
            }

            return usfNode;
        }
        public static void EvaluateType(NodeWrapper node)
        {
            var nodeName = node.usfNode.name.ToLower();
            node.type = NodeType.LOCATOR;
            if (node.gltfNode.Extras != null)
            {
                var actorExtra = JsonConvert.DeserializeObject<UsfNodeExtra>(node.gltfNode.Extras.ToJsonString()).actor;
                if (actorExtra != null)
                {
                    node.usfNode.actor = actorExtra;
                    node.type = NodeType.ACTOR;
                }
            }
            if ((nodeName.Contains("_cdt") || nodeName.StartsWith("rb_")) && !nodeName.Contains("constraint"))
            {
                node.type = NodeType.CDT;
                node.usfNode.mayaNodeId = "mesh";
            }
            else if (nodeName.Contains("_sim"))
            {
                node.type = NodeType.CLOTH_SIM;
                node.usfNode.mayaNodeId = "mesh";
            }
            else if (nodeName.Contains("_lod"))
            {
                node.type = NodeType.LOD;
                node.usfNode.mayaNodeId = "mesh";
            }
            else if (nodeName.Contains("_decal"))
            {
                node.type = NodeType.DECAL;
                node.usfNode.mayaNodeId = "mesh";
            }
            else if (nodeName.Contains("_rend"))
            {
                node.type = NodeType.CLOTH_SKIN;
                node.usfNode.mayaNodeId = "mesh";
            }
            else if (node.gltfNode.IsSkinJoint)
            {
                node.type = NodeType.BONE;
            }
            else if (node.gltfNode.Skin == null && node.gltfNode.Mesh != null)
            {
                node.type = NodeType.RIGID_MESH;
            }
            else if (node.gltfNode.Skin != null)
            {
                node.type = NodeType.SKIN;
                node.usfNode.mayaNodeId = "mesh";
            }
            else if (nodeName == "s3dsaberdxtechnicalnode")
            {
                node.type = NodeType.DX;
                node.usfNode.mayaNodeId = "saberDxNode";
            }
            else if (nodeName == "characterbuilderexporttpldesc")
            {
                node.type = NodeType.CHAR;
                node.usfNode.mayaNodeId = "saberActor";
            }
            else if (nodeName == "root")
            {
                node.type = NodeType.CHAR_ROOT;
            }
            else if (nodeName == ".root.")
            {
                node.type = NodeType.ROOT;
            }

            LogInfo(String.Format("Node {0} Speculated type: {1}", nodeName, node.type.ToString()));
        }
        public static void CollectNodes(NodeWrapper node)
        {
            var sim = nodes.Where(n => n.type == NodeType.CLOTH_SIM && n.groupName == node.groupName).FirstOrDefault();
            var lods = nodes.Where(n => n.type == NodeType.LOD && n.groupName == node.groupName).ToList();
            var decal = nodes.Where(n => n.type == NodeType.DECAL && n.groupName == node.groupName).FirstOrDefault();
            var decal_lods = nodes.Where(n => n.type == NodeType.LOD && n.groupName == node.groupName && n.usfNode.name.Contains("decal")).ToList();
            var cloth = nodes.Where(n => n.type == NodeType.CLOTH_SKIN && n.groupName == node.groupName).FirstOrDefault();
            var skin = nodes.Where(n => n.type == NodeType.SKIN && n.groupName == node.groupName).FirstOrDefault();
            var rigid = nodes.Where(n => n.type == NodeType.RIGID_MESH && n.groupName == node.groupName).FirstOrDefault();

            node.cloth = cloth;
            node.skin = skin;
            node.sim = sim;
            node.lods = lods;
            node.decal = decal;
            node.rigid = rigid;
            if (node.type == NodeType.SKIN && node.sim != null)
            {
                node.type = NodeType.CLOTH_SKIN;
            }

            if (node.type != NodeType.CLOTH_SKIN && node.type != NodeType.SKIN && node.type != NodeType.LOD && node.type != NodeType.CLOTH_SIM && node.type != NodeType.DECAL)
            {
                return;
            }

            if (!skinGroups.ContainsKey(node.groupName))
            {
                skinGroups[node.groupName] = new HashSet<NodeWrapper>();
            }
            skinGroups[node.groupName].Add(node);
        }
        public static void EvaluateMetadata(NodeWrapper node)
        {
            AffixSettings affixSettings = ConvertConfig.currentConfig.affixSettings;
            node.affixes = "export_preserve_geometry\n\nexport_preserve_position\n\n\n";
            switch (node.type)
            {
                case NodeType.LOCATOR:
                    node.affixes = affixSettings.locatorDefault.Replace("\\n", "\n") + "\n";
                    break;
                case NodeType.BONE:
                    node.affixes = affixSettings.boneDefault.Replace("\\n", "\n") + "\n";
                    break;
                case NodeType.RIGID_MESH:
                    if (node.lods.Count > 0)
                    {
                        node.affixes = affixSettings.rigidDefault.Replace("\\n", "\n") + "\n";
                    }
                    else
                    {
                        node.affixes = affixSettings.rigidNoLodDefault.Replace("\\n", "\n") + "\n";
                        LogInfo(String.Format("Node {0} - no lods", node.usfNode.name));
                    }
                    node.usfNode.mayaNodeId = "mesh";
                    break;
                case NodeType.DECAL:
                    var constructorRegionDecal = SpeculateRegion(node.usfNode.name, node.gltfNode.Extras);
                    if (constructorRegionDecal == "")
                    {
                        if (node.lods.Count > 0)
                        {
                            node.affixes = affixSettings.decalDefault.Replace("\\n", "\n") + "\n";
                        }
                        else
                        {
                            node.affixes = affixSettings.decalNoLodDefault.Replace("\\n", "\n") + "\n";
                            LogInfo(String.Format("Node {0} - no lods", node.usfNode.name));
                        }
                    }
                    else
                    {
                        if (node.lods.Count > 0)
                        {
                            node.affixes = affixSettings.decalConstructorDefault.Replace("\\n", "\n").Replace("{REGION_NAME}", constructorRegionDecal) + "\n";
                        }
                        else
                        {
                            node.affixes = affixSettings.decalNoLodConstructorDefault.Replace("\\n", "\n").Replace("{REGION_NAME}", constructorRegionDecal) + "\n";
                            LogInfo(String.Format("Node {0} - no lods", node.usfNode.name));
                        }
                    }
                    node.usfNode.mayaNodeId = "mesh";
                    break;
                case NodeType.CDT:
                    var cdtName = node.usfNode.name.Replace("_CDT", "").Replace("_cdt", "");
                    node.affixes = affixSettings.cdtDefault.Replace("\\n", "\n").Replace("{NODE_NAME}", cdtName) + "\n";
                    node.usfNode.mayaNodeId = "mesh";
                    break;
                case NodeType.SKIN:
                    var constructorRegion = SpeculateRegion(node.usfNode.name, node.gltfNode.Extras);
                    if (constructorRegion == "")
                    {
                        if (node.lods.Count > 0)
                        {
                            node.affixes = affixSettings.skinDefault.Replace("\\n", "\n") + "\n";
                        }
                        else
                        {
                            node.affixes = affixSettings.skinNoLodDefault.Replace("\\n", "\n") + "\n";
                            LogInfo(String.Format("Node {0} - no lods", node.usfNode.name));
                        }
                    }
                    else
                    {
                        if (node.lods.Count > 0)
                        {
                            node.affixes = affixSettings.skinConstructorDefault.Replace("\\n", "\n").Replace("{REGION_NAME}", constructorRegion) + "\n";
                        }
                        else
                        {
                            node.affixes = affixSettings.skinNoLodConstructorDefault.Replace("\\n", "\n").Replace("{REGION_NAME}", constructorRegion) + "\n";
                            LogInfo(String.Format("Node {0} - no lods", node.usfNode.name));
                        }
                    }

                    break;
                case NodeType.CLOTH_SKIN:
                    var constructorRegionCloth = SpeculateRegion(node.usfNode.name, node.gltfNode.Extras);
                    if (constructorRegionCloth == "")
                    {
                        node.affixes = affixSettings.clothDefault.Replace("\\n", "\n") + "\n";
                    }
                    else
                    {
                        node.affixes = affixSettings.clothConstructorDefault.Replace("\\n", "\n").Replace("{REGION_NAME}", constructorRegionCloth) + "\n";
                    }
                    node.usfNode.mayaNodeId = "mesh";
                    break;
                case NodeType.CLOTH_SIM:
                    node.affixes = affixSettings.clothSimDefault.Replace("\\n", "\n") + "\n";
                    node.usfNode.mayaNodeId = "mesh";
                    break;
                case NodeType.ACTOR:
                    node.affixes = affixSettings.actorDefault.Replace("\\n", "\n") + "\n";
                    break;
                case NodeType.CHAR:
                    node.affixes = affixSettings.charDefault.Replace("\\n", "\n") + "\n";
                    break;
                case NodeType.DX:
                    node.affixes = affixSettings.dxDefault.Replace("\\n", "\n") + "\n";
                    break;
                case NodeType.LOD:
                    var constructorRegionLod = SpeculateRegion(node.usfNode.name, node.gltfNode.Extras);
                    if (constructorRegionLod == "")
                    {
                        if (node.cloth != null)
                        {
                            node.affixes = affixSettings.lodClothDefault.Replace("\\n", "\n") + "\n";
                        }
                        else
                        {
                            node.affixes = affixSettings.lodDefault.Replace("\\n", "\n") + "\n";
                        }
                    }
                    else
                    {
                        if (node.cloth != null)
                        {
                            node.affixes = affixSettings.lodClothConstructorDefault.Replace("\\n", "\n").Replace("{REGION_NAME}", constructorRegionLod) + "\n";
                        }
                        else
                        {
                            node.affixes = affixSettings.lodConstructorDefault.Replace("\\n", "\n").Replace("{REGION_NAME}", constructorRegionLod) + "\n";
                        }
                    }

                    if (node.decal != null)
                    {
                        node.affixes = "alias_decal_transp\n\n" + node.affixes;
                    }
                    node.usfNode.mayaNodeId = "mesh";
                    break;
            }

            if (ConvertConfig.currentConfig.affixSettings.clothHacks)
            {
                if (node.usfNode.name.Contains("seal") && node.type == NodeType.SKIN && !node.affixes.Contains("render_preserve_uv_data"))
                {
                    node.affixes = node.affixes.Replace("\n\n\n", "\n\ncloth_material\n\nrender_preserve_uv_data\n\n\n");
                }
                if (node.type == NodeType.LOD && !node.affixes.Contains("render_preserve_uv_data") && node.skin != null && node.skin.usfNode.name.Contains("seal"))
                {
                    node.affixes = node.affixes.Replace("\n\n\n", "\n\ncloth_material\n\nrender_preserve_uv_data\n\n\n");
                }
                if (node.type == NodeType.LOD && !node.affixes.Contains("render_preserve_uv_data") && (node.cloth != null))
                {
                    node.affixes = node.affixes.Replace("\n\n\n", "\n\nrender_preserve_uv_data\n\n\n");
                }
            }

            if (node.storedAffixes == null || node.storedAffixes == "")
            {
                node.usfNode.affixes = node.affixes;
            }
            else
            {
                node.usfNode.affixes = node.storedAffixes;
            }
        }

        public static void BuildSkinnedGeometryExperimental(UsfNode skinnedGeometry, Node skinnedGeometrySource, ConvertParams convertParams)
        {
            foreach (var group in skinnedGeometrySource.VisualChildren)
            {
                var groupNode = CreateGroupNode(group.Name);
                foreach (var skin in group.VisualChildren)
                {
                    PopulateGroupExperimental(groupNode, skin, convertParams);
                }
                skinnedGeometry.AddChild(groupNode);
            }
        }
        private static void PopulateGroupExperimental(UsfNode parent, Node node, ConvertParams convertParams)
        {
            var nodeWrapper = nodes.Where(n => n.gltfNode == node).FirstOrDefault();
            ConvertMesh(nodeWrapper, convertParams);
            if (nodeWrapper.gltfNode.Skin == null)
            {
                LogWarning($"Skipping {node.Name} - no skin attached");
                return;
            }
            AttachBones(nodeWrapper.usfNode.mesh, nodeWrapper.gltfNode.Skin);
            foreach (var child in node.VisualChildren)
            {
                PopulateGroupExperimental(nodeWrapper.usfNode, child, convertParams);
            }

            parent.AddChild(nodeWrapper.usfNode);
        }
        public static void BuildSkinnedGeometry(UsfNode skinnedGeometry, ConvertParams convertParams)
        {
            foreach (var groupName in skinGroups.Keys)
            {
                //helmet_lieutenant

                var mergedGroupName = groupName.Split("_decal")[0];

                foreach (var rule in ConvertConfig.currentConfig.groupRules)
                {
                    if (mergedGroupName.Contains(rule.key))
                    {
                        if (rule.merge == "replace")
                        {
                            mergedGroupName = rule.tokens[0];
                        }
                        else if (rule.merge == "strip")
                        {
                            foreach (var token in rule.tokens)
                            {
                                mergedGroupName = mergedGroupName.Split(token)[0];
                            }
                        }
                    }
                }
                var group = skinGroups[groupName];
                var anyNode = group.First();
                if (anyNode.cloth == null && anyNode.skin == null && anyNode.decal == null)
                {
                    continue;
                }
                var groupNode = nodes.Where(n => n.usfNode.name == mergedGroupName + "_grp").Select(n => n.usfNode).FirstOrDefault();
                if (groupNode == null)
                {
                    groupNode = CreateGroupNode(mergedGroupName + "_grp");
                    skinnedGeometry.AddChild(groupNode);
                }

                var lodRoot = anyNode.skin;
                if (anyNode.cloth != null)
                {
                    ConvertMesh(anyNode.cloth, convertParams);
                    AttachBones(anyNode.cloth.usfNode.mesh, anyNode.cloth.gltfNode.Skin);
                    groupNode.AddChild(anyNode.cloth.usfNode);
                    lodRoot = anyNode.cloth;
                }
                if (anyNode.skin != null)
                {
                    ConvertMesh(anyNode.skin, convertParams);
                    AttachBones(anyNode.skin.usfNode.mesh, anyNode.skin.gltfNode.Skin);
                    groupNode.AddChild(anyNode.skin.usfNode);
                    lodRoot = anyNode.skin;
                }
                if (anyNode.decal != null)
                {
                    ConvertMesh(anyNode.decal, convertParams);
                    AttachBones(anyNode.decal.usfNode.mesh, anyNode.decal.gltfNode.Skin);
                    groupNode.AddChild(anyNode.decal.usfNode);
                    lodRoot = anyNode.decal;
                }
                if (anyNode.sim != null)
                {
                    ConvertMesh(anyNode.sim, convertParams);
                    AttachBones(anyNode.sim.usfNode.mesh, anyNode.sim.gltfNode.Skin);
                    groupNode.AddChild(anyNode.sim.usfNode);
                }

                foreach (var lod in anyNode.lods)
                {
                    ConvertMesh(lod, convertParams);
                    AttachBones(lod.usfNode.mesh, lod.gltfNode.Skin);
                    lodRoot.usfNode.AddChild(lod.usfNode);
                }
            }
        }
        private static string SpeculateRegion(string n, JsonNode extras = null)
        {
            if (extras != null)
            {
                var extra = JsonConvert.DeserializeObject<UsfNodeExtra>(extras.ToJsonString());
                if (extra != null && extra.region != null && extra.region != "")
                {
                    return extra.region;
                }
            }
            var name = n.ToLower();
            var sideName = "";
            if (!ConvertConfig.currentConfig.isConstructor)
            {
                return "";
            }

            RegionRules rules = ConvertConfig.currentConfig.regionRules;
            foreach (var ex in rules.exceptionList)
            {
                if (name.Contains(ex)) return "";
            }
            foreach (var token in rules.leftTokens)
            {
                if (name.Contains(token))
                {
                    sideName = rules.leftRegion;
                    break;
                }
            }
            foreach (var token in rules.rightTokens)
            {
                if (name.Contains(token))
                {
                    sideName = rules.rightRegion;
                    break;
                }
            }

            foreach (var region in rules.regions)
            {
                foreach (var token in region.tokens)
                {
                    if (name.Contains(token) && (region.contains == null || name.Contains(region.contains)))
                    {
                        if (region.centered)
                        {
                            return region.region;
                        }
                        else
                        {
                            return sideName + region.region;
                        }
                    }
                }
            }
            return "";
        }
        private static UsfNode CreateGroupNode(string name)
        {
            var nodeWrapper = new NodeWrapper();
            var node = new UsfNode();
            node.pses = new UsfPses(true);
            node.name = name;
            node.mayaNodeId = "";
            node.affixes = "export_preserve_position\n\n\n";
            node.local = UsfMatrix4.Identity();
            node.localOriginal = UsfMatrix4.Identity();
            node.world = UsfMatrix4.Identity();
            node.uid = (int)UsfNodeCommonUID.Top + nodes.Count;

            nodeWrapper.usfNode = node;
            nodeWrapper.usfUid = node.uid;
            nodeWrapper.type = NodeType.GROUP;
            nodes.Add(nodeWrapper);

            LogInfo(String.Format("Skinned geometry group: {0}", name));
            return node;
        }
        private static void ExtractMaterials(NodeWrapper node)
        {
            if (node.gltfNode == null || node.gltfNode.Mesh == null)
            {
                return;
            }

            var materials = node.gltfNode.Mesh.Primitives.Select(p => p.Material).Where(m => m != null).DistinctBy(m => m.LogicalIndex).ToArray();
            foreach (var m in materials)
            {
                var usfMaterial = new UsfMaterial();
                if (m.Name == null)
                {
                    m.Name = "";
                }
                if (m.Name.Contains("."))
                {
                    m.Name = m.Name.Split(".")[0];
                }
                if (node.type == NodeType.CLOTH_SIM)
                {
                    usfMaterial.InitializeDefaultCloth();
                    LogInfo(String.Format("Node {0} Material {1} Speculated default definition: Cloth", node.usfNode.name, m.Name));
                }
                else if (node.type == NodeType.CDT)
                {
                    usfMaterial.InitializeDefaultCdt();
                    node.materials.Add("", usfMaterial);
                    LogInfo(String.Format("Node {0} Material {1} Speculated default definition: CDT", node.usfNode.name, m.Name));
                    break;
                }
                else if (node.type == NodeType.DECAL || (node.type == NodeType.LOD && node.decal != null))
                {
                    usfMaterial.InitializeDefaultDecal();
                    node.materials.Add("", usfMaterial);
                    LogInfo(String.Format("Node {0} Material {1} Speculated default definition: DECAL", node.usfNode.name, m.Name));
                }
                else
                {
                    usfMaterial.InitializeDefault();
                    LogInfo(String.Format("Node {0} Material {1} Speculated default definition: Default", node.usfNode.name, m.Name));
                }

                var submaterial = "";
                var textureName = m.Name.Split("_mat")[0];
                if (m.Extras != null)
                {
                    var extras = JsonConvert.DeserializeObject<UsfMaterialExtra>(m.Extras.ToJsonString());
                    if (extras != null)
                    {
                        if (extras.texture != null && extras.texture.Trim() != "")
                        {
                            textureName = extras.texture;
                        }
                        if (extras.submaterial != null && extras.submaterial.Trim() != "")
                        {
                            submaterial = extras.submaterial;
                        }
                    }

                }
                if (m.Name != "")
                {
                    usfMaterial.data["material"]["name"] = new ValueString(m.Name);
                }

                if (usfMaterial.data["layers"] != null)
                {
                    var layers = ((ValueArray)usfMaterial.data["layers"]).Value;
                    foreach (var layer in layers)
                    {
                        layer["textureName"] = new ValueString(textureName);
                        if (submaterial != "") layer["subMaterial"] = new ValueString(submaterial);
                    }
                }

                if (!node.materials.ContainsKey(m.Name)) node.materials.Add(m.Name, usfMaterial);
            }
            if (node.gltfNode.Mesh.Extras != null)
            {
                var extras = JsonConvert.DeserializeObject<UsfMeshExtra>(node.gltfNode.Mesh.Extras.ToJsonString());
                if (extras != null && extras.materials != null)
                {
                    UsfMaterial[] storedMaterials = extras.materials;
                    foreach (var storedMat in storedMaterials)
                    {
                        if (storedMat.data == null)
                        {
                            continue;
                        }
                        if (storedMat.data["material"] == null)
                        {
                            continue;
                        }
                        if (storedMat.data["material"]["name"] == null)
                        {
                            continue;
                        }
                        var name = ((ValueString)storedMat.data["material"]["name"]).Value;
                        if (node.materials.ContainsKey(name))
                        {
                            node.materials[name] = storedMat;
                            LogInfo(String.Format("Node {0} Material {1} Extracted material defs from Extras", node.usfNode.name, name));
                        }
                    }
                }
            }

            var matIdx = 0;
            foreach (var name in node.materials.Keys)
            {
                var colorChannelMap = new List<int>();
                var material = node.materials[name];
                if (material.data["colorSets"] != null)
                {
                    var colorSets = ((ValueArray)material.data["colorSets"]).Value;
                    foreach (var set in colorSets)
                    {
                        if (set["colorChannelIdx"] == null)
                        {
                            colorChannelMap.Add(-1);
                            continue;
                        }
                        var tmp = (ValueInt)set["colorChannelIdx"];
                        colorChannelMap.Add(tmp.Value);
                    }
                }
                else
                {
                    colorChannelMap.Add(-1);
                    colorChannelMap.Add(-1);
                    colorChannelMap.Add(-1);
                    colorChannelMap.Add(-1);
                }

                if (material.data["extraVertexColorData"] != null)
                {
                    if (material.data["extraVertexColorData"]["colorR"] != null)
                    {
                        var idx = ((ValueInt)material.data["extraVertexColorData"]["colorR"]["colorSetIdx"]).Value;
                        colorChannelMap.Add(idx);
                    }
                    if (material.data["extraVertexColorData"]["colorG"] != null)
                    {
                        var idx = ((ValueInt)material.data["extraVertexColorData"]["colorG"]["colorSetIdx"]).Value;
                        colorChannelMap.Add(idx);
                    }
                    if (material.data["extraVertexColorData"]["colorB"] != null)
                    {
                        var idx = ((ValueInt)material.data["extraVertexColorData"]["colorB"]["colorSetIdx"]).Value;
                        colorChannelMap.Add(idx);
                    }
                    if (material.data["extraVertexColorData"]["colorA"] != null)
                    {
                        var idx = ((ValueInt)material.data["extraVertexColorData"]["colorA"]["colorSetIdx"]).Value;
                        colorChannelMap.Add(idx);
                    }
                }
                else
                {
                    colorChannelMap.Add(-1);
                    colorChannelMap.Add(-1);
                    colorChannelMap.Add(-1);
                    colorChannelMap.Add(-1);
                }

                while (colorChannelMap.Count < 8) colorChannelMap.Add(-1);
                node.matIndexToMap.Add(matIdx, colorChannelMap);
                ++matIdx;
            }
        }
        private static void ConvertMesh(NodeWrapper node, ConvertParams convertParams)
        {
            if (node.gltfNode == null || node.gltfNode.Mesh == null)
            {
                return;
            }
            if (node.materials.ContainsKey("Material_0") && node.type != NodeType.CDT)
            {
                return;
            }
            var isSkin = node.gltfNode.Skin != null;
            var mesh = new UsfNodeMesh();
            node.usfNode.mesh = mesh;
            mesh.skinningMethod = SkinnginMethod.DUAL_QUATERNION;
            mesh.materials = node.materials.Values.ToArray();
            var materialToIndex = new Dictionary<string, int>();
            foreach (var material in mesh.materials)
            {
                if (material.data["material"] == null || material.data["material"]["name"] == null)
                {
                    if (!materialToIndex.ContainsKey("")) materialToIndex.Add("", materialToIndex.Count);
                    continue;
                }

                var name = ((ValueString)material.data["material"]["name"]).Value;
                if (materialToIndex.ContainsKey(name)) continue;

                materialToIndex.Add(name, materialToIndex.Count);
            }

            var totalElements = node.gltfNode.Mesh.Primitives.Select(p => p.IndexAccessor.Count).Sum();
            mesh.vertixes = new UsfVector3[totalElements];
            mesh.normals = new UsfVector3[totalElements];
            mesh.faces = new UsfFace[totalElements / 3];
            for (int i = 0; i < totalElements / 3; ++i)
            {
                mesh.faces[i] = new UsfFace();
            }
            var colorSetMaxIdx = node.matIndexToMap.Values.SelectMany(s => s).Max();


            if (colorSetMaxIdx == -1)
            {
                mesh.colors = new uint[0][];
            }
            else
            {
                var colorSets = Math.Max(colorSetMaxIdx / 4 + 1, 2);
                mesh.colors = new uint[colorSets][];
            }

            for (int i = 0; i < mesh.colors.Length; ++i)
            {
                mesh.colors[i] = new uint[totalElements];
            }
            var uvSetNames = new HashSet<string>();
            foreach (var mat in mesh.materials)
            {
                var uvSetArray = (ValueArray)mat.data["uvSets"];
                if (uvSetArray == null || uvSetArray.Value.Count == 0)
                {
                    continue;
                }

                for (var i = 0; i < uvSetArray.Value.Count; ++i)
                {
                    var name = (ValueString)uvSetArray.Value[i]["name"];
                    if (!uvSetNames.Contains(name.Value)) uvSetNames.Add(name.Value);
                }
            }
            mesh.uvSets = new UsfUVSet[uvSetNames.Count];
            var names = uvSetNames.ToArray();
            for (var i = 0; i < names.Length; ++i)
            {
                mesh.uvSets[i] = new UsfUVSet();
                mesh.uvSets[i].uvs = new UsfVector2[totalElements];
                mesh.uvSets[i].name = names[i];
            }
            mesh.tangents = new UsfVector4[1][];
            mesh.tangents[0] = new UsfVector4[totalElements];

            mesh.bones = new UsfBone[0];
            if (isSkin)
            {
                mesh.skins = new UsfSkinVtx[totalElements];
            }
            else
            {
                mesh.skins = new UsfSkinVtx[0];
            }
            mesh.bonePairs = new KeyValuePair<int, int>[0];
            mesh.blendShapes = new KeyValuePair<string, UsfSpl>[0];
            var offset = 0;

            foreach (var primitive in node.gltfNode.Mesh.Primitives)
            {
                var positions = primitive.GetVertices("POSITION").AsVector3Array().ToArray();
                var normals = primitive.GetVertices("NORMAL").AsVector3Array().ToArray();
                var uvs = new Vector2[4][];
                for (var j = 0; j < 4; ++j)
                {
                    if (primitive.VertexAccessors.ContainsKey("TEXCOORD_" + j.ToString()))
                    {
                        uvs[j] = primitive.GetVertices("TEXCOORD_" + j.ToString()).AsVector2Array().ToArray();
                    }
                    else
                    {
                        uvs[j] = new Vector2[0];
                    }
                }

                var cols = new Vector4[2][];
                var cco = primitive.VertexAccessors.Keys.Where(k => k.Contains("COLOR_")).Count() - 2;
                if (cco < 0) cco = 0;
                for (var k = 0; k < 2; ++k)
                {
                    var idx = k + cco;
                    if (primitive.VertexAccessors.ContainsKey("COLOR_" + idx.ToString()))
                    {
                        var accessor = primitive.GetVertices("COLOR_" + idx.ToString());
                        if (accessor.Attribute.Dimensions == DimensionType.VEC4)
                        {
                            cols[k] = primitive.GetVertices("COLOR_" + idx.ToString()).AsVector4Array().ToArray();
                        }
                        else if (accessor.Attribute.Dimensions == DimensionType.VEC3)
                        {
                            var tmp = primitive.GetVertices("COLOR_" + idx.ToString()).AsVector3Array().ToArray();
                            cols[k] = new Vector4[positions.Length];
                            for (var j = 0; j < positions.Length; ++j) cols[k][j] = new Vector4(tmp[j], 1);
                        }
                    }
                    else
                    {
                        cols[k] = new Vector4[positions.Length];
                        for (var j = 0; j < positions.Length; ++j) cols[k][j] = new Vector4(1, 1, 1, 1);
                    }
                }
                var isAllWhite = true;
                foreach (var col in cols[0])
                {
                    if (col.X != 1.0f || col.Y != 1.0f || col.Z != 1.0f || col.W != 1.0)
                    {
                        isAllWhite = false;
                        break;
                    }
                }
                if (isAllWhite && (node.type == NodeType.CLOTH_SIM || node.type == NodeType.DECAL || (node.type == NodeType.LOD && node.decal != null)))
                {
                    LogInfo(String.Format("SIM Node {0} COLOR0 channel is all white, swapping with COLOR1", node.usfNode.name));
                    var tmp = cols[0];
                    cols[0] = cols[1];
                    cols[1] = tmp;
                }

                Vector4[] tangents;
                if (primitive.VertexAccessors.ContainsKey("TANGENT"))
                {
                    tangents = primitive.GetVertices("TANGENT").AsVector4Array().ToArray();
                    if (convertParams.invertCustomTangents)
                    {
                        for (var j = 0; j < tangents.Length; ++j)
                        {
                            tangents[j] = -tangents[j];
                        }
                    }
                }
                else
                {
                    tangents = new Vector4[positions.Length];
                    for (var j = 0; j < positions.Length; ++j) tangents[j] = new Vector4(0, 0, 0, -1);
                }

                uint matIdx = 0;
                if (primitive.Material != null)
                {
                    if (materialToIndex.ContainsKey(primitive.Material.Name))
                    {
                        matIdx = (uint)materialToIndex[primitive.Material.Name];
                    }
                    else
                    {
                        matIdx = 0;
                    }

                }
                for (int idx = offset / 3; idx < offset / 3 + primitive.IndexAccessor.Count / 3; ++idx)
                {
                    mesh.faces[idx].materialIndex = matIdx;
                }

                var weights = new Vector4Array[2];
                var joints = new Vector4Array[2];
                if (isSkin)
                {
                    weights[0] = primitive.GetVertices("WEIGHTS_0").AsVector4Array();
                    joints[0] = primitive.GetVertices("JOINTS_0").AsVector4Array();
                }

                int i = 0;
                foreach (int index in primitive.IndexAccessor.AsIndicesArray())
                {
                    mesh.vertixes[offset + i] = new UsfVector3(positions[index]);
                    if (index < normals.Length) mesh.normals[offset + i] = new UsfVector3(normals[index]);
                    if (index < tangents.Length) mesh.tangents[0][offset + i] = new UsfVector4(tangents[index]);

                    BuildColors(mesh.colors, i, node.matIndexToMap[(int)matIdx], cols, index);

                    if (index < uvs[0].Length && mesh.uvSets.Length > 0) mesh.uvSets[0].uvs[offset + i] = new UsfVector2(uvs[0][index]);
                    if (mesh.uvSets.Length > 1 && index < uvs[1].Length && mesh.uvSets.Length > 1) mesh.uvSets[1].uvs[offset + i] = new UsfVector2(uvs[0][index]);

                    if (index < weights[0].Count && isSkin)
                    {
                        mesh.skins[offset + i] = new UsfSkinVtx(4);
                        mesh.skins[offset + i].weight = new float[4];
                        mesh.skins[offset + i].weight[0] = weights[0][index].X;
                        mesh.skins[offset + i].weight[1] = weights[0][index].Y;
                        mesh.skins[offset + i].weight[2] = weights[0][index].Z;
                        mesh.skins[offset + i].weight[3] = 1.0f;

                        mesh.skins[offset + i].boneIdx = new int[4];
                        mesh.skins[offset + i].boneIdx[0] = (int)joints[0][index].X;
                        mesh.skins[offset + i].boneIdx[1] = (int)joints[0][index].Y;
                        mesh.skins[offset + i].boneIdx[2] = (int)joints[0][index].Z;
                        mesh.skins[offset + i].boneIdx[3] = (int)joints[0][index].W;
                    }

                    ++i;
                }
                offset += primitive.IndexAccessor.Count;
            }
        }

        private static void BuildColors(uint[][] colors, int dstIndex, List<int> colorChannelMap, Vector4[][] cols, int srcIndex)
        {
            if (colors.Length == 0)
            {
                return;
            }
            var COLOR0 = cols[0][srcIndex];
            var COLOR1 = cols[1][srcIndex];
            var data = new float[colors.Length * 4];
            for (var i = 0; i < data.Length; ++i)
            {
                data[i] = ((i + 1) % 4 == 0) ? 1.0f : 0.0f;
            }
            if (colorChannelMap[0] == 12)
            {
                COLOR0.Y = COLOR0.X;
                COLOR0.Z = COLOR0.X;
                COLOR0.W = 1.0f;

                colorChannelMap[1] = 13;
                colorChannelMap[2] = 14;
                colorChannelMap[3] = 15;
            }
            if (colorChannelMap[0] != -1) data[colorChannelMap[0]] = COLOR0.X;
            if (colorChannelMap[1] != -1) data[colorChannelMap[1]] = COLOR0.Y;
            if (colorChannelMap[2] != -1) data[colorChannelMap[2]] = COLOR0.Z;
            if (colorChannelMap[3] != -1) data[colorChannelMap[3]] = COLOR0.W;
            if (colorChannelMap[4] != -1) data[colorChannelMap[4]] = COLOR1.X;
            if (colorChannelMap[5] != -1) data[colorChannelMap[5]] = COLOR1.Y;
            if (colorChannelMap[6] != -1) data[colorChannelMap[6]] = COLOR1.Z;
            if (colorChannelMap[7] != -1) data[colorChannelMap[7]] = COLOR1.W;
            for (var i = 0; i < colors.Length; ++i)
            {
                colors[i][dstIndex] = ConvertColor(new Vector4(data[i * 4 + 0], data[i * 4 + 1], data[i * 4 + 2], data[i * 4 + 3]));
            }
        }
        private static void MergeNodes(NodeWrapper dst, NodeWrapper src)
        {
            foreach (var child in src.gltfNode.VisualChildren)
            {
                var childNode = indexToNode[child.LogicalIndex];
                if (!dst.usfNode.children.Where(n => n.name == child.Name).Any())
                {
                    dst.usfNode.AddChild(childNode.usfNode);
                    MergeNodes(childNode, childNode);
                } else
                {
                    var dstChildGltf = dst.gltfNode.VisualChildren.Where(n => n.Name == child.Name).FirstOrDefault();
                    var dstChildNode = indexToNode[dstChildGltf.LogicalIndex];
                    MergeNodes(dstChildNode, childNode);
                }

            }
            return;
        }

        private static void AttachNodes(NodeWrapper node)
        {
            foreach (var child in node.gltfNode.VisualChildren)
            {
                var childNode = indexToNode[child.LogicalIndex];
                if (childNode.type == NodeType.SKIN || childNode.type == NodeType.CLOTH_SKIN)
                {
                    continue;
                }
                if (childNode.type == NodeType.LOD && (childNode.skin != null || childNode.cloth != null || childNode.decal != null))
                {
                    continue;
                }
                if (childNode.type == NodeType.CLOTH_SIM && (childNode.rigid == null || childNode.cloth != null))
                {
                    continue;
                }
                if (childNode.type == NodeType.DECAL)
                {
                    continue;
                }

                node.usfNode.AddChild(childNode.usfNode);
                AttachNodes(childNode);
            }
            return;
        }
        private static void AttachBones(UsfNodeMesh mesh, Skin skin)
        {
            var bones = new List<UsfBone>();
            for (var i = 0; i < skin.Joints.Count; ++i)
            {
                var bone = new UsfBone();
                var boneNode = indexToNode[skin.Joints[i].LogicalIndex];
                bone.usfNodeUid = boneNode.usfUid;
                bone.bindMatrix = ConvertMatrix(skin.InverseBindMatrices[i]);
                bones.Add(bone);
            }
            mesh.bones = bones.ToArray();
        }
        private static void ProcessAnimations(NodeWrapper node)
        {
            if (node.gltfNode.IsTransformAnimated)
            {
                var anims = node.gltfNode.LogicalParent.LogicalAnimations;

                var keyframesTranslation = new List<Vector3>();
                var keyframesScale = new List<Vector3>();
                var keyframesRotation = new List<Quaternion>();
                foreach (var anim in anims)
                {
                    var translationChannel = anim.FindTranslationChannel(node.gltfNode);
                    var scaleChannel = anim.FindScaleChannel(node.gltfNode);
                    var rotationChannel = anim.FindRotationChannel(node.gltfNode);
                    GenerateKeytrack(keyframesTranslation, anim.Name, translationChannel?.GetTranslationSampler(), node.gltfNode.LocalTransform.Translation);
                    GenerateKeytrack(keyframesScale, anim.Name, scaleChannel?.GetScaleSampler(), node.gltfNode.LocalTransform.Scale);
                    GenerateKeytrack(keyframesRotation, anim.Name, rotationChannel?.GetRotationSampler(), node.gltfNode.LocalTransform.Rotation);
                }
                var usfAnim = new UsfAnimation();
                usfAnim.initialTranslate = new UsfVector3(keyframesTranslation.First());
                usfAnim.initialScale = new UsfVector3(keyframesScale.First());
                usfAnim.initialRotation = new UsfQuaternion(keyframesRotation.First());
                usfAnim.initialVisibility = 1.0f;

                usfAnim.splTranslate = new UsfSpl(); usfAnim.splTranslate.FromList(keyframesTranslation);
                usfAnim.splScale = new UsfSpl(); usfAnim.splScale.FromList(keyframesScale);
                usfAnim.splRotation = new UsfSpl(); usfAnim.splRotation.FromList(keyframesRotation);
                node.usfNode.animation = usfAnim;
            }
        }

        private static void GenerateKeytrack<T>(List<T> keyframes, string name, IAnimationSampler<T> sampler, T defValue)
        {
            var targetLength = animationLength[name];
            var keys = sampler?.GetLinearKeys().ToList();
            if (keys == null)
            {
                keys = [(0, defValue)];
            }
            keys.Sort((e1, e2) => e1.Key.CompareTo(e2.Key));
            var flatKeys = keys.Select(e => e.Value).ToList();
            var lastFrame = flatKeys.Last();
            while (flatKeys.Count() < targetLength) flatKeys.Add(lastFrame);
            keyframes.AddRange(flatKeys);
            while (keyframes.Count % 10 != 0) keyframes.Add(lastFrame);
        }
        private static void GenerateNamespaces(UsfNode node)
        {
            node.sourceId = node.GetParent().sourceId + "|" + node.name;
            foreach (var child in node.children)
            {
                GenerateNamespaces(child);
            }
        }

        private static void LogInfo(string msg)
        {
            Log("INFO: " + msg);
        }
        private static void LogWarning(string msg)
        {
            Log("WARN: " + msg);
        }
        private static void Log(string msg)
        {
            Console.WriteLine(msg);
        }

        private static HashSet<UsfNode> usfNodes = new HashSet<UsfNode>();
        private static HashSet<UsfNode> bones = new HashSet<UsfNode>();
        private static Dictionary<Node, UsfNode> boneToUsf = new Dictionary<Node, UsfNode>();
        private static Dictionary<int, Node> uidToGltfNode = new Dictionary<int, Node>();
        private static Dictionary<int, UsfNode> indexToUsfUid = new Dictionary<int, UsfNode>();
        public enum NodeType
        {
            LOCATOR,
            BONE,
            CDT,
            RIGID_MESH,
            SKIN,
            CLOTH_SKIN,
            CLOTH_SIM,
            LOD,
            CHAR,
            ACTOR,
            DX,
            ROOT,
            CHAR_ROOT,
            SKINNED_GEOM,
            GROUP,
            DECAL
        }
        public class NodeWrapper
        {
            public NodeType type;
            public Node gltfNode;
            public UsfNode usfNode;
            public int gltfIndex;
            public int usfUid;
            public string affixes;
            public string storedAffixes;

            public string groupName;
            public NodeWrapper cloth;
            public NodeWrapper skin;
            public NodeWrapper sim;
            public NodeWrapper rigid;
            public List<NodeWrapper> lods;
            public NodeWrapper decal;

            public Dictionary<string, UsfMaterial> materials = new Dictionary<string, UsfMaterial>();
            public Dictionary<int, List<int>> matIndexToMap = new Dictionary<int, List<int>>();

            public static explicit operator string(NodeWrapper obj) { return obj.ToString(); }
            public override string ToString()
            {
                return usfNode.ToString();
            }
        }
        private static void FixNames(IEnumerable<Node> nodes)
        {
            foreach (var node in nodes)
            {
                if (node.Name != null && node.Name != ".root." && node.Name.Contains("."))
                {
                    var tokens = node.Name.Split('.');
                    var name = tokens[0];
                    for (int i = 1; i < tokens.Length; ++i)
                    {
                        var token = tokens[i];
                        if (token.StartsWith("0"))
                        {
                            break;
                        }
                        else
                        {
                            name += "." + token;
                        }
                    }
                    node.Name = name;
                }
                if (node.Mesh != null && node.Mesh.Name != null && node.Mesh.Name.Contains("."))
                {
                    var tokens = node.Mesh.Name.Split('.');
                    var name = tokens[0];
                    for (int i = 1; i < tokens.Length; ++i)
                    {
                        var token = tokens[i];
                        if (token.StartsWith("0"))
                        {
                            break;
                        }
                        else
                        {
                            name += "." + token;
                        }
                    }
                    node.Mesh.Name = name;
                }
                FixNames(node.VisualChildren);
            }
        }
        private static void FixSkinNamespaces(UsfNode skinnedGeom)
        {
            foreach (var grp in skinnedGeom.children)
            {
                if (grp.children.Count == 0)
                {
                    LogWarning(grp.name + " got no children. Unrigged geometry in skinned_geometry section?");
                    continue;
                }
                    
                var ns = grp.children[0].name;
                grp.sourceId = grp.GetParent().sourceId + "|" + ns + ":" + grp.name;
                foreach (var part in grp.children)
                {
                    part.sourceId = part.GetParent().sourceId + "|" + ns + ":" + part.name;
                    foreach (var lod in part.children)
                    {
                        lod.sourceId = lod.GetParent().sourceId + "|" + ns + ":" + lod.name;
                    }
                }
            }
        }
        private static string ExtractName(UsfSaberActor actorExtra, Scene gltfScene)
        {
            var tokens = actorExtra.tplName.Split("\\");
            foreach (var token in tokens)
            {
                if (token.ToLower().EndsWith(".tpl"))
                {
                    return token.Replace(".tpl", "");
                }
            }

            return tokens.Last();
        }
        private static int maxUid = 0;
        private static uint ConvertColor(Vector4 c)
        {
            var r = (byte)(c.X * 255.0f);
            var g = (byte)(c.Y * 255.0f);
            var b = (byte)(c.Z * 255.0f);
            var a = (byte)(c.W * 255.0f);
            return (uint)(r | (g << 8) | (b << 16) | (a << 24));
        }
        private static UsfMatrix4 ConvertMatrix(Matrix4x4 matrix)
        {
            var usfMatrix = new UsfMatrix4();
            usfMatrix.rows[0].x = matrix.M11;
            usfMatrix.rows[0].y = matrix.M12;
            usfMatrix.rows[0].z = matrix.M13;
            usfMatrix.rows[0].w = matrix.M14;

            usfMatrix.rows[1].x = matrix.M21;
            usfMatrix.rows[1].y = matrix.M22;
            usfMatrix.rows[1].z = matrix.M23;
            usfMatrix.rows[1].w = matrix.M24;

            usfMatrix.rows[2].x = matrix.M31;
            usfMatrix.rows[2].y = matrix.M32;
            usfMatrix.rows[2].z = matrix.M33;
            usfMatrix.rows[2].w = matrix.M34;

            usfMatrix.rows[3].x = matrix.M41;
            usfMatrix.rows[3].y = matrix.M42;
            usfMatrix.rows[3].z = matrix.M43;
            usfMatrix.rows[3].w = matrix.M44;

            return usfMatrix;
        }
    }


    public class IntermediateBody
    {
        public IntermediateBody() { }

        public UsfSaberActor actorInfo;
        public GltfNodeWrapper CENTRE;
        public List<Node> skinned_geometry = new List<Node>();
    }

    public class GltfNodeWrapper
    {
        public GltfNodeWrapper(Node me)
        {
            node = me;
        }
        public GltfNodeWrapper parent;
        public Node node;
        public List<GltfNodeWrapper> children = new List<GltfNodeWrapper>();
        public void AddChild(GltfNodeWrapper child)
        {
            child.parent = this;
            children.Add(child);
        }
    }

    public class ConvertConfig
    {
        public static ConvertConfig currentConfig;
        public List<GroupRule> groupRules { get; set; }
        public RegionRules regionRules { get; set; }
        public AffixSettings affixSettings { get; set; }
        public bool isConstructor;
    }

    public class GroupRule
    {
        public string merge { get; set; }
        public string compare { get; set; }
        public string key { get; set; }
        public List<string> tokens { get; set; }
    }
    public class RegionRules
    {
        public string modelNameContains { get; set; }
        public List<RegionRule> regions { get; set; }
        public List<string> exceptionList { get; set; }
        public List<string> leftTokens { get; set; }
        public string leftRegion { get; set; }
        public List<string> rightTokens { get; set; }
        public string rightRegion { get; set; }
    }
    public class RegionRule
    {
        public bool centered { get; set; }
        public List<string> tokens { get; set; }
        public string contains;
        public string region;
    }

    public class AffixSettings
    {
        public bool clothHacks { get; set; }
        public string actorDefault { get; set; }
        public string charDefault { get; set; }
        public string dxDefault { get; set; }
        public string locatorDefault { get; set; }
        public string boneDefault { get; set; }
        public string rigidNoLodDefault { get; set; }
        public string rigidDefault { get; set; }
        public string decalConstructorDefault { get; set; }
        public string decalNoLodConstructorDefault { get; set; }
        public string decalDefault { get; set; }
        public string decalNoLodDefault { get; set; }
        public string skinConstructorDefault { get; set; }
        public string skinNoLodConstructorDefault { get; set; }
        public string skinDefault { get; set; }
        public string skinNoLodDefault { get; set; }
        public string clothDefault { get; set; }
        public string clothConstructorDefault { get; set; }
        public string clothSimDefault { get; set; }
        public string lodDefault { get; set; }
        public string lodClothDefault { get; set; }
        public string lodConstructorDefault { get; set; }
        public string lodClothConstructorDefault { get; set; }
        public string cdtDefault { get; set; }
    }
}
