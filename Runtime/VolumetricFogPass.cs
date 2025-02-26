using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.Universal.Internal;

using Unity.Collections;

using System.Reflection;
using System.Collections.Generic;
using System.Linq;

namespace Sinnwrig.FogVolumes
{
    public class VolumetricFogPass : ScriptableRenderPass
    {    
        // Bundles the integer ID with an RtID
        readonly struct RTPair
        {
            public readonly int propertyId;
            public readonly RenderTargetIdentifier identifier;

            public RTPair(string propertyName)
            {
                propertyId = Shader.PropertyToID(propertyName);
                identifier = new(propertyId);
            }

            public static implicit operator int(RTPair a) => a.propertyId;
            public static implicit operator RenderTargetIdentifier(RTPair a) => a.identifier; 
        }


        // --------------------------------------------------------------------------
        // ------------------------------- Properties -------------------------------
        // --------------------------------------------------------------------------

        #region PROPERTIES

        // Depth render targets
        private static readonly RTPair halfDepth = new RTPair("_HalfDepthTarget");
        private static readonly RTPair quarterDepth = new RTPair("_QuarterDepthTarget");

        // Light render targets
        private static readonly RTPair volumeFog = new RTPair("_VolumeFogTexture");
        private static readonly RTPair halfVolumeFog = new RTPair("_HalfVolumeFogTexture");
        private static readonly RTPair quarterVolumeFog = new RTPair("_QuarterVolumeFogTexture");

        // Low-res temporal rendering target
        private static readonly RTPair temporalTarget = new RTPair("_TemporalTarget");

        // Temporary render target 
        private static readonly RTPair temp = new RTPair("_Temp");


        // Materials
        private static Material bilateralBlur;
        private static Shader fogShader;
        private static Material blitAdd;
        private static Material reprojection;
        

        private readonly VolumetricFogFeature feature;
        private CommandBuffer commandBuffer;


        private VolumetricResolution Resolution
        {
            get
            {
                // Temporal reprojection will force full-res rendering
                if (feature.resolution != VolumetricResolution.Full && feature.temporalRendering)
                    return VolumetricResolution.Full;

                return feature.resolution;
            }   
        }

        

        public VolumetricFogPass(VolumetricFogFeature feature, Shader blur, Shader fog, Shader add, Shader reproj)
        {
            this.feature = feature;

            fogShader = fog;

            if (bilateralBlur == null || bilateralBlur.shader != blur)
                bilateralBlur = new Material(blur);

            if (blitAdd == null || blitAdd.shader != add)
                blitAdd = new Material(add);

            if (reprojection == null || reprojection.shader != reproj)
                reprojection = new Material(reproj);
        }   

        #endregion

        // --------------------------------------------------------------------------
        // ----------------------------   Render Pass   -----------------------------
        // --------------------------------------------------------------------------

        #region RENDERING

        // Allocate temporary textures
        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData data)
        {
            RenderTextureDescriptor descriptor = data.cameraData.cameraTargetDescriptor;

            int width = descriptor.width;
            int height = descriptor.height;
            var colorFormat = RenderTextureFormat.ARGBHalf;
            var depthFormat = RenderTextureFormat.RFloat;

            cmd.GetTemporaryRT(volumeFog, width, height, 0, FilterMode.Point, colorFormat);

            if (feature.temporalRendering)
                cmd.GetTemporaryRT(temporalTarget, width / TemporalKernelSize, height / TemporalKernelSize, 0, FilterMode.Point, colorFormat);

            if (Resolution == VolumetricResolution.Half)
                cmd.GetTemporaryRT(halfVolumeFog, width / 2, height / 2, 0, FilterMode.Bilinear, colorFormat);

            // Half/Quarter res both need half-res depth buffer for downsampling
            if (Resolution == VolumetricResolution.Half || Resolution == VolumetricResolution.Quarter)
                cmd.GetTemporaryRT(halfDepth, width / 2, height / 2, 0, FilterMode.Point, depthFormat);

            if (Resolution == VolumetricResolution.Quarter)
            {
                cmd.GetTemporaryRT(quarterVolumeFog, width / 4, height / 4, 0, FilterMode.Bilinear, colorFormat);
                cmd.GetTemporaryRT(quarterDepth, width / 4, height / 4, 0, FilterMode.Point, depthFormat);
            }
        }


        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            var fogVolumes = SetupVolumes(ref renderingData);

            if (fogVolumes.Count == 0)
                return;

            var renderer = renderingData.cameraData.renderer;
            var descriptor = renderingData.cameraData.cameraTargetDescriptor;

            #if UNITY_2022_1_OR_NEWER
                var cameraColor = renderer.cameraColorTargetHandle;
            #else
                var cameraColor = renderer.cameraColorTarget;
            #endif

            commandBuffer = CommandBufferPool.Get("Volumetric Fog Pass");

            DownsampleDepthBuffer();

            SetupFogRenderTarget(descriptor.width, descriptor.height);
            DrawVolumes(fogVolumes, ref renderingData);

            ReprojectBuffer(ref renderingData);
            BilateralBlur(descriptor.width, descriptor.height);

            BlendFog(cameraColor, ref renderingData);

            context.ExecuteCommandBuffer(commandBuffer);
            CommandBufferPool.Release(commandBuffer);
        }


        // Release temporary textures
        public override void OnCameraCleanup(CommandBuffer cmd)
        {
            cmd.ReleaseTemporaryRT(volumeFog);

            if (feature.temporalRendering)
                cmd.ReleaseTemporaryRT(temporalTarget);

            if (Resolution == VolumetricResolution.Half)
                cmd.ReleaseTemporaryRT(halfVolumeFog);

            if (Resolution == VolumetricResolution.Half || Resolution == VolumetricResolution.Quarter)
                cmd.ReleaseTemporaryRT(halfDepth);

            if (Resolution == VolumetricResolution.Quarter)
            {
                cmd.ReleaseTemporaryRT(quarterVolumeFog);
                cmd.ReleaseTemporaryRT(quarterDepth);
            }
        }


        // Additively blend render result with the scene
        private void BlendFog(RenderTargetIdentifier target, ref RenderingData data)
        {
            commandBuffer.GetTemporaryRT(temp, data.cameraData.cameraTargetDescriptor);
            commandBuffer.Blit(target, temp);

            commandBuffer.SetGlobalTexture("_BlitSource", temp);
            commandBuffer.SetGlobalTexture("_BlitAdd", volumeFog);

            // Use blit add kernel to merge target color and the light buffer
            TargetBlit(commandBuffer, target, blitAdd, 0);
    
            commandBuffer.ReleaseTemporaryRT(temp);
        }


        // Equivalent to normal Blit, but uses a custom quad instead of stupid idiot fullscreen triangle I couldn't get working.
        private static void TargetBlit(CommandBuffer cmd, RenderTargetIdentifier destination, Material material, int pass)
        {
            cmd.SetRenderTarget(destination);
            cmd.DrawMesh(MeshUtility.FullscreenQuad, Matrix4x4.identity, material, 0, pass);
        }

        #endregion

        // --------------------------------------------------------------------------
        // --------------------------   Volume Rendering   --------------------------
        // --------------------------------------------------------------------------

        #region VOLUMES

        private static readonly HashSet<FogVolume> activeVolumes = new();

        /// <summary>
        /// Add a volume to the tracked volume set. Does not track duplicates.
        /// </summary>
        /// <param name="volume">The volume to track.</param>
        public static void AddVolume(FogVolume volume) => activeVolumes.Add(volume);

        /// <summary>
        /// Remove a volume from the tracked volume set.
        /// </summary>
        /// <param name="volume">The volume to stop tracking.</param>
        public static void RemoveVolume(FogVolume volume) => activeVolumes.Remove(volume);

        /// <summary>
        /// The list of active volumes in the scene.
        /// </summary>
        public static IEnumerable<FogVolume> ActiveVolumes => activeVolumes;


        private static readonly FieldInfo shadowPassField = typeof(UniversalRenderer).GetField("m_AdditionalLightsShadowCasterPass", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly Plane[] cullingPlanes = new Plane[6];


	    private bool CullSphere(Vector3 pos, float radius) 
	    {
	    	// Cull spherical bounds, ignoring camera far plane at index 5
	    	for (int i = 0; i < cullingPlanes.Length; i++) 
	    	{
	    		float distance = cullingPlanes[i].GetDistanceToPoint(pos);

	    		if (distance < 0 && Mathf.Abs(distance) > radius) 
	    			return true;
	    	}

	    	return false;
	    }

        // Package visible lights and initialize lighting data
        private List<NativeLight> SetupLights(ref RenderingData renderingData)
        {
            // Curse you unity internals
            var shadowPass = shadowPassField.GetValue(renderingData.cameraData.renderer) as AdditionalLightsShadowCasterPass;

            LightData lightData = renderingData.lightData;
            NativeArray<VisibleLight> visibleLights = lightData.visibleLights;

            List<NativeLight> initializedLights = new();

            Vector3 cameraPosition = renderingData.cameraData.camera.transform.position;

            for (int i = 0; i < visibleLights.Length; i++)
            {
                var visibleLight = visibleLights[i];

                bool isDirectional = visibleLight.lightType == LightType.Directional;
                bool isMain = i == lightData.mainLightIndex;

                Vector3 position = visibleLight.localToWorldMatrix.GetColumn(3);

                if (!isDirectional && CullSphere(position, visibleLight.range))
                    continue;

                int shadowIndex = shadowPass.GetShadowLightIndexFromLightIndex(i);

                NativeLight light = new()
                {
                    isDirectional = isDirectional,
                    shadowIndex = isMain ? -1 : shadowIndex, // Main light gets special treatment
                    range = visibleLight.range,
                    layer = visibleLight.light.gameObject.layer,
                    cameraDistance = isDirectional ? 0 : (cameraPosition - position).sqrMagnitude
                };

                // Set up light properties
                UniversalRenderPipeline.InitializeLightConstants_Common(visibleLights, i,
                    out light.position,
                    out light.color, 
                    out light.attenuation,
                    out light.spotDirection,
                    out _
                );

                initializedLights.Add(light);
            }

            initializedLights.Sort((a, b) => a.cameraDistance.CompareTo(b.cameraDistance));

            return initializedLights;
        }


        // Cull active volumes and package only the visible ones
        private List<FogVolume> SetupVolumes(ref RenderingData renderingData)
        {
            Camera camera = renderingData.cameraData.camera;
            GeometryUtility.CalculateFrustumPlanes(camera, cullingPlanes);
            Vector3 camPos = camera.transform.position;

            List<FogVolume> fogVolumes = new();

            foreach (FogVolume volume in activeVolumes)
            {
                if (!volume.CullVolume(camPos, cullingPlanes))
                    fogVolumes.Add(volume);
            }

            return fogVolumes;
        }


        // Draw all of the volumes into the active render target
        private void DrawVolumes(List<FogVolume> volumes, ref RenderingData renderingData)
        {
            List<NativeLight> lights = SetupLights(ref renderingData);

            int perObjectLightCount = renderingData.lightData.maxPerObjectAdditionalLightsCount;

            for (int i = 0; i < volumes.Count; i++)
                volumes[i].DrawVolume(ref renderingData, commandBuffer, fogShader, lights, perObjectLightCount);
        }

        #endregion

        // --------------------------------------------------------------------------
        // --------------------------   Upscaling & Blur   --------------------------
        // --------------------------------------------------------------------------

        #region UPSCALING

        // Blurs the active resolution texture, upscaling to full resolution if needed
        private void BilateralBlur(int width, int height)
        {
            Resolution.SetResolutionKeyword(commandBuffer);

            // Blur quarter-res texture and upsample to full res
            if (Resolution == VolumetricResolution.Quarter)
            {
                BilateralBlur(quarterVolumeFog, quarterDepth, width / 4, height / 4); 
                Upsample(quarterVolumeFog, quarterDepth, volumeFog);
                return;
            }

            // Blur half-res texture and upsample to full res
            if (Resolution == VolumetricResolution.Half)
            {
                BilateralBlur(halfVolumeFog, halfDepth, width / 2, height / 2);
                Upsample(halfVolumeFog, halfDepth, volumeFog);
                return;
            }

            if (feature.disableBlur)
                return;

            // Blur full-scale texture 
            BilateralBlur(volumeFog, null, width, height);
        }


        // Blurs source texture with provided depth texture- uses camera depth if null
        private void BilateralBlur(RenderTargetIdentifier source, RenderTargetIdentifier? depthBuffer, int sourceWidth, int sourceHeight)
        {
            commandBuffer.GetTemporaryRT(temp, sourceWidth, sourceHeight, 0, FilterMode.Bilinear, RenderTextureFormat.ARGBHalf);

            SetDepthTexture("_DepthTexture", depthBuffer);

            // Horizontal blur
            commandBuffer.SetGlobalTexture("_BlurSource", source);
            TargetBlit(commandBuffer, temp, bilateralBlur, 0);

            // Vertical blur
            commandBuffer.SetGlobalTexture("_BlurSource", temp);
            TargetBlit(commandBuffer, source, bilateralBlur, 1);

            commandBuffer.ReleaseTemporaryRT(temp);
        }


        // Downsamples depth texture to active resolution buffer
        private void DownsampleDepthBuffer()
        {
            if (Resolution == VolumetricResolution.Half || Resolution == VolumetricResolution.Quarter)
            {
                SetDepthTexture("_DownsampleSource", null);
                TargetBlit(commandBuffer, halfDepth, bilateralBlur, 2);
            }

            if (Resolution == VolumetricResolution.Quarter)
            {
                SetDepthTexture("_DownsampleSource", halfDepth);
                TargetBlit(commandBuffer, quarterDepth, bilateralBlur, 2);
            }
        }


        // Perform depth-aware upsampling to the destination
        private void Upsample(RenderTargetIdentifier sourceColor, RenderTargetIdentifier sourceDepth, RenderTargetIdentifier destination)
        {
            commandBuffer.SetGlobalTexture("_DownsampleColor", sourceColor);
            commandBuffer.SetGlobalTexture("_DownsampleDepth", sourceDepth);

            TargetBlit(commandBuffer, destination, bilateralBlur, 3);
        }


        private void SetDepthTexture(string textureId, RenderTargetIdentifier? depth)
        {
            if (depth.HasValue)
            {
                commandBuffer.SetGlobalTexture(textureId, depth.Value);
            }
            else
            {
                #if UNITY_2022_1_OR_NEWER
                   commandBuffer.SetGlobalTexture(textureId, depthAttachmentHandle);
                #else
                    commandBuffer.SetGlobalTexture(textureId, depthAttachment);
                #endif
            }
        }

        #endregion

        // --------------------------------------------------------------------------
        // -------------------------   Temporal Rendering   -------------------------
        // --------------------------------------------------------------------------

        #region TEMPORAL

        private static readonly GlobalKeyword temporalKeyword = GlobalKeyword.Create("TEMPORAL_RENDERING_ENABLED");

        private int TemporalKernelSize => System.Math.Max(2, feature.temporalResolution);
        private int temporalPassIndex;

        // Temporal Reprojection Target-
        // NOTE: only a RenderTexture seems to preserve information between frames on my device, otherwise I'd use an RTHandle or RenderTargetIdentifier
        private RenderTexture temporalBuffer;


        private static System.Random random = new();


        private void SetTemporalConstants()
        {
            temporalPassIndex = (temporalPassIndex + 1) % (TemporalKernelSize * TemporalKernelSize);

            commandBuffer.SetGlobalVector("_TileSize", new Vector2(TemporalKernelSize, TemporalKernelSize));
            commandBuffer.SetGlobalVector("_PassOffset", new Vector2(random.Next(0, TemporalKernelSize), random.Next(0, TemporalKernelSize)));
        }


        // Set the volumetric fog render target
        // Clear the target if there is nothing to reproject
        // Otherwise, reproject the previous frame
        private void SetupFogRenderTarget(int width, int height)
        {
            commandBuffer.SetKeyword(temporalKeyword, feature.temporalRendering);

            if (Resolution == VolumetricResolution.Quarter)
                commandBuffer.SetRenderTarget(quarterVolumeFog);
            else if (Resolution == VolumetricResolution.Half)
                commandBuffer.SetRenderTarget(halfVolumeFog);
            else if (feature.temporalRendering)
                commandBuffer.SetRenderTarget(temporalTarget);
            else
                commandBuffer.SetRenderTarget(volumeFog);

            commandBuffer.ClearRenderTarget(true, true, new Color(0, 0, 0, 0));

            if (feature.temporalRendering)
            {
                SetTemporalConstants();
                commandBuffer.SetGlobalVector("_TemporalRenderSize", new Vector2(width, height) / TemporalKernelSize);
            }
        }


        private void ReprojectBuffer(ref RenderingData data)
        {
            if (!feature.temporalRendering)
                return;

            RenderTextureDescriptor descriptor = data.cameraData.cameraTargetDescriptor;
            int width = descriptor.width;
            int height = descriptor.height;
            descriptor.colorFormat = RenderTextureFormat.ARGBHalf;

            if (temporalBuffer == null || !temporalBuffer.IsCreated() || temporalBuffer.width != width || temporalBuffer.height != height)
            {
                if (temporalBuffer != null && temporalBuffer.IsCreated())
                    temporalBuffer.Release();

                temporalBuffer = new RenderTexture(descriptor);
                temporalBuffer.Create();
            }

            commandBuffer.SetGlobalTexture("_TemporalBuffer", temporalBuffer);
            commandBuffer.SetGlobalTexture("_TemporalTarget", temporalTarget);
            commandBuffer.SetGlobalFloat("_MotionInfluence", data.cameraData.isSceneViewCamera ? 0 : 1);

            TargetBlit(commandBuffer, volumeFog, reprojection, 0);

            commandBuffer.CopyTexture(volumeFog, 0, 0, temporalBuffer, 0, 0);
        }


        public void Dispose()
        {
            if (temporalBuffer != null && temporalBuffer.IsCreated())
                temporalBuffer.Release();
        }

        #endregion
    
    }
}