﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEditor.Graphing;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace UnityEditor.ShaderGraph.Drawing
{
    public class PreviewManager : IDisposable
    {
        AbstractMaterialGraph m_Graph;
        List<PreviewRenderData> m_RenderDatas = new List<PreviewRenderData>();
//        List<PreviewShaderData> m_ShaderDatas = new List<PreviewShaderData>();
        PreviewRenderData m_MasterRenderData;
        List<Identifier> m_Identifiers = new List<Identifier>();
        List<bool> m_DirtyPreviews = new List<bool>();
        List<bool> m_DirtyShaders = new List<bool>();
        List<bool> m_TimeDependentPreviews = new List<bool>();
        Material m_PreviewMaterial;
        MaterialPropertyBlock m_PreviewPropertyBlock;
        PreviewSceneResources m_SceneResources;
        Texture2D m_ErrorTexture;
        Shader m_UberShader;
        Dictionary<Guid, int> m_UberShaderIds;
        FloatShaderProperty m_OutputIdProperty;

        public PreviewRenderData masterRenderData
        {
            get { return m_MasterRenderData; }
        }

        public PreviewManager(AbstractMaterialGraph graph)
        {
            m_Graph = graph;
            m_PreviewMaterial = new Material(Shader.Find("Unlit/Color")) { hideFlags = HideFlags.HideInHierarchy };
            m_PreviewMaterial.hideFlags = HideFlags.HideAndDontSave;
            m_PreviewPropertyBlock = new MaterialPropertyBlock();
            m_ErrorTexture = new Texture2D(2, 2);
            m_ErrorTexture.SetPixel(0, 0, Color.magenta);
            m_ErrorTexture.SetPixel(0, 1, Color.black);
            m_ErrorTexture.SetPixel(1, 0, Color.black);
            m_ErrorTexture.SetPixel(1, 1, Color.magenta);
            m_ErrorTexture.filterMode = FilterMode.Point;
            m_ErrorTexture.Apply();
            m_SceneResources = new PreviewSceneResources();
            m_UberShader = ShaderUtil.CreateShaderAsset(k_EmptyShader);
            m_UberShader.hideFlags = HideFlags.HideAndDontSave;
            m_UberShaderIds = new Dictionary<Guid, int>();
            m_MasterRenderData = new PreviewRenderData
            {
                renderTexture = new RenderTexture(400, 400, 16, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default) { hideFlags = HideFlags.HideAndDontSave }
            };

            foreach (var node in m_Graph.GetNodes<INode>())
                AddPreview(node);
        }

        public PreviewRenderData GetPreview(AbstractMaterialNode node)
        {
            return m_RenderDatas[node.tempId.index];
        }

        void AddPreview(INode node)
        {
            var shaderData = new PreviewShaderData
            {
                node = node
            };
            var renderData = new PreviewRenderData
            {
                shaderData = shaderData,
                renderTexture = new RenderTexture(200, 200, 16, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default) { hideFlags = HideFlags.HideAndDontSave }
            };
//            if (m_RenderDatas[node.tempId.index] != null)
//            {
//                Debug.LogWarningFormat("A preview already exists for {0} {1}", node.name, node.guid);
//                DestroyPreview(node.tempId);
//            }
            Set(m_Identifiers, node.tempId, node.tempId);
            Set(m_RenderDatas, node.tempId, renderData);
            Set(m_DirtyShaders, node.tempId, true);
            Set(m_DirtyPreviews, node.tempId, true);
            node.onModified += OnNodeModified;
            if (node.RequiresTime())
                Set(m_TimeDependentPreviews, node.tempId, true);

            var masterNode = node as IMasterNode;
            if (masterRenderData.shaderData == null && masterNode != null)
            {
                masterRenderData.shaderData = shaderData;
            }
        }

//        void RemovePreview(INode node)
//        {
//            node.onModified -= OnNodeModified;
//            Set(m_RenderDatas, node.tempId, null);
//            Set(m_TimeDependentPreviews, node.tempId, false);
//            Set(m_DirtyPreviews, node.tempId, false);
//            Set(m_DirtyShaders, node.tempId, false);
//            Set(m_Identifiers, node.tempId, default(Identifier));
//
//            if (masterRenderData.shaderData != null && masterRenderData.shaderData.node == node)
//                masterRenderData.shaderData = m_RenderDatas.Select(x => x.shaderData).FirstOrDefault(x => x.node is IMasterNode);
//        }

        void OnNodeModified(INode node, ModificationScope scope)
        {
            if (scope >= ModificationScope.Graph)
                Set(m_DirtyShaders, node.tempId, true);
            else if (scope == ModificationScope.Node)
                Set(m_DirtyPreviews, node.tempId, true);

            Set(m_TimeDependentPreviews, node.tempId, node.RequiresTime());
        }

        Stack<Identifier> m_Wavefront = new Stack<Identifier>();
        List<IEdge> m_Edges = new List<IEdge>();
        List<MaterialSlot> m_Slots = new List<MaterialSlot>();

        void PropagateNodeSet(List<bool> nodeSet, bool forward = true, IEnumerable<Identifier> initialWavefront = null)
        {
            m_Wavefront.Clear();
            if (initialWavefront != null)
            {
                foreach (var id in initialWavefront)
                    m_Wavefront.Push(id);
            }
            else
            {
                for (var i = 0; i < nodeSet.Count; i++)
                {
                    if (nodeSet[i])
                        m_Wavefront.Push(m_Identifiers[i]);
                }
            }
            while (m_Wavefront.Count > 0)
            {
                var index = m_Wavefront.Pop();
                var node = m_Graph.GetNodeFromTempId(index);
                if (node == null)
                    continue;

                // Loop through all nodes that the node feeds into.
                m_Slots.Clear();
                if (forward)
                    node.GetOutputSlots(m_Slots);
                else
                    node.GetInputSlots(m_Slots);
                foreach (var slot in m_Slots)
                {
                    m_Edges.Clear();
                    m_Graph.GetEdges(slot.slotReference, m_Edges);
                    foreach (var edge in m_Edges)
                    {
                        // We look at each node we feed into.
                        var connectedSlot = forward ? edge.inputSlot : edge.outputSlot;
                        var connectedNodeGuid = connectedSlot.nodeGuid;
                        var connectedNode = m_Graph.GetNodeFromGuid(connectedNodeGuid);

                        // If the input node is already in the set of time-dependent nodes, we don't need to process it.
                        if (Get(nodeSet, connectedNode.tempId))
                            continue;

                        // Add the node to the set of time-dependent nodes, and to the wavefront such that we can process the nodes that it feeds into.
                        Set(nodeSet, connectedNode.tempId, true);
                        m_Wavefront.Push(connectedNode.tempId);
                    }
                }
            }
        }

        List<bool> m_PropertyNodes = new List<bool>();
        List<PreviewProperty> m_PreviewProperties = new List<PreviewProperty>();

        public void HandleGraphChanges()
        {
            foreach (var node in m_Graph.removedNodes)
                DestroyPreview(node.tempId);

            foreach (var node in m_Graph.addedNodes)
                AddPreview(node);

            foreach (var edge in m_Graph.removedEdges)
                Set(m_DirtyShaders, m_Graph.GetNodeFromGuid(edge.inputSlot.nodeGuid).tempId, true);

            foreach (var edge in m_Graph.addedEdges)
                Set(m_DirtyShaders, m_Graph.GetNodeFromGuid(edge.inputSlot.nodeGuid).tempId, true);
        }

        List<PreviewRenderData> m_RenderList2D = new List<PreviewRenderData>();
        List<PreviewRenderData> m_RenderList3D = new List<PreviewRenderData>();
        List<bool> m_NodesWith3DPreview = new List<bool>();

        public void RenderPreviews()
        {
            if (m_DirtyShaders.Any(x => x))
            {
                m_NodesWith3DPreview.Clear();
                foreach (var node in m_Graph.GetNodes<AbstractMaterialNode>())
                {
                    if (node.previewMode == PreviewMode.Preview3D)
                        Set(m_NodesWith3DPreview, node.tempId, true);
                }
                PropagateNodeSet(m_NodesWith3DPreview);
                foreach (var renderData in m_RenderDatas)
                    renderData.previewMode = Get(m_NodesWith3DPreview, renderData.shaderData.node.tempId) ? PreviewMode.Preview3D : PreviewMode.Preview2D;
                PropagateNodeSet(m_DirtyShaders);

                var masterNodes = new List<MasterNode>();
                var uberNodes = new List<INode>();
                for (var i = 0; i < m_DirtyPreviews.Count; i++)
                {
                    if (!m_DirtyPreviews[i])
                        continue;
                    var node = m_Graph.GetNodeFromTempId(m_Identifiers[i]);
                    if (node == null)
                        continue;
                    var masterNode = node as MasterNode;
                    if (masterNode != null)
                        masterNodes.Add(masterNode);
                    else
                        uberNodes.Add(node);
                }
                var count = Math.Min(uberNodes.Count, 1) + masterNodes.Count;

                try
                {
                    var sw = new Stopwatch();
                    sw.Start();
                    var i = 0;
                    EditorUtility.DisplayProgressBar("Shader Graph", string.Format("Compiling preview shaders ({0}/{1})", i, count), 0f);
                    foreach (var node in masterNodes)
                    {
                        UpdateShader(node.tempId);
                        i++;
                        EditorUtility.DisplayProgressBar("Shader Graph", string.Format("Compiling preview shaders ({0}/{1})", i, count), 0f);
                    }
                    if (uberNodes.Count > 0)
                    {
                        m_UberShaderIds.Clear();
                        var results = m_Graph.GetUberPreviewShader();
                        m_OutputIdProperty = results.outputIdProperty;
                        m_UberShaderIds = results.ids;
                        ShaderUtil.UpdateShaderAsset(m_UberShader, results.shader);
                        File.WriteAllText(Application.dataPath + "/../UberShader.shader", (results.shader ?? "null").Replace("UnityEngine.MaterialGraph", "Generated"));
                        bool uberShaderHasError = false;
                        if (MaterialGraphAsset.ShaderHasError(m_UberShader))
                        {
                            var errors = MaterialGraphAsset.GetShaderErrors(m_UberShader);
                            var message = new ShaderStringBuilder();
                            message.AppendLine(@"Preview shader for graph has {0} error{1}:", errors.Length, errors.Length != 1 ? "s" : "");
                            foreach (var error in errors)
                            {
                                INode node;
                                try
                                {
                                    node = results.sourceMap.FindNode(error.line);
                                }
                                catch (Exception)
                                {
                                    Debug.LogWarning("ERROR");
                                    continue;
                                }
                                message.AppendLine("{0} in {3} at line {1} (on {2})", error.message, error.line, error.platform, node != null ? string.Format("node {0} ({1})", node.name, node.guid) : "graph");
                                message.AppendLine(error.messageDetails);
                                message.AppendNewLine();
                            }
                            Debug.LogWarning(message.ToString());
                            ShaderUtil.ClearShaderErrors(m_UberShader);
                            ShaderUtil.UpdateShaderAsset(m_UberShader, k_EmptyShader);
                            uberShaderHasError = true;
                        }

                        foreach (var node in uberNodes)
                        {
                            var renderData = GetRenderData(node.tempId);
                            if (renderData == null)
                                continue;
                            var shaderData = renderData.shaderData;
                            shaderData.shader = m_UberShader;
                            shaderData.hasError = uberShaderHasError;
                        }
                        i++;
                        EditorUtility.DisplayProgressBar("Shader Graph", string.Format("Compiling preview shaders ({0}/{1})", i, count), 0f);
                    }
                    sw.Stop();
                    //Debug.LogFormat("Compiled preview shaders in {0} seconds", sw.Elapsed.TotalSeconds);
                }
                finally
                {
                    EditorUtility.ClearProgressBar();
                }

                // Union dirty shaders into dirty previews
                for (var i = 0; i < m_DirtyShaders.Count; i++)
                    Set(m_DirtyPreviews, i, (i < m_DirtyPreviews.Count && m_DirtyPreviews[i]) || m_DirtyShaders[i]);
                m_DirtyShaders.Clear();
            }

            // Union time dependent previews into dirty previews
            for (var i = 0; i < m_TimeDependentPreviews.Count; i++)
                Set(m_DirtyPreviews, i, (i < m_DirtyPreviews.Count && m_DirtyPreviews[i]) || m_TimeDependentPreviews[i]);
            PropagateNodeSet(m_DirtyPreviews);

            // Find nodes we need properties from
            m_PropertyNodes.Clear();
            m_PropertyNodes.AddRange(m_DirtyPreviews);
            PropagateNodeSet(m_PropertyNodes, false);

            // Fill MaterialPropertyBlock
            m_PreviewPropertyBlock.Clear();
            var outputIdName = m_OutputIdProperty != null ? m_OutputIdProperty.referenceName : null;
            m_PreviewPropertyBlock.SetFloat(outputIdName, -1);
            for (var index = 0; index < m_PropertyNodes.Count; index++)
            {
                var node = m_Graph.GetNodeFromTempId(m_Identifiers[index]) as AbstractMaterialNode;
                if (node == null)
                    continue;
                node.CollectPreviewMaterialProperties(m_PreviewProperties);
                foreach (var prop in m_Graph.properties)
                    m_PreviewProperties.Add(prop.GetPreviewMaterialProperty());

                foreach (var previewProperty in m_PreviewProperties)
                {
                    if (previewProperty.propType == PropertyType.Texture && previewProperty.textureValue != null)
                        m_PreviewPropertyBlock.SetTexture(previewProperty.name, previewProperty.textureValue);
                    else if (previewProperty.propType == PropertyType.Cubemap && previewProperty.cubemapValue != null)
                        m_PreviewPropertyBlock.SetTexture(previewProperty.name, previewProperty.cubemapValue);
                    else if (previewProperty.propType == PropertyType.Color)
                        m_PreviewPropertyBlock.SetColor(previewProperty.name, previewProperty.colorValue);
                    else if (previewProperty.propType == PropertyType.Vector2)
                        m_PreviewPropertyBlock.SetVector(previewProperty.name, previewProperty.vector4Value);
                    else if (previewProperty.propType == PropertyType.Vector3)
                        m_PreviewPropertyBlock.SetVector(previewProperty.name, previewProperty.vector4Value);
                    else if (previewProperty.propType == PropertyType.Vector4)
                        m_PreviewPropertyBlock.SetVector(previewProperty.name, previewProperty.vector4Value);
                    else if (previewProperty.propType == PropertyType.Float)
                        m_PreviewPropertyBlock.SetFloat(previewProperty.name, previewProperty.floatValue);
                }
                m_PreviewProperties.Clear();
            }

            for (var i = 0; i < m_DirtyPreviews.Count; i++)
            {
                if (!m_DirtyPreviews[i])
                    continue;
                var renderData = m_RenderDatas[i];
                if (renderData.shaderData.shader == null)
                {
                    renderData.texture = null;
                    continue;
                }
                if (renderData.shaderData.hasError)
                {
                    renderData.texture = m_ErrorTexture;
                    continue;
                }

                if (renderData.previewMode == PreviewMode.Preview2D)
                    m_RenderList2D.Add(renderData);
                else
                    m_RenderList3D.Add(renderData);
            }

            if (masterRenderData.shaderData != null && Get(m_DirtyPreviews, masterRenderData.shaderData.node.tempId))
                m_RenderList3D.Add(masterRenderData);

            m_RenderList3D.Sort((data1, data2) => data1.shaderData.shader.GetInstanceID().CompareTo(data2.shaderData.shader.GetInstanceID()));
            m_RenderList2D.Sort((data1, data2) => data1.shaderData.shader.GetInstanceID().CompareTo(data2.shaderData.shader.GetInstanceID()));

            var time = Time.realtimeSinceStartup;
            EditorUtility.SetCameraAnimateMaterialsTime(m_SceneResources.camera, time);

            m_SceneResources.light0.enabled = true;
            m_SceneResources.light0.intensity = 1.0f;
            m_SceneResources.light0.transform.rotation = Quaternion.Euler(50f, 50f, 0);
            m_SceneResources.light1.enabled = true;
            m_SceneResources.light1.intensity = 1.0f;
            m_SceneResources.camera.clearFlags = CameraClearFlags.Depth;

            // Render 2D previews
            m_SceneResources.camera.transform.position = -Vector3.forward * 2;
            m_SceneResources.camera.transform.rotation = Quaternion.identity;
            m_SceneResources.camera.orthographicSize = 1;
            m_SceneResources.camera.orthographic = true;

            foreach (var renderData in m_RenderList2D)
            {
                int outputId;
                if (m_UberShaderIds.TryGetValue(renderData.shaderData.node.guid, out outputId))
                    m_PreviewPropertyBlock.SetFloat(outputIdName, outputId);
                if (m_PreviewMaterial.shader != renderData.shaderData.shader)
                    m_PreviewMaterial.shader = renderData.shaderData.shader;
                m_SceneResources.camera.targetTexture = renderData.renderTexture;
                var previousRenderTexure = RenderTexture.active;
                RenderTexture.active = renderData.renderTexture;
                GL.Clear(true, true, Color.black);
                Graphics.Blit(Texture2D.whiteTexture, renderData.renderTexture, m_SceneResources.checkerboardMaterial);
                Graphics.DrawMesh(m_SceneResources.quad, Matrix4x4.identity, m_PreviewMaterial, 1, m_SceneResources.camera, 0, m_PreviewPropertyBlock, ShadowCastingMode.Off, false, null, false);
                var previousUseSRP = Unsupported.useScriptableRenderPipeline;
                Unsupported.useScriptableRenderPipeline = false;
                m_SceneResources.camera.Render();
                Unsupported.useScriptableRenderPipeline = previousUseSRP;
                RenderTexture.active = previousRenderTexure;
                renderData.texture = renderData.renderTexture;
            }

            // Render 3D previews
            m_SceneResources.camera.transform.position = -Vector3.forward * 5;
            m_SceneResources.camera.transform.rotation = Quaternion.identity;
            m_SceneResources.camera.orthographic = false;

            foreach (var renderData in m_RenderList3D)
            {
                int outputId;
                if (m_UberShaderIds.TryGetValue(renderData.shaderData.node.guid, out outputId))
                    m_PreviewPropertyBlock.SetFloat(outputIdName, outputId);
                if (m_PreviewMaterial.shader != renderData.shaderData.shader)
                    m_PreviewMaterial.shader = renderData.shaderData.shader;
                m_SceneResources.camera.targetTexture = renderData.renderTexture;
                var previousRenderTexure = RenderTexture.active;
                RenderTexture.active = renderData.renderTexture;
                GL.Clear(true, true, Color.black);
                Graphics.Blit(Texture2D.whiteTexture, renderData.renderTexture, m_SceneResources.checkerboardMaterial);
                var mesh = (renderData == masterRenderData && m_Graph.previewData.serializedMesh.mesh) ? m_Graph.previewData.serializedMesh.mesh :  m_SceneResources.sphere;
                Quaternion rotation = (renderData == masterRenderData) ? m_Graph.previewData.rotation : Quaternion.identity;
                Matrix4x4 previewTransform = Matrix4x4.identity;

                if (renderData == masterRenderData)
                {
                    previewTransform *= Matrix4x4.Rotate(rotation);
                    previewTransform *= Matrix4x4.Scale(Vector3.one * (Vector3.one).magnitude / mesh.bounds.size.magnitude);
                    previewTransform *= Matrix4x4.Translate(-mesh.bounds.center);
                }

                Graphics.DrawMesh(mesh, previewTransform, m_PreviewMaterial, 1, m_SceneResources.camera, 0, m_PreviewPropertyBlock, ShadowCastingMode.Off, false, null, false);

                var previousUseSRP = Unsupported.useScriptableRenderPipeline;
                Unsupported.useScriptableRenderPipeline = renderData.shaderData.node is IMasterNode;
                m_SceneResources.camera.Render();
                Unsupported.useScriptableRenderPipeline = previousUseSRP;
                RenderTexture.active = previousRenderTexure;
                renderData.texture = renderData.renderTexture;
            }

            m_SceneResources.light0.enabled = false;
            m_SceneResources.light1.enabled = false;

            foreach (var renderData in m_RenderList2D)
                renderData.NotifyPreviewChanged();
            foreach (var renderData in m_RenderList3D)
                renderData.NotifyPreviewChanged();

            m_RenderList2D.Clear();
            m_RenderList3D.Clear();
            m_DirtyPreviews.Clear();
        }

        void UpdateShader(Identifier nodeId)
        {
            var node = m_Graph.GetNodeFromTempId(nodeId) as AbstractMaterialNode;
            if (node == null)
                return;
            var renderData = Get(m_RenderDatas, nodeId);
            if (renderData == null || renderData.shaderData == null)
                return;
            var shaderData = renderData.shaderData;

            if (!(node is IMasterNode) && (!node.hasPreview || NodeUtils.FindEffectiveShaderStage(node, true) == ShaderStage.Vertex))
            {
                shaderData.shaderString = null;
            }
            else
            {
                var masterNode = node as IMasterNode;
                if (masterNode != null)
                {
                    List<PropertyCollector.TextureInfo> configuredTextures;
                    shaderData.shaderString = masterNode.GetShader(GenerationMode.Preview, node.name, out configuredTextures);
                }
                else
                    shaderData.shaderString = m_Graph.GetPreviewShader(node).shader;
            }

            File.WriteAllText(Application.dataPath + "/../GeneratedShader.shader", (shaderData.shaderString ?? "null").Replace("UnityEngine.MaterialGraph", "Generated"));

            if (string.IsNullOrEmpty(shaderData.shaderString))
            {
                if (shaderData.shader != null)
                {
                    ShaderUtil.ClearShaderErrors(shaderData.shader);
                    Object.DestroyImmediate(shaderData.shader, true);
                    shaderData.shader = null;
                }
                return;
            }

            if (shaderData.shader == null)
            {
                shaderData.shader = ShaderUtil.CreateShaderAsset(shaderData.shaderString);
                shaderData.shader.hideFlags = HideFlags.HideAndDontSave;
            }
            else
            {
                ShaderUtil.ClearShaderErrors(shaderData.shader);
                ShaderUtil.UpdateShaderAsset(shaderData.shader, shaderData.shaderString);
            }

            // Debug output
            var message = "RecreateShader: " + node.GetVariableNameForNode() + Environment.NewLine + shaderData.shaderString;
            if (MaterialGraphAsset.ShaderHasError(shaderData.shader))
            {
                shaderData.hasError = true;
                Debug.LogWarning(message);
                ShaderUtil.ClearShaderErrors(shaderData.shader);
                Object.DestroyImmediate(shaderData.shader, true);
                shaderData.shader = null;
            }
            else
            {
                shaderData.hasError = false;
            }
        }

        void DestroyPreview(Identifier nodeId)
        {
            var renderData = Get(m_RenderDatas, nodeId);
            if (renderData != null)
            {
                if (renderData.shaderData.shader != null)
                    Object.DestroyImmediate(renderData.shaderData.shader, true);
                if (renderData.renderTexture != null)
                    Object.DestroyImmediate(renderData.renderTexture, true);
                var node = m_Graph.GetNodeFromTempId(nodeId);
                if (node != null)
                    node.onModified -= OnNodeModified;

                Set(m_RenderDatas, nodeId, null);
                Set(m_TimeDependentPreviews, nodeId, false);
                Set(m_DirtyPreviews, nodeId, false);
                Set(m_DirtyShaders, nodeId, false);
                Set(m_Identifiers, nodeId, default(Identifier));

                if (masterRenderData.shaderData != null && masterRenderData.shaderData.node == node)
                    masterRenderData.shaderData = m_RenderDatas.Where(x => x != null && x.shaderData.node is IMasterNode).Select(x => x.shaderData).FirstOrDefault();

                renderData.shaderData.shader = null;
                renderData.renderTexture = null;
                renderData.texture = null;
                renderData.onPreviewChanged = null;
            }
        }

        void ReleaseUnmanagedResources()
        {
            if (m_PreviewMaterial != null)
                Object.DestroyImmediate(m_PreviewMaterial, true);
            m_PreviewMaterial = null;
            if (m_SceneResources != null)
                m_SceneResources.Dispose();
            m_SceneResources = null;
            var previews = m_RenderDatas.ToList();
            foreach (var renderData in previews)
                DestroyPreview(renderData.shaderData.node.tempId);
        }

        public void Dispose()
        {
            ReleaseUnmanagedResources();
            GC.SuppressFinalize(this);
        }

        ~PreviewManager()
        {
            throw new Exception("PreviewManager was not disposed of properly.");
        }

        const string k_EmptyShader = @"
Shader ""hidden/preview""
{
    SubShader
    {
        Tags { ""RenderType""=""Opaque"" }
        LOD 100

        Pass
        {
            CGPROGRAM
            #pragma    vertex    vert
            #pragma    fragment    frag

            #include    ""UnityCG.cginc""

            struct    appdata
            {
                float4 vertex : POSITION;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                return 0;
            }
            ENDCG
        }
    }
}";

        T Get<T>(List<T> list, Identifier id)
        {
            var existingId = Get(m_Identifiers, id.index);
            if (existingId.valid && existingId.version != id.version)
                throw new Exception("Identifier version mismatch");
            return Get(list, id.index);
        }

        static T Get<T>(List<T> list, int index)
        {
            return index < list.Count ? list[index] : default(T);
        }

        void Set<T>(List<T> list, Identifier id, T value)
        {
            var existingId = Get(m_Identifiers, id.index);
            if (existingId.valid && existingId.version != id.version)
                throw new Exception("Identifier version mismatch");
            Set(list, id.index, value);
        }

        static void Set<T>(List<T> list, int index, T value)
        {
            // Make sure the list is large enough for the index
            for (var i = list.Count; i <= index; i++)
                list.Add(default(T));
            list[index] = value;
        }

        PreviewRenderData GetRenderData(Identifier id)
        {
            var value = Get(m_RenderDatas, id);
            if (value != null && value.shaderData.node.tempId.version != id.version)
                throw new Exception("Trying to access render data of a previous version of a node");
            return value;
        }

//        void SetRenderData(Identifier id, PreviewRenderData value)
//        {
//            // Make sure the list is large enough for the index
//            for (var i = m_RenderDatas.Count; i <= id.index; i++)
//                m_RenderDatas.Add(null);
//            if (m_RenderDatas[id.index] != null)
//                throw new Exception("Trying to overwrite existing render data");
//            m_RenderDatas[id.index] = value;
//        }

        void RemoveRenderData(Identifier id)
        {
            var value = Get(m_RenderDatas, id);
            if (value == null)
                throw new Exception("Trying to remove non-existant render data");
            if (value.shaderData.node.tempId.version != id.version)
                throw new Exception("Trying to remove render data for a previous version of a node");
        }
    }

    public delegate void OnPreviewChanged();

    public class PreviewShaderData
    {
        public INode node { get; set; }
        public Shader shader { get; set; }
        public string shaderString { get; set; }
        public bool hasError { get; set; }
    }

    public class PreviewRenderData
    {
        public PreviewShaderData shaderData { get; set; }
        public RenderTexture renderTexture { get; set; }
        public Texture texture { get; set; }
        public PreviewMode previewMode { get; set; }
        public OnPreviewChanged onPreviewChanged;

        public void NotifyPreviewChanged()
        {
            if (onPreviewChanged != null)
                onPreviewChanged();
        }
    }
}
